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
public sealed partial class PhysicsWorld : IDisposable, IPlayerCollisionSource
{
    /// <summary>Object layer for terrain. Players are on <see cref="PlayerLayer"/>.</summary>
    private const uint TerrainLayer = 0;

    private const uint PlayerLayer = 1;

    /// <summary>Object layer for ragdoll bodies. They hit terrain and each other, never players, so a corpse cannot push anyone around.</summary>
    private const uint DebrisLayer = 2;

    private const int LayerCount = 3;

    /// <summary>One body per chunk of ground, one per tree trunk, and the ragdolls; a body Jolt has no room for is silently invalid.</summary>
    private const int MaxBodyCount = 32768;

    private readonly PhysicsSystem _system;
    private readonly JobSystemThreadPool _jobs;
    private readonly ObjectLayerPairFilterTable _objectLayerPairFilter;
    private readonly BroadPhaseLayerInterfaceTable _broadPhaseLayers;
    private readonly ObjectVsBroadPhaseLayerFilterTable _objectVsBroadPhase;
    private readonly Dictionary<ChunkCoord, BodyID> _terrain = [];

    /// <summary>The solid box of every tree trunk in a chunk, which the ground heightfield leaves out.</summary>
    private readonly Dictionary<ChunkCoord, List<BodyID>> _trunks = [];

    /// <summary>The seam-inclusive sample grid of every chunk's heightfield, so a character found under the surface can be put back on it.</summary>
    private readonly Dictionary<ChunkCoord, float[]> _grids = [];

    /// <summary>How far under the terrain a character may be before it counts as having fallen through it.</summary>
    private const float UnderTerrainMargin = 1.5f;
    private readonly List<BodyID> _terrainOrder = [];
    private readonly List<Shape> _shapes = [];
    private readonly List<CharacterVirtual> _characters = [];
    /// <summary>
    /// Samples along one side of a chunk's heightfield. A heightfield spans one unit fewer than its sample count, so a chunk's own
    /// 16 columns would leave a one-block crack along every chunk edge. Jolt wants a power of two, so the field is 32 wide: the
    /// first 17 samples cover the chunk and the seam, and the rest have no collision.
    /// </summary>
    private const int FieldSize = ChunkConstants.Size * 2;

    private const float NoCollision = float.MaxValue;

    private readonly float[] _samples = new float[FieldSize * FieldSize];
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
        _objectLayerPairFilter.EnableCollision(DebrisLayer, TerrainLayer);
        _objectLayerPairFilter.EnableCollision(DebrisLayer, DebrisLayer);

        _broadPhaseLayers = new BroadPhaseLayerInterfaceTable(LayerCount, LayerCount);
        _broadPhaseLayers.MapObjectToBroadPhaseLayer(TerrainLayer, new BroadPhaseLayer(0));
        _broadPhaseLayers.MapObjectToBroadPhaseLayer(PlayerLayer, new BroadPhaseLayer(1));
        _broadPhaseLayers.MapObjectToBroadPhaseLayer(DebrisLayer, new BroadPhaseLayer(1));

        _objectVsBroadPhase = new ObjectVsBroadPhaseLayerFilterTable(_broadPhaseLayers, LayerCount, _objectLayerPairFilter, LayerCount);

