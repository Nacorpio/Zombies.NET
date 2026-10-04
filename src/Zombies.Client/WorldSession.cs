using System.Numerics;
using Zombies.Domain.Mods;
using Zombies.Domain.World;
using Zombies.Engine.Core;
using Zombies.Engine.Core.Modding;
using Zombies.Engine.Net;
using Zombies.Engine.Physics;
using Zombies.Engine.Platform;
using Zombies.Engine.Render;
using Zombies.Engine.Voxel;

namespace Zombies.Client;

/// <summary>Everything the client needs to show the world: the loaded mods, the embedded Server, the generator, worker threads, and the manager that streams chunks to the GPU.</summary>
internal sealed class WorldSession : IDisposable
{
    private readonly ChunkPipeline _pipeline;
    private readonly PhysicsWorld _physics;
    private readonly HashSet<ChunkCoord> _physicsChunks = [];

    public WorldSession(ClientOptions options, IWorldRenderer renderer)
    {
        var mods = ModLoader.Load(DirectoryModSource.Read(options.ModsDirectory ?? DirectoryModSource.Find(AppContext.BaseDirectory)));
        if (!mods.IsSuccess)
        {
            throw new InvalidOperationException("The mods could not be loaded:" + Environment.NewLine + string.Join(Environment.NewLine, mods.Errors));
        }

        // Solo play: join an embedded Server and take the world seed from it, as a client of a dedicated server would.
        Solo = new EmbeddedServer(new ServerOptions(GameIdentity.From(mods, WorldGenerator.GeneratorVersion), options.Seed), string.IsNullOrEmpty(Environment.UserName) ? "player" : Environment.UserName);

        var biomes = new BiomeCatalog(mods.Registry.OfKind("biome").Select(d => BiomeJson.Parse(d.Json)));
        _pipeline = new ChunkPipeline(new WorldGenerator(Solo.Client.WorldSeed, biomes));
        Manager = new ChunkRenderManager(_pipeline, renderer, new ChunkStreamer(options.ViewDistance));
        ViewDistance = options.ViewDistance;

        // The player walks on Jolt terrain, and the Server moves them with the same character controller.
        _physics = new PhysicsWorld();
        Solo.Server.Collision = _physics;
        Solo.Client.Collision = _physics.Create(Solo.Client.Local.State.Position, PlayerMovement.StandingHeight);
    }

    public EmbeddedServer Solo { get; }

    public ChunkRenderManager Manager { get; }

    public int ViewDistance { get; }

    public int WorkerCount => _pipeline.WorkerCount;

    /// <summary>Chunks whose terrain is in the physics world.</summary>
    public int PhysicsChunkCount => _physics.TerrainChunkCount;

    /// <summary>
    /// Adds the terrain of every loaded chunk to the physics world and drops the terrain of chunks that streamed out, so
    /// the player always walks on what is drawn.
    /// </summary>
    public void SyncPhysicsTerrain()
    {
        foreach (var chunk in Manager.Chunks)
        {
            if (_physicsChunks.Add(chunk.Coord))
            {
                _physics.AddTerrain(_pipeline.GetChunk(chunk.Coord));
            }
        }

        if (_physicsChunks.Count == Manager.LoadedCount)
        {
            return;
        }

        _physicsChunks.RemoveWhere(coord =>
        {
            if (Manager.Chunks.Any(c => c.Coord == coord))
            {
                return false;
            }

            _physics.RemoveTerrain(coord);
            return true;
        });
    }

    /// <summary>Steps the physics world once per simulation tick, after the players have moved.</summary>
    public void StepPhysics(TimeSpan elapsed)
    {
        var ticks = (int)Math.Min(Math.Round(elapsed.TotalSeconds * Simulation.TickRateHz), 5);
        for (var i = 0; i < ticks; i++)
        {
            _physics.Step(PlayerMovement.StepSeconds);
        }
    }

    public void Dispose()
    {
        _physics.Dispose();
        _pipeline.Dispose();
        Solo.Dispose();
    }
}

/// <summary>
/// Turns the keyboard and mouse into <see cref="PlayerInput"/> and sends it, so the Server moves the player and the client
/// predicts the same move. W A S D walk, Shift sprints, Ctrl crouches, Q and E lean, Space jumps, Tab captures the mouse.
/// </summary>
internal static class PlayerController
{
    public static PlayerInput Read(InputState input, float yaw, float pitch, bool mouseCaptured)
    {
        var forward = (input.IsDown(Key.W) ? 1f : 0f) - (input.IsDown(Key.S) ? 1f : 0f);
        var strafe = (input.IsDown(Key.D) ? 1f : 0f) - (input.IsDown(Key.A) ? 1f : 0f);
        return new PlayerInput(
            forward,
            strafe,
            yaw,
            pitch,
            input.IsDown(Key.LeftShift),
            input.IsDown(Key.LeftCtrl),
            input.IsDown(Key.Q),
            input.IsDown(Key.E),
            input.IsDown(Key.Space));
    }

    /// <summary>Places the camera at the player's eye, leaning sideways when asked.</summary>
    public static void ApplyToCamera(Camera camera, in PlayerMoveState state, float lean)
    {
        var eye = PlayerMovement.EyePosition(state);
        var right = new Vector3(MathF.Cos(state.Yaw), 0, MathF.Sin(state.Yaw));
        camera.Position = eye + (right * lean);
        camera.Yaw = state.Yaw;
        camera.Pitch = state.Pitch;
    }
}

/// <summary>Moves the camera from keys and mouse. W A S D fly, Space and Ctrl go up and down, Shift is fast.</summary>
internal static class FlyController
{
    public const float WalkSpeed = 24f;
    public const float FastSpeed = 90f;

    public static void Update(Camera camera, InputState input, float seconds, bool mouseCaptured)
    {
        if (mouseCaptured)
        {
            camera.Look(input.MouseDeltaX, input.MouseDeltaY);
        }

        var speed = (input.IsDown(Key.LeftShift) ? FastSpeed : WalkSpeed) * seconds;
        var right = (input.IsDown(Key.D) ? 1 : 0) - (input.IsDown(Key.A) ? 1 : 0);
        var forward = (input.IsDown(Key.W) ? 1 : 0) - (input.IsDown(Key.S) ? 1 : 0);
        var up = (input.IsDown(Key.Space) ? 1 : 0) - (input.IsDown(Key.LeftCtrl) ? 1 : 0);
        camera.Fly(right * speed, up * speed, forward * speed);
    }
}

internal static class SkyColors
{
    /// <summary>Linear light to the gamma-encoded byte color the swapchain expects, since the terrain shader encodes the same way.</summary>
    public static Rgba Encode(Vector3 linear) => new(
        Channel(linear.X),
        Channel(linear.Y),
        Channel(linear.Z));

    private static byte Channel(float value) => (byte)Math.Clamp(MathF.Round(MathF.Pow(MathF.Max(value, 0f), 1f / 2.2f) * 255f), 0f, 255f);
}
