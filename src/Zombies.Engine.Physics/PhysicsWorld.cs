using System.Numerics;
using JoltPhysicsSharp;
using Zombies.Engine.Net;
using Zombies.Engine.Voxel;

namespace Zombies.Engine.Physics;

/// <summary>
/// The Jolt world the game runs on: one static heightfield body per loaded chunk, and one <see cref="CharacterVirtual"/>
/// per player. Terrain is a heightfield rather than a triangle mesh because a heightfield is far cheaper to build and to
/// query, and the voxel world is a heightmap everywhere the player can walk.
/// </summary>
public sealed class PhysicsWorld : IDisposable, IPlayerCollisionSource
{
    /// <summary>Object layer for terrain. Players are on <see cref="PlayerLayer"/>.</summary>
    private const uint TerrainLayer = 0;

    private const uint PlayerLayer = 1;

    private const int LayerCount = 2;

    private readonly PhysicsSystem _system;
    private readonly JobSystemThreadPool _jobs;
    private readonly ObjectLayerPairFilterTable _objectLayerPairFilter;
    private readonly BroadPhaseLayerInterfaceTable _broadPhaseLayers;
    private readonly ObjectVsBroadPhaseLayerFilterTable _objectVsBroadPhase;
    private readonly Dictionary<ChunkCoord, BodyID> _terrain = [];
    private readonly List<BodyID> _terrainOrder = [];
    private readonly List<Shape> _shapes = [];
    private readonly List<CharacterVirtual> _characters = [];
    private readonly float[] _samples = new float[ChunkConstants.Size * ChunkConstants.Size];
    private bool _disposed;

    public PhysicsWorld()
    {
        if (!Foundation.Init(false))
        {
            throw new InvalidOperationException("Jolt Physics could not start.");
        }

        _objectLayerPairFilter = new ObjectLayerPairFilterTable(LayerCount);
        _objectLayerPairFilter.EnableCollision(TerrainLayer, PlayerLayer);
        _objectLayerPairFilter.EnableCollision(PlayerLayer, PlayerLayer);

        _broadPhaseLayers = new BroadPhaseLayerInterfaceTable(LayerCount, LayerCount);
        _broadPhaseLayers.MapObjectToBroadPhaseLayer(TerrainLayer, new BroadPhaseLayer(0));
        _broadPhaseLayers.MapObjectToBroadPhaseLayer(PlayerLayer, new BroadPhaseLayer(1));

        _objectVsBroadPhase = new ObjectVsBroadPhaseLayerFilterTable(_broadPhaseLayers, LayerCount, _objectLayerPairFilter, LayerCount);

        _system = new PhysicsSystem(new PhysicsSystemSettings
        {
            MaxBodies = 4096,
            NumBodyMutexes = 0,
            MaxBodyPairs = 4096,
            MaxContactConstraints = 4096,
            ObjectLayerPairFilter = _objectLayerPairFilter,
            BroadPhaseLayerInterface = _broadPhaseLayers,
            ObjectVsBroadPhaseLayerFilter = _objectVsBroadPhase,
        });
        _system.Gravity = new Vector3(0, -PlayerMovement.Gravity, 0);
        _jobs = new JobSystemThreadPool();
    }

    /// <summary>Chunks whose terrain is in the physics world.</summary>
    public int TerrainChunkCount => _terrain.Count;

    /// <summary>Players with a character in the physics world.</summary>
    public int CharacterCount => _characters.Count;

    /// <summary>Adds a chunk's terrain as a static heightfield. Adding a chunk that is already there does nothing.</summary>
    public void AddTerrain(Chunk chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_terrain.ContainsKey(chunk.Coord))
        {
            return;
        }

        // A heightfield samples the top of each column, so a column with nothing in it gets the lowest possible height.
        for (var z = 0; z < ChunkConstants.Size; z++)
        {
            for (var x = 0; x < ChunkConstants.Size; x++)
            {
                var top = chunk.TopOpaque(x, z);
                _samples[(z * ChunkConstants.Size) + x] = top < 0 ? -1f : top + 1f;
            }
        }

        var settings = new HeightFieldShapeSettings(
            _samples,
            new Vector3(chunk.Coord.WorldX, 0, chunk.Coord.WorldZ),
            Vector3.One,
            ChunkConstants.Size);
        var shape = settings.Create();
        _shapes.Add(shape);