        _system = new PhysicsSystem(new PhysicsSystemSettings
        {
            MaxBodies = MaxBodyCount,
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

        // A heightfield samples the top of each column, so a column with nothing in it gets the lowest possible height. The
        // seam sample past the last column repeats it, so the crack to the next chunk is filled; the next chunk's own field
        // takes over from there.
        Array.Fill(_samples, NoCollision);
        for (var z = 0; z <= ChunkConstants.Size; z++)
        {
            for (var x = 0; x <= ChunkConstants.Size; x++)
            {
                var top = GroundTop(chunk, Math.Min(x, ChunkConstants.Size - 1), Math.Min(z, ChunkConstants.Size - 1));
                _samples[(z * FieldSize) + x] = top < 0 ? -1f : top + 1f;
            }
        }

        var grid = new float[(ChunkConstants.Size + 1) * (ChunkConstants.Size + 1)];
        for (var z = 0; z <= ChunkConstants.Size; z++)
        {
            for (var x = 0; x <= ChunkConstants.Size; x++)
            {
                grid[(z * (ChunkConstants.Size + 1)) + x] = _samples[(z * FieldSize) + x];
            }
        }

        _grids[chunk.Coord] = grid;

        var settings = new HeightFieldShapeSettings(
            _samples,
            new Vector3(chunk.Coord.WorldX, 0, chunk.Coord.WorldZ),
            Vector3.One,
            FieldSize);
        var shape = settings.Create();
        _shapes.Add(shape);

        var body = new BodyCreationSettings(shape, Vector3.Zero, Quaternion.Identity, MotionType.Static, new ObjectLayer(TerrainLayer));
        var id = _system.BodyInterface.CreateAndAddBody(body, Activation.DontActivate);
        _terrain[chunk.Coord] = id;
        _terrainOrder.Add(id);
        AddTrunks(chunk);
    }

    /// <summary>
    /// Y of the highest block in a column that is ground. Trees are left out: a canopy is opaque but not solid, and a heightfield
    /// has no overhangs, so counting it would turn every tree into a slab with no floor under it. The air between a canopy and the
    /// ground is skipped too.
    /// </summary>
    private static int GroundTop(Chunk chunk, int x, int z)
    {
        var y = chunk.TopOpaque(x, z);
        while (y >= 0 && chunk.Get(x, y, z) is Blocks.Air or Blocks.Leaves or Blocks.Log)
        {
            y--;
        }

        return y;
    }

    /// <summary>True for the blocks of a tree. They are solid to the body, but the ground heightfield leaves them out.</summary>
    private static bool IsTree(ushort block) => block is Blocks.Log or Blocks.Leaves;

    /// <summary>
    /// Gives every vertical run of logs and leaves in a chunk a solid box, all in one compound body, so a trunk is a wall and a
    /// canopy blocks the player's body without the heightfield turning it into a slab with no floor under it.
    /// </summary>
    private void AddTrunks(Chunk chunk)
    {
        StaticCompoundShapeSettings? compound = null;
        for (var z = 0; z < ChunkConstants.Size; z++)
        {
            for (var x = 0; x < ChunkConstants.Size; x++)
            {
                var y = chunk.TopOpaque(x, z);
                while (y >= 0)
                {
                    if (!IsTree(chunk.Get(x, y, z)))
                    {
                        y--;
                        continue;
                    }

                    var top = y;
                    while (y >= 0 && IsTree(chunk.Get(x, y, z)))
                    {
                        y--;
                    }

                    var height = top - y;
                    compound ??= new StaticCompoundShapeSettings();
                    compound.AddShape(
                        new Vector3(x + 0.5f, y + 1 + (height / 2f), z + 0.5f),
                        Quaternion.Identity,
                        new BoxShapeSettings(new Vector3(0.5f, height / 2f, 0.5f)));
                }
            }
        }

        if (compound is null)
        {
            return;
        }

        var shape = compound.Create();
        _shapes.Add(shape);
        var settings = new BodyCreationSettings(shape, new Vector3(chunk.Coord.WorldX, 0, chunk.Coord.WorldZ), Quaternion.Identity, MotionType.Static, new ObjectLayer(TerrainLayer));
        _trunks[chunk.Coord] = [_system.BodyInterface.CreateAndAddBody(settings, Activation.DontActivate)];
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
        _grids.Remove(coord);
        _system.BodyInterface.RemoveAndDestroyBody(id);
        if (_trunks.Remove(coord, out var trunks))
        {
            foreach (var trunk in trunks)
            {
                _system.BodyInterface.RemoveAndDestroyBody(trunk);
            }
        }

        return true;
    }

    /// <summary>
    /// The lowest and highest terrain height of the cell under a world position, from the loaded heightfields. False when the
    /// chunk is not loaded or the cell has no ground, so nothing is known about where the surface is.
    /// </summary>
    private bool TryGetSurface(float x, float z, out float lowest, out float highest)
    {
        lowest = highest = 0f;
        var worldX = (int)MathF.Floor(x);
        var worldZ = (int)MathF.Floor(z);
        var coord = new ChunkCoord(worldX >> 4, worldZ >> 4);
        if (!_grids.TryGetValue(coord, out var grid))
        {
            return false;
        }

        var lx = worldX & (ChunkConstants.Size - 1);
        var lz = worldZ & (ChunkConstants.Size - 1);
        const int Stride = ChunkConstants.Size + 1;
        Span<float> corners = [grid[(lz * Stride) + lx], grid[(lz * Stride) + lx + 1], grid[((lz + 1) * Stride) + lx], grid[((lz + 1) * Stride) + lx + 1]];
        lowest = float.MaxValue;
        highest = float.MinValue;
        foreach (var corner in corners)
        {
            if (corner < 0f)
            {
                return false;
            }

            lowest = MathF.Min(lowest, corner);
            highest = MathF.Max(highest, corner);
        }

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
        ClearRagdolls();
        foreach (var id in _terrainOrder)
        {
            _system.BodyInterface.RemoveAndDestroyBody(id);
        }

        foreach (var trunk in _trunks.Values.SelectMany(bodies => bodies))
        {
            _system.BodyInterface.RemoveAndDestroyBody(trunk);
        }

        _trunks.Clear();
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
            var feet = character.Position - new Vector3(0, height / 2f, 0);

            // A heightfield is a surface with no thickness, so a character pushed through it (a reconcile teleport beside a steep
            // step, say) keeps falling. The world is a heightmap wherever the player can walk, so a character well under the
            // terrain has fallen through it: stand it back on top.
            if (world.TryGetSurface(feet.X, feet.Z, out var lowest, out var highest) && feet.Y < lowest - UnderTerrainMargin)
            {
                feet = new Vector3(feet.X, highest + 0.05f, feet.Z);
                character.Position = feet + new Vector3(0, height / 2f, 0);
                character.LinearVelocity = Vector3.Zero;
                return new PlayerMoveResult(feet, true);
            }

            return new PlayerMoveResult(feet, character.GroundState == GroundState.OnGround);
        }
    }
}
