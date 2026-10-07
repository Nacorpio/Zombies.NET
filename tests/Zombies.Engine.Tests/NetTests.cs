using System.Numerics;
using UnitsNet;
using Zombies.Domain.Items;
using Zombies.Domain.Mods;
using Zombies.Engine.Core;
using Zombies.Engine.Core.Modding;
using Zombies.Engine.Net;
using Zombies.Engine.Voxel;

namespace Zombies.Engine.Tests;

/// <summary>Runs a Server and fake clients over the in-memory transport and checks what each client ends up seeing.</summary>
public sealed class NetTests
{
    private static readonly GameIdentity Identity = new(NetProtocol.Version, WorldGenerator.GeneratorVersion, [new ModFingerprint("base", "1.0.0", "aa")]);

    private sealed class Rig
    {
        public Rig(ServerOptions? options = null, int dropEveryNthUnreliable = 0, int latencyPolls = 0)
        {
            Network.DropEveryNthUnreliable = dropEveryNthUnreliable;
            Network.LatencyPolls = latencyPolls;
            ServerTransport = Network.CreateServer();
            Server = new GameServer(ServerTransport, options ?? new ServerOptions(Identity, WorldSeed: 777));
        }

        public InMemoryNetwork Network { get; } = new();

        public ITransport ServerTransport { get; }

        public GameServer Server { get; }

        public List<GameClient> Clients { get; } = [];

        public GameClient Join(string name, GameIdentity? identity = null)
        {
            var client = new GameClient(Network.Connect(), identity ?? Identity, name);
            Clients.Add(client);
            return client;
        }

        /// <summary>One Server tick, then every client reads what it was sent.</summary>
        public void Run(int ticks)
        {
            for (var i = 0; i < ticks; i++)
            {
                Server.Tick(Tick++);
                foreach (var client in Clients)
                {
                    client.Poll();
                }
            }
        }

        public long Tick { get; private set; }
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Zombies.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Could not find the repository root.");
    }

    [Fact]
    public void TwoClients_Join_AndEachSeesBothPlayers()
    {
        var rig = new Rig();
        var alice = rig.Join("alice");
        var bob = rig.Join("bob");

        rig.Run(10);

        Assert.Equal(ClientState.Joined, alice.State);
        Assert.Equal(ClientState.Joined, bob.State);
        Assert.Equal(777UL, alice.WorldSeed);
        Assert.NotEqual(alice.PlayerEntityId, bob.PlayerEntityId);
        Assert.Equal(2, rig.Server.PlayerCount);
        foreach (var client in new[] { alice, bob })
        {
            Assert.True(client.World.TryGet(alice.PlayerEntityId, out var a));
            Assert.True(client.World.TryGet(bob.PlayerEntityId, out var b));
            Assert.Equal(EntityKind.Player, a.Kind);
            Assert.Equal(rig.Server.Options.SpawnPoint, b.Position);
        }
    }

    [Fact]
    public void ValidMove_ReplicatesToTheOtherClient()
    {
        var rig = new Rig();
        var alice = rig.Join("alice");
        var bob = rig.Join("bob");
        rig.Run(5);

        var target = rig.Server.Options.SpawnPoint + new Vector3(1.5f, 0, 0);
        alice.Send(new MovePlayer(target, 1.25f));
        rig.Run(5);

        Assert.True(bob.World.TryGet(alice.PlayerEntityId, out var seen));
        Assert.Equal(target, seen.Position);
        Assert.Equal(1.25f, seen.Yaw);
        Assert.Equal(0, alice.RejectionCount);
    }

