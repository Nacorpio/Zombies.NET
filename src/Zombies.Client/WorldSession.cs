using System.Numerics;
using Zombies.Domain.Mods;
using Zombies.Domain.World;
using Zombies.Engine.Core.Modding;
using Zombies.Engine.Platform;
using Zombies.Engine.Render;
using Zombies.Engine.Voxel;

namespace Zombies.Client;

/// <summary>Everything the client needs to show the world: the loaded mods, the generator, worker threads, and the manager that streams chunks to the GPU.</summary>
internal sealed class WorldSession : IDisposable
{
    private readonly ChunkPipeline _pipeline;

    public WorldSession(ClientOptions options, IWorldRenderer renderer)
    {
        var mods = ModLoader.Load(DirectoryModSource.Read(options.ModsDirectory ?? FindModsDirectory()));
        if (!mods.IsSuccess)
        {
            throw new InvalidOperationException("The mods could not be loaded:" + Environment.NewLine + string.Join(Environment.NewLine, mods.Errors));
        }

        var biomes = new BiomeCatalog(mods.Registry.OfKind("biome").Select(d => BiomeJson.Parse(d.Json)));
        _pipeline = new ChunkPipeline(new WorldGenerator(options.Seed, biomes));
        Manager = new ChunkRenderManager(_pipeline, renderer, new ChunkStreamer(options.ViewDistance));
        ViewDistance = options.ViewDistance;
    }

    public ChunkRenderManager Manager { get; }

    public int ViewDistance { get; }

    public int WorkerCount => _pipeline.WorkerCount;

    public void Dispose() => _pipeline.Dispose();

    /// <summary>Finds the <c>mods</c> folder by walking up from the program's folder, so it works from the repository and from a published build.</summary>
    private static string FindModsDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "mods");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not find a 'mods' folder. Pass one with --mods.");
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
