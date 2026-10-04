using System.Globalization;
using System.Numerics;
using Zombies.Domain.Mods;
using Zombies.Engine.Core;
using Zombies.Engine.Core.Modding;
using Zombies.Engine.Net;
using Zombies.Engine.Voxel;

/// <summary>
/// Runs a Server with two fake clients over the in-memory transport, one of them walking, then checks that both clients
/// see exactly the Server's entities and that a steady-state tick allocated nothing.
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
        var simulation = new Simulation(new Walker(alice, server.Options.SpawnPoint), server, alice, bob);

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

        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"SimHarness net: {server.PlayerCount} players, {server.World.Count} entities, {server.SnapshotsSent} snapshots averaging {server.SnapshotBytesSent / Math.Max(1, server.SnapshotsSent)} bytes over {simulation.CurrentTick} ticks, steady-state allocation {allocated} bytes"));
        foreach (var failure in failures)
        {
            Console.Error.WriteLine($"SimHarness net FAILED: {failure}");
        }

        return failures.Count == 0 ? 0 : 1;
    }

    private sealed class Walker(GameClient client, Vector3 start) : ITickable
    {
        private Vector3 _position = start;

        public void Tick(long tick)
        {
            if (client.State == ClientState.Joined)
            {
                _position += new Vector3(0.15f, 0, 0.05f);
                client.Send(new MovePlayer(_position, tick * 0.02f));
            }
        }
    }
}