    [Fact]
    public void MoveFartherThanAStep_IsRejected_AndChangesNothing()
    {
        var rig = new Rig();
        var alice = rig.Join("alice");
        rig.Run(5);

        var sequence = alice.Send(new MovePlayer(new Vector3(500, 80, 500), 0));
        rig.Run(5);

        Assert.Equal(new CommandRejected(sequence, CommandRejection.Invalid, $"A move may cover at most {MovePlayer.MaxStep} blocks."), alice.LastRejection);
        Assert.True(rig.Server.World.TryGet(alice.PlayerEntityId, out var player));
        Assert.Equal(rig.Server.Options.SpawnPoint, player.Position);
    }

    [Fact]
    public void MoveToNaN_IsRejected()
    {
        var rig = new Rig();
        var alice = rig.Join("alice");
        rig.Run(5);

        alice.Send(new MovePlayer(new Vector3(float.NaN, 80, 8), 0));
        rig.Run(3);

        Assert.Equal(CommandRejection.Invalid, alice.LastRejection?.Reason);
    }

    [Fact]
    public void UnknownCommandId_IsRejected()
    {
        var rig = new Rig();
        var alice = rig.Join("alice");
        rig.Run(5);

        alice.SendRaw([(byte)MessageType.Command, 9, 0, 0, 0, 0xFF, 0xFF]);
        rig.Run(3);

        Assert.Equal(new CommandRejected(9, CommandRejection.UnknownCommand, "No command has id 65535."), alice.LastRejection);
        Assert.Equal(ClientState.Joined, alice.State);
    }

    [Fact]
    public void TruncatedCommandPayload_IsRejectedAsMalformed()
    {
        var rig = new Rig();
        var alice = rig.Join("alice");
        rig.Run(5);

        alice.SendRaw([(byte)MessageType.Command, 3, 0, 0, 0, (byte)MovePlayer.CommandId, 0, 1, 2]);
        rig.Run(3);

        Assert.Equal(CommandRejection.Malformed, alice.LastRejection?.Reason);
        Assert.True(rig.Server.World.TryGet(alice.PlayerEntityId, out var player));
        Assert.Equal(rig.Server.Options.SpawnPoint, player.Position);
    }

    [Fact]
    public void CommandBeforeJoin_DisconnectsTheClient()
    {
        var network = new InMemoryNetwork();
        var server = new GameServer(network.CreateServer(), new ServerOptions(Identity, 1));
        using var raw = network.Connect();
        var handler = new RecordingHandler();

        raw.Send(ConnectionId.Server, [(byte)MessageType.Command, 1, 0, 0, 0, 1, 0], Delivery.ReliableOrdered);
        server.Tick(0);
        raw.Poll(handler);

        Assert.True(handler.Disconnected);
        Assert.Equal(0, server.PlayerCount);
    }

    [Fact]
    public void Join_IsRefused_WhenAModHashDiffers()
    {
        var rig = new Rig();
        var client = rig.Join("eve", Identity with { Mods = [new ModFingerprint("base", "1.0.0", "bb")] });

        rig.Run(3);

        Assert.Equal(ClientState.Refused, client.State);
        Assert.Equal(JoinRefusal.ModListMismatch, client.Refusal);
        Assert.Equal("Mod 'base' 1.0.0 has different content on the Server and on this client.", client.RefusalDetail);
        Assert.Equal(0, rig.Server.PlayerCount);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("extra")]
    [InlineData("version")]
    public void Join_IsRefused_WhenTheModListDiffers(string difference)
    {
        var rig = new Rig();
        IReadOnlyList<ModFingerprint> mods = difference switch
        {
            "missing" => [],
            "extra" => [.. Identity.Mods, new ModFingerprint("sample_data", "1.0.0", "cc")],
            _ => [new ModFingerprint("base", "1.0.1", "aa")],
        };
        var client = rig.Join("eve", Identity with { Mods = mods });

        rig.Run(3);

        Assert.Equal(JoinRefusal.ModListMismatch, client.Refusal);
    }

    [Fact]
    public void Join_IsRefused_WhenTheVersionDiffers()
    {
        var rig = new Rig();
        var client = rig.Join("eve", Identity with { WorldGeneratorVersion = Identity.WorldGeneratorVersion + 1 });

        rig.Run(3);

        Assert.Equal(JoinRefusal.VersionMismatch, client.Refusal);
    }

