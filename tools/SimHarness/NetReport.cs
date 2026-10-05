using System.Globalization;
using Zombies.Domain.Items;
using Zombies.Domain.Mods;
using Zombies.Domain.Survival;
using Zombies.Engine.Core;
using Zombies.Engine.Core.Modding;
using Zombies.Engine.Net;
using Zombies.Engine.Voxel;

/// <summary>
/// Runs a Server with two fake clients over the in-memory transport, one of them walking, then checks that both clients
/// see exactly the Server's entities and that a steady-state tick allocated nothing. Then it joins one client per
/// Profession the mods declare, in the first Scenario they declare, and checks that each one got its loadout.
/// </summary>
internal static class NetReport
{
    public static int Run(string[] args)
    {
        var ticks = args.Length > 0 && long.TryParse(args[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 300;
        var mods = ModLoader.Load(DirectoryModSource.Read(args.Length > 1 ? args[1] : "mods"));
        if (!mods.IsSuccess)
        {
            Console.Error.WriteLine("SimHarness net: the mods did not load.");
            return 1;
        }

        var identity = GameIdentity.From(mods, WorldGenerator.GeneratorVersion);
        var network = new InMemoryNetwork();
        using var serverTransport = network.CreateServer();
        var server = new GameServer(serverTransport, new ServerOptions(identity, WorldSeed: 12345));
        using var aliceTransport = network.Connect();
        using var bobTransport = network.Connect();
        var alice = new GameClient(aliceTransport, identity, "alice");
        var bob = new GameClient(bobTransport, identity, "bob");
        var simulation = new Simulation(new Walker(alice), server, alice, bob);

        var allocated = AllocationProbe.MeasureSteadyState(simulation, warmupTicks: 60, ticks: ticks);
        simulation.Run(3);

        var failures = new List<string>();
        foreach (var client in new[] { alice, bob })
        {
            if (client.State != ClientState.Joined)
            {
                failures.Add($"a client is {client.State}");
            }
            else if (!client.World.Entities.SequenceEqual(server.World.Entities))
            {
                failures.Add("a client's entities differ from the Server's");
            }
        }

        if (allocated != 0)
        {
            failures.Add($"the steady-state tick allocated {allocated} bytes");
        }

        // The client predicts the same movement the Server runs, so its own player matches the Server exactly.
        if (server.World.TryGet(alice.PlayerEntityId, out var truth) && truth.Position != alice.Local.State.Position)
        {
            failures.Add($"the client predicted {alice.Local.State.Position} but the Server has {truth.Position}");
        }

        if (alice.Local.ReconciliationCount != 0)
        {
            failures.Add($"the prediction needed {alice.Local.ReconciliationCount} corrections");
        }

        failures.AddRange(JoinWithEachProfession(mods, identity));

        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"SimHarness net: {server.PlayerCount} players, {server.World.Count} entities, {server.SnapshotsSent} snapshots averaging {server.SnapshotBytesSent / Math.Max(1, server.SnapshotsSent)} bytes over {simulation.CurrentTick} ticks, steady-state allocation {allocated} bytes, prediction corrections {alice.Local.ReconciliationCount}"));
        foreach (var failure in failures)
        {
            Console.Error.WriteLine($"SimHarness net FAILED: {failure}");
        }

        return failures.Count == 0 ? 0 : 1;
    }

    private static List<string> JoinWithEachProfession(ModLoadResult mods, GameIdentity identity)
    {
        var failures = new List<string>();
        var professions = StartingContentLoader.LoadProfessions(mods.Registry).All;
        var scenarios = StartingContentLoader.LoadScenarios(mods.Registry).All;
        var scenario = scenarios.Count > 0 ? scenarios[0] : null;
        var network = new InMemoryNetwork();
        using var serverTransport = network.CreateServer();
        var server = new GameServer(serverTransport, new ServerOptions(identity, WorldSeed: 12345)
        {
            MaxPlayers = Math.Max(4, professions.Count),
            Items = new ItemCatalog(mods.Registry.OfKind("item").Select(d => ItemDefinitionJson.Parse(d.Json))),
            Wearables = StartingContentLoader.LoadWearables(mods.Registry),
            Loot = StartingContentLoader.LoadLoot(mods.Registry),
            Professions = StartingContentLoader.LoadProfessions(mods.Registry),
            Scenario = scenario,
        });
        var transports = new List<ITransport>();
        var clients = new List<GameClient>();
        foreach (var profession in professions)
        {
            transports.Add(network.Connect());
            clients.Add(new GameClient(transports[^1], identity, $"player{clients.Count}", profession.Id));
        }

        new Simulation([server, .. clients]).Run(10);
        for (var i = 0; i < professions.Count; i++)
        {
            var profession = professions[i];
            if (clients[i].State != ClientState.Joined || !server.TryGetPlayer(new ConnectionId(i + 1), out var session))
            {
                failures.Add($"the client with profession {profession.Id} is {clients[i].State}");
            }
            else if (session.Profession != profession.Id
                || !session.Outfit.WornItems.SequenceEqual(profession.Outfit)
                || profession.Items.Any(item => session.Carried.CountOf(item.Item) < item.Count)
                || session.Modifiers.All.Count != profession.Modifiers.Count)
            {
                failures.Add($"the player with profession {profession.Id} did not get its loadout");
            }
        }

        foreach (var transport in transports)
        {
            transport.Dispose();
        }

        return failures;
    }

    private sealed class Walker(GameClient client) : ITickable
    {
        public void Tick(long tick)
        {
            if (client.State == ClientState.Joined)
            {
                // Walk forward and turn slowly, so the harness exercises prediction and reconciliation, not just replication.
                client.SendInput(new PlayerInput(1f, 0f, tick * 0.01f, 0f, false, false, false, false, false));
            }
        }
    }
}
