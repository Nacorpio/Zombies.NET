using System.Diagnostics;
using System.Globalization;
using Zombies.Domain.Mods;
using Zombies.Domain.World;
using Zombies.Engine.Core.Modding;
using Zombies.Engine.Voxel;

/// <summary>
/// Generates a patch of world headless, then times light and mesh building per chunk against the budget.
/// Usage: worldgen [seed] [radius] [--budget-ms N] [--mods DIR]
/// </summary>
internal static class WorldGenReport
{
    public static int Run(string[] args)
    {
        var seed = 12345UL;
        var radius = 4;
        var budgetMs = 2.0;
        var modsDirectory = "mods";
        var positional = 0;

        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--budget-ms" && i + 1 < args.Length)
            {
                budgetMs = double.Parse(args[++i], CultureInfo.InvariantCulture);
            }
            else if (args[i] == "--mods" && i + 1 < args.Length)
            {
                modsDirectory = args[++i];
            }
            else if (positional == 0)
            {
                seed = ulong.Parse(args[i], CultureInfo.InvariantCulture);
                positional++;
            }
            else
            {
                radius = int.Parse(args[i], CultureInfo.InvariantCulture);
            }
        }

        var loaded = ModLoader.Load(DirectoryModSource.Read(modsDirectory));
        if (!loaded.IsSuccess)
        {
            foreach (var error in loaded.Errors)
            {
                Console.Error.WriteLine(error);
            }

            return 1;
        }

        var biomes = new BiomeCatalog(loaded.Registry.OfKind("biome").Select(d => BiomeJson.Parse(d.Json)));
        var generator = new WorldGenerator(seed, biomes, SettlementContentLoader.Load(loaded.Registry));

        // Generate the ring one chunk wider than the meshed area so every meshed chunk has real neighbors.
        var chunks = new Dictionary<ChunkCoord, Chunk>();
        var generateStart = Stopwatch.GetTimestamp();
        for (var z = -radius - 1; z <= radius + 1; z++)
        {
            for (var x = -radius - 1; x <= radius + 1; x++)
            {
                chunks[new ChunkCoord(x, z)] = generator.Generate(new ChunkCoord(x, z));
            }
        }

        var generateMs = Stopwatch.GetElapsedTime(generateStart).TotalMilliseconds / chunks.Count;

        var coords = new List<ChunkCoord>();
        for (var z = -radius; z <= radius; z++)
        {
            for (var x = -radius; x <= radius; x++)
            {
                coords.Add(new ChunkCoord(x, z));
            }
        }

        // Warm up so JIT tiering is not counted: the game runs long enough that steady state is what matters.
        for (var w = 0; w < Math.Min(24, coords.Count); w++)
        {
            Build(chunks, coords[w]);
        }

        var lightMs = new List<double>();
        var meshMs = new List<double>();
        long vertices = 0;
        long quads = 0;
        foreach (var coord in coords)
        {
            var (light, mesh, set) = Build(chunks, coord);
            lightMs.Add(light);
            meshMs.Add(mesh);
            vertices += set.VertexCount;
            quads += set.QuadCount;
        }

        var totals = lightMs.Zip(meshMs, (l, m) => l + m).ToList();
        var mean = totals.Average();
        var p95 = totals.Order().ElementAt((int)(totals.Count * 0.95));

        Console.WriteLine($"SimHarness worldgen: seed {seed}, {coords.Count} chunks meshed, generator version {WorldGenerator.GeneratorVersion}");
        foreach (var probe in new[] { new ChunkCoord(0, 0), new ChunkCoord(1, 0), new ChunkCoord(-3, 5) })
        {
            if (chunks.TryGetValue(probe, out var probed))
            {
                Console.WriteLine($"  {probe} hash {probed.Hash():X16}");
            }
        }