    private static readonly WorldOptionSetting[] ServerOptions = [new("base:world_option/loot_rarity", 1), new("base:world_option/zombie_density", 2)];

    [Fact]
    public void Join_IsAccepted_WhenTheWorldOptionsMatch()
    {
        var identity = Identity with { WorldOptions = ServerOptions };
        var rig = new Rig(new ServerOptions(identity, WorldSeed: 777));
        var client = rig.Join("alice", identity with { WorldOptions = [.. ServerOptions] });

        rig.Run(3);

        Assert.Equal(ClientState.Joined, client.State);
    }

    [Fact]
    public void Join_IsRefusedWithTheDifferingOption_WhenAWorldOptionValueDiffers()
    {
        var rig = new Rig(new ServerOptions(Identity with { WorldOptions = ServerOptions }, WorldSeed: 777));
        var client = rig.Join("eve", Identity with { WorldOptions = [ServerOptions[0], new("base:world_option/zombie_density", 1)] });

        rig.Run(3);

        Assert.Equal(ClientState.Refused, client.State);
        Assert.Equal(JoinRefusal.WorldOptionMismatch, client.Refusal);
        Assert.Equal("World option 'base:world_option/zombie_density' is 2 on the Server but 1 on this client.", client.RefusalDetail);
        Assert.Equal(0, rig.Server.PlayerCount);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("extra")]
    public void Join_IsRefused_WhenAClientLacksOrAddsAWorldOption(string difference)
    {
        var rig = new Rig(new ServerOptions(Identity with { WorldOptions = ServerOptions }, WorldSeed: 777));
        var options = difference == "missing" ? ServerOptions[..1] : [.. ServerOptions, new WorldOptionSetting("mymod:world_option/extra", 1)];
        var client = rig.Join("eve", Identity with { WorldOptions = options });

        rig.Run(3);

        Assert.Equal(JoinRefusal.WorldOptionMismatch, client.Refusal);
    }

    [Fact]
    public void Identity_FromLoadedModsAndOptions_CarriesOnlySimulationOptionsWithDefaultsFilledIn()
    {
        var loaded = ModLoader.Load(DirectoryModSource.Read(Path.Combine(RepoRoot(), "mods")));
        var catalog = WorldOptionCatalog.From(loaded.Registry);

        var identity = GameIdentity.From(loaded, WorldGenerator.GeneratorVersion, new WorldOptions(catalog, [KeyValuePair.Create(BaseWorldOptions.ZombieDensity, 2.0)]));

        Assert.Equal(ServerOptions, identity.WorldOptions);
    }

    [Fact]
    public void RefusedClient_IsDisconnectedAfterItHadTimeToReadWhy()
    {
        var rig = new Rig();
        var client = rig.Join("eve", Identity with { ProtocolVersion = 99 });

        rig.Run(Simulation.TickRateHz + 3);

        Assert.Equal(ClientState.Refused, client.State);
        Assert.False(rig.Server.TryGetPlayer(new ConnectionId(1), out _));
    }

    [Fact]
    public void FifthPlayer_IsRefused_BecauseTheServerIsFull()
    {
        var rig = new Rig();
        for (var i = 0; i < 4; i++)
        {
            rig.Join($"p{i}");
        }

        var fifth = rig.Join("p4");
        rig.Run(3);

        Assert.Equal(JoinRefusal.ServerFull, fifth.Refusal);
        Assert.Equal(4, rig.Server.PlayerCount);
    }

    [Fact]
    public void Snapshots_AreSentAt20Hz()
    {
        var rig = new Rig();
        var alice = rig.Join("alice");
        rig.Run(1);
        var before = rig.Server.SnapshotsSent;

        rig.Run(Simulation.TickRateHz * 3);

        Assert.Equal(60, rig.Server.SnapshotsSent - before);
        Assert.True(alice.SnapshotsReceived >= 59);
    }

