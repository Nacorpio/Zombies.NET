using System.Numerics;
using Zombies.Domain.Mods;
using Zombies.Domain.World;
using Zombies.Engine.Core.Modding;
using Zombies.Engine.Net;
using Zombies.Engine.Platform;
using Zombies.Engine.Render;
using Zombies.Engine.Voxel;

namespace Zombies.Client;

/// <summary>Everything the client needs to show the world: the loaded mods, the embedded Server, the generator, worker threads, and the manager that streams chunks to the GPU.</summary>
internal sealed class WorldSession : IDisposable
{
    private readonly ChunkPipeline _pipeline;

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
    }

    public EmbeddedServer Solo { get; }

    public ChunkRenderManager Manager { get; }

    public int ViewDistance { get; }

    public int WorkerCount => _pipeline.WorkerCount;

    public void Dispose()
    {
        _pipeline.Dispose();
        Solo.Dispose();
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
