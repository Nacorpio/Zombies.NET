using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text;
using Zombies.Domain.Mods;
using Zombies.Domain.World;
using Zombies.Domain.Zombies;
using Zombies.Engine.Ai;
using Zombies.Engine.Animation;
using Zombies.Engine.Core;
using Zombies.Engine.Core.Modding;
using Zombies.Engine.Ecs;
using Zombies.Engine.Net;
using Zombies.Engine.Voxel;

/// <summary>
/// Runs a Server with four fake clients and 200 zombies on generated terrain, the players walking about and shooting now and then,
/// and times each Server tick (the Server, the zombies, and their AI) against the slice's budget. It also checks that a steady-state
/// tick allocates nothing.
/// Usage: ai [seed] [--ticks N] [--budget-ms N] [--zombies N] [--mods DIR]
/// </summary>
internal static class AiReport
{
    private const int Players = 4;

    public static int Run(string[] args)
    {
        ulong seed = 12345;
        var ticks = 900;
        var zombieCount = 200;
        var budgetMs = 12.0;
        var modsDirectory = "mods";
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--ticks" when i + 1 < args.Length: ticks = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--budget-ms" when i + 1 < args.Length: budgetMs = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--zombies" when i + 1 < args.Length: zombieCount = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--mods" when i + 1 < args.Length: modsDirectory = args[++i]; break;
                default: seed = ulong.Parse(args[i], CultureInfo.InvariantCulture); break;
            }
        }

        var packages = DirectoryModSource.Read(modsDirectory);
        var mods = ModLoader.Load(packages);
        if (!mods.IsSuccess)
        {
            Console.Error.WriteLine("SimHarness ai: the mods did not load.");
            return 1;
        }

        var baseMod = packages.Single(p => p.Source == "base");
        if (!Skeleton.TryParse(Asset(baseMod, "rigs/humanoid.skeleton.json"), out var skeleton, out var skeletonError)
            || !ClipSet.TryParse(Asset(baseMod, "rigs/humanoid.clips.json"), out var clips, out var clipError))
        {
            Console.Error.WriteLine("SimHarness ai: the humanoid rig did not load.");
            return 1;
        }

        _ = skeletonError;
        _ = clipError;

        var biomes = new BiomeCatalog(mods.Registry.OfKind("biome").Select(d => BiomeJson.Parse(d.Json)));
        var generator = new WorldGenerator(seed, biomes);
        var terrain = new ChunkTerrain();
        var generating = Stopwatch.StartNew();
        terrain.Generate(generator, new ChunkCoord(0, 0), radius: 5);
        generating.Stop();

        var identity = GameIdentity.From(mods, WorldGenerator.GeneratorVersion);
        var network = new InMemoryNetwork();
        using var serverTransport = network.CreateServer();
        var server = new GameServer(serverTransport, new ServerOptions(identity, seed));
        var traits = new TraitRegistry();
        BaseTraits.Register(traits);
        var catalog = ZombieContentLoader.Load(mods.Registry);
        var zombies = new ZombieSystem(server.World, catalog, traits, skeleton, clips);
        var ai = new ZombieAi(server.World, zombies, terrain);
        var preparing = Stopwatch.StartNew();
        ai.Planner.Prepare(new NavCell(-5 * 16, 0, -5 * 16), new NavCell((6 * 16) - 1, 0, (6 * 16) - 1));
        preparing.Stop();
        var clients = new List<GameClient>();
        var transports = new List<ITransport>();
        for (var i = 0; i < Players; i++)
        {
            var transport = network.Connect();
            transports.Add(transport);
            clients.Add(new GameClient(transport, identity, $"player{i}"));
        }

        long tick = 0;
        for (; tick < 5; tick++)
        {
            server.Tick(tick);
            foreach (var client in clients)
            {
                client.Poll();
            }
        }

        // Four players, each at home in a corner of the patch, with 50 zombies scattered around each of them.
        Vector3[] homes = [Surface(terrain, -24, -24), Surface(terrain, 24, -24), Surface(terrain, -24, 24), Surface(terrain, 24, 24)];
        var sessions = new PlayerSession[Players];
        for (var p = 0; p < Players; p++)
        {
            if (!server.TryGetPlayer(new ConnectionId(p + 1), out sessions[p]))
            {
                Console.Error.WriteLine("SimHarness ai: a player did not join.");
                return 1;
            }
        }

        var types = catalog.Types.Select(t => t.Id).ToArray();
        var spawned = 0;
        for (var i = 0; spawned < zombieCount && i < zombieCount * 4; i++)
        {
            var hash = WorldHash.Mix(seed, i, 17, 3);
            var home = homes[i % Players];
            var x = (int)home.X + (int)(hash % 41) - 20;
            var z = (int)home.Z + (int)((hash >> 16) % 41) - 20;
            var type = types[(int)((hash >> 32) % (ulong)types.Length)];
            var spec = new ZombieSpec(hash, type, 1);
            if (zombies.TrySpawn(spec, Surface(terrain, x, z), (hash >> 40) % 360 * (MathF.PI / 180f), out _, out _))
            {
                spawned++;
            }
        }

        void Step()
        {
            // Each player walks a slow circle round their home and fires a shot every few seconds.
            for (var p = 0; p < Players; p++)
            {
                var angle = (tick * 0.03f) + (p * 1.7f);
                var target = homes[p] + new Vector3(MathF.Cos(angle) * 8, 0, MathF.Sin(angle) * 8);
                server.Teleport(sessions[p], Surface(terrain, (int)MathF.Floor(target.X), (int)MathF.Floor(target.Z)) with { X = target.X, Z = target.Z }, angle);
                if ((tick + (p * 23)) % 120 == 0)
                {
                    ai.Perception.Emit(homes[p], 60f, sessions[p].EntityId);
                }
            }
        }

        var warmup = 10 * Simulation.TickRateHz;
        for (var i = 0; i < warmup; i++, tick++)
        {
            Step();
            server.Tick(tick);
            zombies.Tick(tick);
            ai.Tick(tick);
            foreach (var client in clients)
            {
                client.Poll();
            }
        }

        var times = new double[ticks];
        var aiTimes = new double[ticks];
        var allocated = 0L;
        for (var i = 0; i < ticks; i++, tick++)
        {
            Step();
            var before = GC.GetAllocatedBytesForCurrentThread();
            var started = Stopwatch.GetTimestamp();
            server.Tick(tick);
            zombies.Tick(tick);
            ai.Tick(tick);
            times[i] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            allocated += GC.GetAllocatedBytesForCurrentThread() - before;
            aiTimes[i] = ai.LastTickTime.TotalMilliseconds;
            foreach (var client in clients)
            {
                client.Poll();
            }
        }

        foreach (var transport in transports)
        {
            transport.Dispose();
        }

        var sorted = times.Order().ToArray();
        double Percentile(double p) => sorted[Math.Min(sorted.Length - 1, (int)(p * sorted.Length))];
        var mean = times.Average();
        var p99 = Percentile(0.99);
        var states = new int[4];
        foreach (ref readonly var entity in server.World.Entities)
        {
            if (entity.Kind == EntityKind.Zombie && ai.TryGetMind(entity.Id, out var mind))
            {
                states[(int)mind.State]++;
            }
        }

        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"SimHarness ai: {server.PlayerCount} players, {spawned} zombies on {terrain.ChunkCount} chunks generated in {generating.Elapsed.TotalMilliseconds:F0} ms and analysed for routes in {preparing.Elapsed.TotalMilliseconds:F0} ms, {ticks} ticks timed after {warmup} warm-up ticks"));
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"  server tick mean {mean:F3} ms, p50 {Percentile(0.5):F3} ms, p95 {Percentile(0.95):F3} ms, p99 {p99:F3} ms, max {sorted[^1]:F3} ms, budget {budgetMs:F1} ms"));
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"  of which AI mean {aiTimes.Average():F3} ms, max {aiTimes.Max():F3} ms; detail full {ai.CountAt(DetailLevel.Full)}, reduced {ai.CountAt(DetailLevel.Reduced)}, dormant {ai.CountAt(DetailLevel.Dormant)}; flow fields {ai.FlowFieldCount}"));
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"  zombies idle {states[0]}, investigating {states[1]}, chasing {states[2]}, breaking doors {states[3]}; routes and fields planned {ai.Planner.Completed}, waiting {ai.Planner.Pending}; steady-state allocation {allocated} bytes"));

        var failures = new List<string>();
        if (spawned < zombieCount)
        {
            failures.Add($"only {spawned} of {zombieCount} zombies could spawn");
        }

        if (p99 > budgetMs)
        {
            failures.Add($"the 99th percentile tick took {p99:F3} ms, over the {budgetMs:F1} ms budget");
        }

        if (allocated != 0)
        {
            failures.Add($"the steady-state ticks allocated {allocated} bytes");
        }

        foreach (var failure in failures)
        {
            Console.Error.WriteLine($"SimHarness ai FAILED: {failure}");
        }

        return failures.Count == 0 ? 0 : 1;
    }

    private static Vector3 Surface(ChunkTerrain terrain, int x, int z) => new(x + 0.5f, terrain.SurfaceY(x, z), z + 0.5f);

    private static string Asset(ModPackage package, string path) =>
        Encoding.UTF8.GetString(package.Assets.Single(a => a.Path == path).Bytes).TrimStart('﻿');
}