    [Fact]
    public void Interest_OnlyReplicatesEntitiesWithinTheChunkRadius()
    {
        var rig = new Rig(new ServerOptions(Identity, 1) { InterestRadiusChunks = 2 });
        var alice = rig.Join("alice");
        var near = rig.Server.World.Spawn(EntityKind.Player, new Vector3(8 + (2 * 16), 80, 8), 0);
        var far = rig.Server.World.Spawn(EntityKind.Player, new Vector3(8 + (3 * 16), 80, 8), 0);

        rig.Run(5);

        Assert.True(alice.World.TryGet(near, out _));
        Assert.False(alice.World.TryGet(far, out _));

        rig.Server.World.Move(far, new Vector3(8 + 16, 80, 8), 0);
        rig.Server.World.Move(near, new Vector3(8 - (5 * 16), 80, 8), 0);
        rig.Run(5);

        Assert.True(alice.World.TryGet(far, out _));
        Assert.False(alice.World.TryGet(near, out _));
    }

    [Fact]
    public void DespawnedEntity_DisappearsFromClients()
    {
        var rig = new Rig();
        var alice = rig.Join("alice");
        var bob = rig.Join("bob");
        rig.Run(5);

        rig.Server.World.Despawn(bob.PlayerEntityId);
        rig.Run(5);

        Assert.False(alice.World.TryGet(bob.PlayerEntityId, out _));
        Assert.True(alice.World.TryGet(alice.PlayerEntityId, out _));
    }

    [Fact]
    public void DisconnectedPlayer_IsRemovedForEveryoneElse()
    {
        var rig = new Rig();
        var alice = rig.Join("alice");
        var bobTransport = rig.Network.Connect();
        var bob = new GameClient(bobTransport, Identity, "bob");
        rig.Clients.Add(bob);
        rig.Run(5);
        Assert.True(alice.World.TryGet(bob.PlayerEntityId, out _));

        bobTransport.Dispose();
        rig.Run(5);

        Assert.Equal(1, rig.Server.PlayerCount);
        Assert.False(alice.World.TryGet(bob.PlayerEntityId, out _));
    }

    [Fact]
    public void QuietWorld_SendsSnapshotsWithoutEntityData()
    {
        var rig = new Rig();
        rig.Join("alice");
        rig.Join("bob");
        rig.Run(10);
        var bytes = rig.Server.SnapshotBytesSent;
        var count = rig.Server.SnapshotsSent;

        rig.Run(30);

        // Header only: type, sequence, tick, acknowledged input, Stamina, exhausted flag, carried mass, baseline, and an entry count of zero.
        Assert.Equal(1 + 4 + 8 + 4 + 4 + 1 + 4 + 4 + 2, (rig.Server.SnapshotBytesSent - bytes) / (rig.Server.SnapshotsSent - count));
    }

    [Fact]
    public void LossyNetwork_StillConvergesToTheServersState()
    {
        var rig = new Rig(dropEveryNthUnreliable: 3);
        var alice = rig.Join("alice");
        var bob = rig.Join("bob");
        rig.Run(5);

        var position = rig.Server.Options.SpawnPoint;
        for (var i = 0; i < 90; i++)
        {
            position += new Vector3(0.1f, 0, 0.05f);
            alice.Send(new MovePlayer(position, i * 0.01f));
            rig.Run(1);
        }

        rig.Network.DropEveryNthUnreliable = 0;
        rig.Run(5);

        Assert.True(rig.Server.World.TryGet(alice.PlayerEntityId, out var truth));
        Assert.True(bob.World.TryGet(alice.PlayerEntityId, out var seen));
        Assert.Equal(truth, seen);
        Assert.Equal(position, seen.Position);
    }

