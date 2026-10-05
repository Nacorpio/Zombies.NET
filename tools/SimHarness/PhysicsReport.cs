using System.Globalization;
using System.Numerics;
using Zombies.Domain.Mods;
using Zombies.Domain.World;
using Zombies.Engine.Core.Modding;
using Zombies.Engine.Net;
using Zombies.Engine.Physics;
using Zombies.Engine.Voxel;

/// <summary>
/// Drops a player onto generated terrain with Jolt and walks them into a one-block ledge, so CI proves the character
/// collides with the world and steps up without a GPU.
/// </summary>
internal static class PhysicsReport
{
    public static int Run(string[] args)
    {
        var seed = args.Length > 0 && ulong.TryParse(args[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 12345UL;
        var mods = ModLoader.Load(DirectoryModSource.Read(args.Length > 1 ? args[1] : "mods"));
        if (!mods.IsSuccess)
        {
            Console.Error.WriteLine("SimHarness physics: the mods did not load.");
            return 1;
        }

        var biomes = new BiomeCatalog(mods.Registry.OfKind("biome").Select(d => BiomeJson.Parse(d.Json)));
        var generator = new WorldGenerator(seed, biomes);
        var failures = new List<string>();

        using var world = new PhysicsWorld();
        for (var cz = -1; cz <= 1; cz++)
        {
            for (var cx = -1; cx <= 1; cx++)
            {
                world.AddTerrain(generator.Generate(new ChunkCoord(cx, cz)));
            }
        }

        // Fall onto the terrain and settle.
        var start = new Vector3(8f, 80f, 8f);
        var collision = world.Create(start, PlayerMovement.StandingHeight);
        var state = PlayerMoveState.At(start);
        for (var i = 0; i < 600; i++)
        {
            state = PlayerMovement.Step(state, default, PlayerMovement.StepSeconds, collision);
            world.Step(PlayerMovement.StepSeconds);
        }

        if (!state.OnGround)
        {
            failures.Add("the player never landed on the terrain");
        }

        var landed = state.Position;
        for (var i = 0; i < 300; i++)
        {
            state = PlayerMovement.Step(state, default, PlayerMovement.StepSeconds, collision);
            world.Step(PlayerMovement.StepSeconds);
        }

        if (state.Position != landed)
        {
            failures.Add($"the player crept {Vector3.Distance(landed, state.Position)} blocks while standing still");
        }

        // Walk into a one-block ledge and check the character steps up instead of stopping.
        var ledge = LedgeChunk(new ChunkCoord(0, 0), lowY: 64, highY: 65, stepAtX: 8);
        using var stepWorld = new PhysicsWorld();
        stepWorld.AddTerrain(ledge);
        var stepStart = new Vector3(4.5f, 65f, 8.5f);
        var stepCollision = stepWorld.Create(stepStart, PlayerMovement.StandingHeight);
        var stepState = PlayerMoveState.At(stepStart);
        var walk = new PlayerInput(0f, 1f, 0f, 0f, false, false, false, false, false);
        for (var i = 0; i < 60; i++)
        {
            stepState = PlayerMovement.Step(stepState, walk, PlayerMovement.StepSeconds, stepCollision);
            stepWorld.Step(PlayerMovement.StepSeconds);
        }

        if (stepState.Position.X <= 9f)
        {
            failures.Add($"the player did not step up the one-block ledge (stopped at x {stepState.Position.X})");
        }

        if (Math.Abs(stepState.Position.Y - 66f) > 1f)
        {
            failures.Add($"the player should stand on top of the ledge at y 66, but is at y {stepState.Position.Y}");
        }

        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"SimHarness physics: landed at {landed} on {world.TerrainChunkCount} chunks, stepped up to {stepState.Position}"));
        foreach (var failure in failures)
        {
            Console.Error.WriteLine($"SimHarness physics FAILED: {failure}");
        }

        return failures.Count == 0 ? 0 : 1;
    }

    private static Chunk LedgeChunk(ChunkCoord coord, int lowY, int highY, int stepAtX)
    {
        var chunk = new Chunk(coord);
        for (var z = 0; z < ChunkConstants.Size; z++)
        {
            for (var x = 0; x < ChunkConstants.Size; x++)
            {
                var groundY = x >= stepAtX ? highY : lowY;
                chunk.Set(x, 0, z, Blocks.Bedrock);
                for (var y = 1; y <= groundY; y++)
                {
                    chunk.Set(x, y, z, y == groundY ? Blocks.Grass : Blocks.Dirt);
                }
            }
        }

        chunk.RecomputeHeights();
        return chunk;
    }
}