        Console.WriteLine($"  fingerprint: WorldHash {WorldHash.Mix(seed, 1, 2, 3):X16}, noise {IntNoise.Value2D(seed, 100, -200, 6)} {IntNoise.Fractal2D(seed, -37, 911, 6, 4)}");
        var grid = new RegionGrid(seed);
        var site = grid.SiteIn(new RegionCoord(3, -2));
        Console.WriteLine($"  fingerprint: site(3,-2) {(site is null ? "none" : $"{site.X},{site.Z},{site.Seed:X16}")}, danger(3,-2) {grid.DangerOf(new RegionCoord(3, -2))}, danger(8,5) {grid.DangerOf(new RegionCoord(8, 5))}");
        Console.WriteLine(SettlementFingerprint(seed, generator));
        Console.WriteLine($"  generate {generateMs:F3} ms/chunk");
        Console.WriteLine($"  light    {lightMs.Average():F3} ms/chunk");
        Console.WriteLine($"  mesh     {meshMs.Average():F3} ms/chunk");
        Console.WriteLine($"  light+mesh mean {mean:F3} ms, p95 {p95:F3} ms, budget {budgetMs:F3} ms");
        Console.WriteLine($"  {vertices / coords.Count} vertices and {quads / coords.Count} quads per chunk");

        using var pipeline = new ChunkPipeline(generator);
        var parallelStart = Stopwatch.GetTimestamp();
        Task.WaitAll(coords.Select(pipeline.RequestMeshAsync).ToArray());
        Console.WriteLine($"  {pipeline.WorkerCount} workers built {coords.Count} chunks (incl. generation) in {Stopwatch.GetElapsedTime(parallelStart).TotalMilliseconds:F0} ms");

        if (mean > budgetMs)
        {
            Console.Error.WriteLine($"SimHarness worldgen: mean light+mesh {mean:F3} ms is over the {budgetMs:F3} ms budget.");
            return 1;
        }

        return 0;
    }

    /// <summary>
    /// The first Settlement found scanning outward from the spawn region, as the hash of the chunk its first Structure stands in
    /// and its Container count, so two platforms' logs can be compared line by line.
    /// </summary>
    private static string SettlementFingerprint(ulong seed, WorldGenerator generator)
    {
        for (var ring = 1; ring <= 6; ring++)
        {
            for (var x = -ring; x <= ring; x++)
            {
                for (var z = -ring; z <= ring; z++)
                {
                    if (Math.Max(Math.Abs(x), Math.Abs(z)) != ring || generator.SettlementIn(new RegionCoord(x, z)) is not { } plan)
                    {
                        continue;
                    }

                    var first = plan.Structures[0];
                    var chunk = generator.Generate(new ChunkCoord(first.X >> 4, first.Z >> 4));
                    return $"  fingerprint: settlement region({x},{z}) {plan.Type.Id} structures {plan.Structures.Count} containers {generator.ContainersIn(new RegionCoord(x, z)).Count} zombies {plan.ZombieSpawns.Count} {chunk.Coord} hash {chunk.Hash():X16} (seed {seed})";
                }
            }
        }

        return "  fingerprint: no settlement within six regions";
    }

    private static (double LightMs, double MeshMs, ChunkMeshSet Set) Build(Dictionary<ChunkCoord, Chunk> chunks, ChunkCoord coord)
    {
        var chunk = chunks[coord];
        var neighbors = new ChunkNeighbors(
            chunks[new ChunkCoord(coord.X + 1, coord.Z)],
            chunks[new ChunkCoord(coord.X - 1, coord.Z)],
            chunks[new ChunkCoord(coord.X, coord.Z + 1)],
            chunks[new ChunkCoord(coord.X, coord.Z - 1)]);

        var t0 = Stopwatch.GetTimestamp();
        var light = ChunkLighting.Compute(chunk, neighbors);
        var t1 = Stopwatch.GetTimestamp();
        var set = ChunkMesher.Build(chunk, light, neighbors);
        var t2 = Stopwatch.GetTimestamp();
        return (Stopwatch.GetElapsedTime(t0, t1).TotalMilliseconds, Stopwatch.GetElapsedTime(t1, t2).TotalMilliseconds, set);
    }
}