    [Fact]
    public void SteadyStateTick_WithTwoClientsMoving_DoesNotAllocate()
    {
        var rig = new Rig();
        var alice = rig.Join("alice");
        var bob = rig.Join("bob");
        var mover = new Mover(alice, rig.Server.Options.SpawnPoint);
        var simulation = new Simulation(mover, rig.Server, alice, bob);

        simulation.Run(60);
        var allocating = new List<string>();
        for (var i = 0; i < 300; i++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            simulation.Run(1);
            var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            if (bytes != 0)
            {
                allocating.Add($"steady-state tick {i} allocated {bytes} bytes");
            }
        }

        Assert.Equal(ClientState.Joined, alice.State);
        Assert.True(bob.World.TryGet(alice.PlayerEntityId, out var seen));
        Assert.NotEqual(rig.Server.Options.SpawnPoint, seen.Position);
        Assert.True(allocating.Count == 0, string.Join("; ", allocating));
    }

    [Fact]
    public void LiteNetLib_ClientJoinsOverUdp_AndReceivesSnapshots()
    {
        using var serverTransport = LiteNetTransport.Listen(0, "zombies-test", maxConnections: 4);
        var server = new GameServer(serverTransport, new ServerOptions(Identity, 42));
        using var clientTransport = LiteNetTransport.Connect("127.0.0.1", serverTransport.LocalPort, "zombies-test");
        var client = new GameClient(clientTransport, Identity, "udp");

        var deadline = DateTime.UtcNow.AddSeconds(10);
        long tick = 0;
        while (client.SnapshotsReceived < 3 && DateTime.UtcNow < deadline)
        {
            server.Tick(tick++);
            client.Poll();
            Thread.Sleep(5);
        }

        Assert.Equal(ClientState.Joined, client.State);
        Assert.Equal(42UL, client.WorldSeed);
        Assert.True(client.World.TryGet(client.PlayerEntityId, out _));
    }

    [Fact]
    public void InterestChunkSize_MatchesTheVoxelChunk() => Assert.Equal(ChunkConstants.Size, EntityState.ChunkSize);

    [Fact]
    public void PredictedInput_At150Milliseconds_MatchesTheServerExactly()
    {
        // Five polls of latency each way is about 150 ms at 30 Hz.
        var rig = new Rig(latencyPolls: 5);
        var alice = rig.Join("alice");
        rig.Run(20);

        var input = new PlayerInput(1f, 0f, 0.4f, 0f, false, false, false, false, false);
        for (var i = 0; i < 60; i++)
        {
            alice.SendInput(input);
            rig.Run(1);
        }

        rig.Run(20);

        Assert.True(rig.Server.World.TryGet(alice.PlayerEntityId, out var truth));
        Assert.Equal(truth.Position, alice.Local.State.Position);
        Assert.Equal(0, alice.Local.PendingCount);
        Assert.Equal(0, alice.Local.ReconciliationCount);
        Assert.Equal(0f, alice.Local.LastCorrectionDistance);
    }