        var body = new BodyCreationSettings(shape, Vector3.Zero, Quaternion.Identity, MotionType.Static, new ObjectLayer(TerrainLayer));
        var id = _system.BodyInterface.CreateAndAddBody(body, Activation.DontActivate);
        _terrain[chunk.Coord] = id;
        _terrainOrder.Add(id);
    }

    /// <summary>Removes a chunk's terrain. Players standing on it fall.</summary>
    public bool RemoveTerrain(ChunkCoord coord)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_terrain.Remove(coord, out var id))
        {
            return false;
        }

        _terrainOrder.Remove(id);
        _system.BodyInterface.RemoveAndDestroyBody(id);
        return true;
    }

    /// <summary>Creates a character for a player at the given feet position.</summary>
    public IPlayerCollision Create(Vector3 position, float height)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var capsule = new CapsuleShape(MathF.Max(0.05f, (height / 2f) - PlayerMovement.Radius), PlayerMovement.Radius);
        _shapes.Add(capsule);

        var settings = new CharacterVirtualSettings
        {
            Shape = capsule,
            Up = Vector3.UnitY,
            MaxSlopeAngle = 50f * MathF.PI / 180f,
            CharacterPadding = 0.02f,
            PenetrationRecoverySpeed = 0.05f,
            EnhancedInternalEdgeRemoval = true,
            PredictiveContactDistance = 0.1f,
            MaxStrength = 100f,
            Mass = 80f,
            SupportingVolume = new Plane(Vector3.UnitY, -(height / 2f)),
        };

        var character = new CharacterVirtual(settings, position + new Vector3(0, height / 2f, 0), Quaternion.Identity, 0, _system);
        _characters.Add(character);
        return new JoltCharacterCollision(this, character);
    }

    /// <summary>Steps the physics world. Call once per simulation tick, after every character has moved.</summary>
    public void Step(float seconds)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _system.Update(seconds, collisionSteps: 1, _jobs);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var character in _characters)
        {
            character.Dispose();
        }

        _characters.Clear();
        foreach (var id in _terrainOrder)
        {
            _system.BodyInterface.RemoveAndDestroyBody(id);
        }

        _terrain.Clear();
        _terrainOrder.Clear();
        foreach (var shape in _shapes)
        {
            shape.Dispose();
        }

        _shapes.Clear();
        _jobs.Dispose();
        _system.Dispose();
        _objectVsBroadPhase.Dispose();
        _broadPhaseLayers.Dispose();
        _objectLayerPairFilter.Dispose();
        Foundation.Shutdown();
    }

    /// <summary>One player's Jolt character, driven by <see cref="PlayerMovement"/>.</summary>
    private sealed class JoltCharacterCollision(PhysicsWorld world, CharacterVirtual character) : IPlayerCollision
    {
        private readonly ExtendedUpdateSettings _extended = new()
        {
            StickToFloorStepDown = new Vector3(0, -0.5f, 0),
            WalkStairsStepUp = new Vector3(0, PlayerMovement.StepHeight, 0),
            WalkStairsMinStepForward = 0.02f,
            WalkStairsStepForwardTest = 0.15f,
            WalkStairsCosAngleForwardContact = MathF.Cos(75f * MathF.PI / 180f),
            WalkStairsStepDownExtra = Vector3.Zero,
        };

        public PlayerMoveResult Move(Vector3 position, Vector3 delta, float height, float seconds)
        {
            // Jolt's character position is the centre of its shape; the game's is the feet.
            var centre = position + new Vector3(0, height / 2f, 0);

            // Only move the character directly for a teleport or a correction. Setting the position every tick fights the
            // character's own contact resolution and makes it creep along the ground.
            if (Vector3.DistanceSquared(character.Position, centre) > 0.01f)
            {
                character.Position = centre;
            }

            character.LinearVelocity = delta / seconds;
            character.ExtendedUpdate(seconds, _extended, new ObjectLayer(PlayerLayer), world._system, null!, null!);
            return new PlayerMoveResult(character.Position - new Vector3(0, height / 2f, 0), character.GroundState == GroundState.OnGround);
        }
    }
}