    [Theory]
    [InlineData(false, false, 0)]
    [InlineData(true, false, 0)]
    [InlineData(false, true, 0)]
    [InlineData(true, false, 35)]
    public void PredictedMovementModes_MatchTheServer_IncludingStaminaAndExhaustion(bool sprint, bool crouch, int carriedKilograms)
    {
        var anvil = new ItemId("test:item/anvil");
        var items = new ItemCatalog([new ItemDefinition(anvil, Mass.FromKilograms(1), Volume.FromLiters(1), maxStack: 64)]);
        var rig = new Rig(new ServerOptions(Identity, WorldSeed: 777) { Items = items }, latencyPolls: 5);
        var alice = rig.Join("alice");
        rig.Run(10);
        Assert.True(rig.Server.TryGetPlayer(new ConnectionId(1), out var session));
        if (carriedKilograms > 0)
        {
            Assert.True(session!.Carried.TryAdd(anvil, carriedKilograms).IsSuccess);
        }

        rig.Run(10);

        // Long enough to run a sprinting player dry, and to see sprinting come back once the pool recovers.
        var input = new PlayerInput(1f, 0f, 0.4f, 0f, sprint, crouch, false, false, false);
        for (var i = 0; i < 240; i++)
        {
            alice.SendInput(input);
            rig.Run(1);
        }

        rig.Run(20);

        Assert.True(rig.Server.World.TryGet(alice.PlayerEntityId, out var truth));
        Assert.Equal(truth.Position, alice.Local.State.Position);
        Assert.Equal(session!.Movement.Stamina, alice.Local.State.Stamina);
        Assert.Equal(session.Movement.Exhausted, alice.Local.State.Exhausted);
        Assert.Equal(session.Movement.Noise, alice.Local.State.Noise);
        Assert.Equal(0, alice.Local.ReconciliationCount);
        Assert.Equal(0f, alice.Local.MaxCorrectionDistance);
        Assert.Equal(sprint || carriedKilograms > 0, session.Movement.Stamina < PlayerMovement.FullStamina || session.Movement.Exhausted);
    }

    [Fact]
    public void ALocalPlayer_WhoseStaminaDiffersFromTheServers_TakesTheServersStamina()
    {
        var local = new LocalPlayer();
        local.Reset(PlayerMoveState.At(Vector3.Zero));
        var input = new PlayerInput(1f, 0f, 0f, 0f, true, false, false, false, false);
        for (uint sequence = 1; sequence <= 10; sequence++)
        {
            local.Predict(sequence, input, FlatFloorCollision.Instance);
        }

        // The Server says that at input 6 the player had little Stamina left; the four inputs after it replay from there.
        local.Reconcile(Vector3.Zero, 0f, new PlayerVitals(0.1f, false, 0f), 6, FlatFloorCollision.Instance);

        Assert.True(local.State.Stamina < 0.1f);
        Assert.Equal(4, local.PendingCount);
    }

    [Fact]
    public void LocalMovement_IsInstant_EvenWhileTheServerIsBehind()
    {
        var rig = new Rig(latencyPolls: 5);
        var alice = rig.Join("alice");
        rig.Run(20);

        var start = alice.Local.State.Position;
        var input = new PlayerInput(1f, 0f, 0f, 0f, false, false, false, false, false);
        for (var i = 0; i < 10; i++)
        {
            alice.SendInput(input);
            rig.Run(1);
        }

        // The Server has not seen these inputs yet, but the local player has already moved.
        Assert.True(alice.Local.State.Position.Z < start.Z - 0.3f);
        Assert.True(alice.Local.PendingCount > 0);
    }

    [Fact]
    public void AServerCorrection_IsReconciled_AndTheUnconfirmedInputsAreReplayed()
    {
        // Latency keeps inputs in flight, so the correction has something to replay.
        var rig = new Rig(latencyPolls: 5);
        var alice = rig.Join("alice");
        rig.Run(20);

        var input = new PlayerInput(1f, 0f, 0f, 0f, false, false, false, false, false);
        for (var i = 0; i < 10; i++)
        {
            alice.SendInput(input);
            rig.Run(1);
        }

        Assert.True(alice.Local.PendingCount > 0);

        // Teleport the Server's copy, as a rubber-band or a knock-back would.
        var shoved = rig.Server.Options.SpawnPoint + new Vector3(0, 0, -3f);
        Assert.True(rig.Server.TryGetPlayer(new ConnectionId(1), out var session));
        Assert.True(rig.Server.Teleport(session!, shoved, 0f));

        rig.Run(20);

        Assert.True(alice.Local.ReconciliationCount > 0);
        Assert.True(alice.Local.MaxCorrectionDistance > 0f);
        Assert.True(rig.Server.World.TryGet(alice.PlayerEntityId, out var truth));
        Assert.Equal(truth.Position, alice.Local.State.Position);
    }

    [Fact]
    public void ARejectedInput_IsReported_AndDoesNotMoveThePlayer()
    {
        var rig = new Rig();
        var alice = rig.Join("alice");
        rig.Run(10);

        var before = alice.Local.State.Position;
        var sequence = alice.Send(new PlayerInputCommand(new PlayerInput(1f, 0f, float.NaN, 0f, false, false, false, false, false)));
        rig.Run(5);

        Assert.Equal(sequence, alice.LastRejection?.Sequence);
        Assert.Equal(CommandRejection.Invalid, alice.LastRejection?.Reason);
        Assert.Equal(before, alice.Local.State.Position);
    }

    [Fact]
    public void TwoClients_Walking_SeeEachOtherMoveSmoothly()
    {
        var rig = new Rig();
        var alice = rig.Join("alice");
        var bob = rig.Join("bob");
        rig.Run(10);

        var input = new PlayerInput(1f, 0f, 0f, 0f, false, false, false, false, false);

        // Let both players fall to the floor before measuring, so the drop is not mistaken for a jump.
        for (var i = 0; i < 150; i++)
        {
            alice.SendInput(input);
            bob.SendInput(input);
            rig.Run(1);
        }

        Assert.True(rig.Server.World.TryGet(alice.PlayerEntityId, out var landed));
        Assert.True(landed.Position.Y < 1f, $"the player should have landed, but is at y {landed.Position.Y}.");

        var previous = landed.Position;
        var maxJump = 0f;
        for (var i = 0; i < 90; i++)
        {
            alice.SendInput(input);
            bob.SendInput(input);
            rig.Run(1);

            Assert.True(rig.Server.World.TryGet(alice.PlayerEntityId, out var truth));
            maxJump = MathF.Max(maxJump, Vector3.Distance(previous, truth.Position));
            previous = truth.Position;
        }

        // No tick ever teleports a player: every step is a normal walking step.
        Assert.True(maxJump < PlayerMovement.MaxStepPerTick, $"a player jumped {maxJump} blocks in one tick.");
        Assert.True(previous.Z < landed.Position.Z - 5f);

        // And the other client sees the same thing, within the one snapshot it may be behind.
        Assert.True(bob.World.TryGet(alice.PlayerEntityId, out var seen));
        Assert.True(Vector3.Distance(previous, seen.Position) < PlayerMovement.MaxStepPerTick * 2f);
    }

    private sealed class Mover(GameClient client, Vector3 start) : ITickable
    {
        private Vector3 _position = start;

        public void Tick(long tick)
        {
            if (client.State == ClientState.Joined)
            {
                _position.X += (tick & 1) == 0 ? 0.2f : -0.1f;
                client.Send(new MovePlayer(_position, tick * 0.01f));
            }
        }
    }

    private sealed class RecordingHandler : ITransportHandler
    {
        public bool Disconnected { get; private set; }

        public void OnConnected(ConnectionId connection)
        {
        }

        public void OnReceived(ConnectionId connection, ReadOnlySpan<byte> payload, Delivery delivery)
        {
        }

        public void OnDisconnected(ConnectionId connection) => Disconnected = true;
    }
}

public sealed class EmbeddedServerTests
{
    [Fact]
    public void EmbeddedServer_JoinsAtOnce_AndReplicatesThePlayer()
    {
        var identity = new GameIdentity(NetProtocol.Version, WorldGenerator.GeneratorVersion, []);
        using var solo = new EmbeddedServer(new ServerOptions(identity, WorldSeed: 99), "solo");

        solo.Advance(TimeSpan.FromSeconds(0.2));

        Assert.Equal(ClientState.Joined, solo.Client.State);
        Assert.Equal(99UL, solo.Client.WorldSeed);
        Assert.True(solo.Client.World.TryGet(solo.Client.PlayerEntityId, out var player));
        Assert.Equal(EntityKind.Player, player.Kind);
    }
}
