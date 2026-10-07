using System.Numerics;
using UnitsNet;
using Zombies.Domain.Combat;
using Zombies.Domain.Death;
using Zombies.Domain.Inventory;
using Zombies.Domain.Items;
using Zombies.Engine.Core;
using Zombies.Engine.Net;
using Zombies.Engine.Voxel;
using Zombies.Persistence.Sqlite;

namespace Zombies.Engine.Tests;

/// <summary>Death, respawn and corpse recovery, driven through a Server and fake clients over the in-memory transport.</summary>
public sealed class DeathTests
{
    private static readonly GameIdentity Identity = new(NetProtocol.Version, WorldGenerator.GeneratorVersion, [new ModFingerprint("base", "1.0.0", "aa")]);

    private static readonly ItemId Beans = new("base:item/canned_beans");
    private static readonly ItemId Rifle = new("base:item/rifle");
    private static readonly ItemId Scope = new("base:item/scope");
    private static readonly ItemId Jacket = new("base:item/jacket");

    private static readonly ItemState ScopedRifle = ItemFaults.With(ItemState.Create([new("condition", 70), new("rounds", 5)], [new(Scope, 1)]), "base:fault/jammed_action");

    private static readonly ItemCatalog Items = new(
    [
        new ItemDefinition(Beans, Mass.FromKilograms(0.4), Volume.FromLiters(0.35), maxStack: 4),
        new ItemDefinition(Rifle, Mass.FromKilograms(3.5), Volume.FromLiters(4), maxStack: 1),
        new ItemDefinition(Scope, Mass.FromKilograms(0.5), Volume.FromLiters(0.4), maxStack: 1),
        new ItemDefinition(Jacket, Mass.FromKilograms(1), Volume.FromLiters(3), maxStack: 1),
    ]);

    private static readonly WearableCatalog Wearables = new(
    [
        new WearableDefinition(Jacket, ClothingLayer.Outer, [BodyPart.Torso], ThermalResistance.FromSquareMeterKelvinsPerWatt(0.1)),
    ]);

    private static ServerOptions Options(DeathOptions? death = null) => new(Identity, WorldSeed: 7)
    {
        Items = Items,
        Wearables = Wearables,
        Death = death ?? new DeathOptions { RespawnDelay = TimeSpan.FromSeconds(2) },
        DayLength = TimeSpan.FromSeconds(1),
    };

    private sealed class Rig
    {
        public Rig(ServerOptions? options = null, DeathStores? stores = null)
        {
            Server = new GameServer(Network.CreateServer(), options ?? Options(), stores);
        }

        public InMemoryNetwork Network { get; } = new();

        public GameServer Server { get; }

        public List<GameClient> Clients { get; } = [];

        public long Tick { get; private set; }

        public GameClient Join(string name)
        {
            var client = new GameClient(Network.Connect(), Identity, name);
            Clients.Add(client);
            return client;
        }

        public PlayerSession Session(int connection)
        {
            Assert.True(Server.TryGetPlayer(new ConnectionId(connection), out var session));
            return session;
        }

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

        /// <summary>Kills the player with one hit to the torso and lets the snapshots arrive.</summary>
        public void Kill(PlayerSession session)
        {
            var result = Server.Damage(session, BodyPart.Torso, DamageType.Cut, 1000);
            Assert.Contains(result.Events, e => e is BodyDied);
            Run(5);
        }
    }

    private static EntityState[] Corpses(GameClient client) => [.. client.World.Entities.ToArray().Where(e => e.Kind == EntityKind.Corpse)];

    private static void Arm(PlayerSession session)
    {
        Assert.True(session.Carried.TryAdd(Rifle, 1, ScopedRifle).IsSuccess);
        Assert.True(session.Carried.TryAdd(Beans, 6).IsSuccess);
        Assert.True(session.Outfit.Equip(Jacket).IsSuccess);
        Assert.True(session.Outfit.SetWearState(Jacket, wetness: 0.25, condition: 0.5).IsSuccess);
    }

    [Fact]
    public void ADeath_MovesWhatWasCarriedAndWornIntoACorpseAtTheDeathPosition()
    {
        var stores = DeathStores.InMemory();
        var rig = new Rig(stores: stores);
        var alice = rig.Join("alice");
        rig.Run(10);
        var session = rig.Session(1);
        Arm(session);
        var fell = new Vector3(20, 70, -5);
        Assert.True(rig.Server.Teleport(session, fell, 1f));

        rig.Kill(session);

        Assert.Empty(session.Carried.Stacks);
        Assert.Empty(session.Outfit.WornItems);
        var corpse = Assert.Single(stores.Corpses.All());
        Assert.Equal(fell, corpse.Position);
        Assert.True(stores.Containers.TryGet(corpse.Container, out var container));
        Assert.Equal(6, container.CountOf(Beans));
        Assert.Equal(1, container.CountOf(Rifle, ScopedRifle));
        Assert.Equal(1, container.CountOf(Jacket, ItemState.Create([new("wetness", 25), new("condition", 50)])));
        Assert.Equal(fell, Assert.Single(Corpses(alice)).Position);
    }

    [Fact]
    public void ADeath_ReplicatesToEveryClient_IncludingOneThatJoinsLater()
    {
        var rig = new Rig();
        var alice = rig.Join("alice");
        var bob = rig.Join("bob");
        rig.Run(10);

        rig.Kill(rig.Session(1));
        var carol = rig.Join("carol");
        rig.Run(10);

        foreach (var client in new[] { alice, bob, carol })
        {
            Assert.True(client.World.TryGet(alice.PlayerEntityId, out var dead));
            Assert.True(dead.Player.Dead);
            Assert.True(client.World.TryGet(bob.PlayerEntityId, out var living));
            Assert.False(living.Player.Dead);
            Assert.Single(Corpses(client));
        }

        Assert.True(alice.IsSpectating);
        Assert.False(bob.IsSpectating);
    }

    [Fact]
    public void TheMemorial_RecordsTheCauseDaysSurvivedAndKills()
    {
        var stores = DeathStores.InMemory();
        var rig = new Rig(stores: stores);
        rig.Join("alice");
        rig.Run(95);
        var session = rig.Session(1);
        session.CreditKill();
        session.CreditKill();

        rig.Kill(session);

        // A day lasts one second, 30 ticks, so a life of about 95 ticks is three days.
        Assert.Equal(new Memorial("alice", DaysSurvived: 3, Kills: 2, DeathCause.Trauma), Assert.Single(stores.Memorials.All()));
    }

    [Fact]
    public void TheMemorial_RecordsBloodLossAsTheCause()
    {
        var stores = DeathStores.InMemory();
        var rig = new Rig(stores: stores);
        rig.Join("alice");
        rig.Run(10);
        var session = rig.Session(1);
        Assert.True(rig.Server.Damage(session, BodyPart.LeftArm, DamageType.Cut, 90).IsSuccess);
        Assert.False(session.IsDead);

        rig.Server.AdvanceBody(session, TimeSpan.FromMinutes(10));

        Assert.True(session.IsDead);
        Assert.Equal(DeathCause.BloodLoss, Assert.Single(stores.Memorials.All()).Cause);
    }

    [Fact]
    public void ADeadPlayer_HasEveryCommandRejected_AndDoesNotMove()
    {
        var rig = new Rig();
        var alice = rig.Join("alice");
        rig.Run(10);
        rig.Kill(rig.Session(1));
        Assert.True(rig.Server.World.TryGet(alice.PlayerEntityId, out var before));

        alice.Send(new MovePlayer(before.Position + new Vector3(1, 0, 0), 0f));
        alice.Send(new PlayerInputCommand(new PlayerInput(1f, 0f, 0f, 0f, false, false, false, false, false)));
        rig.Run(5);

        Assert.Equal(2, alice.RejectionCount);
        Assert.Equal(CommandRejection.Dead, alice.LastRejection?.Reason);
        Assert.True(rig.Server.World.TryGet(alice.PlayerEntityId, out var after));
        Assert.Equal(before.Position, after.Position);
    }

    [Fact]
    public void ASpectatingClient_SendsNoInput_AndKeepsItsPredictionAtTheDeathPosition()
    {
        var rig = new Rig();
        var alice = rig.Join("alice");
        rig.Run(10);
        var walking = new PlayerInput(1f, 0f, 0f, 0f, false, false, false, false, false);
        for (var i = 0; i < 5; i++)
        {
            alice.SendInput(walking);
            rig.Run(1);
        }

        rig.Kill(rig.Session(1));

        Assert.Equal(0u, alice.SendInput(walking));
        Assert.True(rig.Server.World.TryGet(alice.PlayerEntityId, out var truth));
        Assert.Equal(truth.Position, alice.Local.State.Position);
        Assert.Equal(0, alice.Local.PendingCount);
    }

    [Fact]
    public void ADeadPlayer_RespawnsAfterTheDelay_AtTheSpawnWithFreshNeedsAndBody()
    {
        var rig = new Rig();
        var alice = rig.Join("alice");
        var bob = rig.Join("bob");
        rig.Run(10);
        var session = rig.Session(1);
        session.Needs.Eat(0.5);
        Assert.True(rig.Server.Teleport(session, new Vector3(50, 70, 50), 0f));
        rig.Server.Damage(session, BodyPart.LeftArm, DamageType.Cut, 40);
        rig.Kill(session);

        // The delay is two seconds, 60 ticks, and five have passed.
        rig.Run(50);
        Assert.True(alice.IsSpectating);
        Assert.True(session.IsDead);

        rig.Run(10);

        Assert.False(session.IsDead);
        Assert.False(alice.IsSpectating);
        Assert.True(rig.Server.World.TryGet(alice.PlayerEntityId, out var respawned));
        Assert.Equal(rig.Server.Options.SpawnPoint, respawned.Position);
        Assert.False(respawned.Player.Dead);
        Assert.Empty(session.Body.Wounds);
        Assert.Equal(100, session.Body.Health(BodyPart.LeftArm));
        Assert.Equal(1, session.Needs.Satiety);
        Assert.True(bob.World.TryGet(alice.PlayerEntityId, out var seenByBob));
        Assert.False(seenByBob.Player.Dead);
        Assert.Equal(rig.Server.Options.SpawnPoint, seenByBob.Position);
        Assert.Equal(rig.Server.Options.SpawnPoint, alice.Local.State.Position);
    }

    [Fact]
    public void ARespawnedPlayer_CanActAgain_AndStartsANewLifeForTheMemorial()
    {
        var stores = DeathStores.InMemory();
        var rig = new Rig(stores: stores);
        var alice = rig.Join("alice");
        rig.Run(10);
        var session = rig.Session(1);
        session.CreditKill();
        rig.Kill(session);
        rig.Run(70);

        alice.Send(new MovePlayer(rig.Server.Options.SpawnPoint + new Vector3(1, 0, 0), 0f));
        rig.Run(5);

        Assert.Equal(0, alice.RejectionCount);
        Assert.Equal(0, session.Kills);
        Assert.Empty(session.Carried.Stacks);

        rig.Run(40);
        rig.Kill(session);

        // The second life began at the respawn and lasted 60 ticks, which is two days.
        Assert.Equal([new Memorial("alice", 0, 1, DeathCause.Trauma), new Memorial("alice", 2, 0, DeathCause.Trauma)], stores.Memorials.All());
    }

    [Fact]
    public void WithTheSpectateOnlyPolicy_ADeadPlayerNeverRespawns()
    {
        var rig = new Rig(Options(new DeathOptions { Policy = DeathPolicy.SpectateOnly }));
        var alice = rig.Join("alice");
        rig.Run(10);

        rig.Kill(rig.Session(1));
        rig.Run(600);

        Assert.True(alice.IsSpectating);
        Assert.True(rig.Session(1).IsDead);
    }

    [Fact]
    public void AnotherPlayer_CanLootTheCorpse_AndItsItemStateSurvivesTheMove()
    {
        var stores = DeathStores.InMemory();
        var rig = new Rig(stores: stores);
        var alice = rig.Join("alice");
        var bob = rig.Join("bob");
        rig.Run(10);
        Arm(rig.Session(1));
        rig.Kill(rig.Session(1));
        var corpseEntity = Assert.Single(Corpses(bob)).Id;

        bob.Send(new LootCorpse(corpseEntity));
        rig.Run(5);

        Assert.Equal(0, bob.RejectionCount);
        var carried = rig.Session(2).Carried;
        Assert.Equal(6, carried.CountOf(Beans));
        Assert.Equal(1, carried.CountOf(Rifle, ScopedRifle));
        Assert.Equal(1, carried.CountOf(Jacket, ItemState.Create([new("wetness", 25), new("condition", 50)])));
        Assert.Empty(Corpses(alice));
        Assert.Empty(Corpses(bob));
        Assert.Empty(stores.Corpses.All());
    }

    [Fact]
    public void ThePlayerWhoDied_CanRecoverTheirOwnCorpseAfterRespawning()
    {
        var rig = new Rig();
        var alice = rig.Join("alice");
        rig.Run(10);
        Arm(rig.Session(1));
        rig.Kill(rig.Session(1));
        rig.Run(70);

        alice.Send(new LootCorpse(Assert.Single(Corpses(alice)).Id));
        rig.Run(5);

        Assert.Equal(0, alice.RejectionCount);
        Assert.Equal(6, rig.Session(1).Carried.CountOf(Beans));
    }

    [Fact]
    public void ALooterWithLittleRoom_TakesWhatFits_AndTheCorpseStaysWithTheRest()
    {
        var rig = new Rig(new ServerOptions(Identity, 7)
        {
            Items = Items,
            Wearables = Wearables,
            CarryMass = Mass.FromKilograms(2),
            CarryVolume = Volume.FromLiters(2),
            DayLength = TimeSpan.FromSeconds(1),
        });
        var alice = rig.Join("alice");
        var bob = rig.Join("bob");
        rig.Run(10);
        Assert.True(rig.Session(1).Carried.TryAdd(Beans, 5).IsSuccess);
        Assert.True(rig.Session(2).Carried.TryAdd(Beans, 3).IsSuccess);
        rig.Kill(rig.Session(1));

        bob.Send(new LootCorpse(Assert.Single(Corpses(bob)).Id));
        rig.Run(5);

        Assert.Equal(5, rig.Session(2).Carried.CountOf(Beans));
        Assert.Single(Corpses(alice));
    }

    [Fact]
    public void LootingFromTooFarAway_OrAnUnknownCorpse_IsRejected()
    {
        var rig = new Rig();
        rig.Join("alice");
        var bob = rig.Join("bob");
        rig.Run(10);
        Arm(rig.Session(1));
        rig.Kill(rig.Session(1));
        var corpseEntity = Assert.Single(Corpses(bob)).Id;
        Assert.True(rig.Server.Teleport(rig.Session(2), rig.Server.Options.SpawnPoint + new Vector3(LootCorpse.Reach + 1, 0, 0), 0f));

        bob.Send(new LootCorpse(corpseEntity));
        bob.Send(new LootCorpse(9999));
        rig.Run(5);

        Assert.Equal(2, bob.RejectionCount);
        Assert.Equal(CommandRejection.Invalid, bob.LastRejection?.Reason);
        Assert.Empty(rig.Session(2).Carried.Stacks);
        Assert.Single(Corpses(bob));
    }

    [Fact]
    public void ADeadPlayer_CannotLootACorpse()
    {
        var rig = new Rig(Options(new DeathOptions { Policy = DeathPolicy.SpectateOnly }));
        var alice = rig.Join("alice");
        var bob = rig.Join("bob");
        rig.Run(10);
        Arm(rig.Session(1));
        rig.Kill(rig.Session(1));
        rig.Kill(rig.Session(2));

        bob.Send(new LootCorpse(Corpses(bob).First().Id));
        rig.Run(5);

        Assert.Equal(CommandRejection.Dead, bob.LastRejection?.Reason);
        Assert.Equal(2, Corpses(alice).Length);
    }

    [Fact]
    public void ACorpse_SurvivesASaveAndLoad_AndCanStillBeLooted()
    {
        var directory = Directory.CreateTempSubdirectory("zombies-death-").FullName;
        try
        {
            var path = Path.Combine(directory, "world.sqlite");
            var fell = new Vector3(12, 70, 9);
            using (var database = SaveDatabase.Open(path))
            {
                var rig = new Rig(stores: StoresIn(database));
                rig.Join("alice");
                rig.Run(10);
                Arm(rig.Session(1));
                Assert.True(rig.Server.Teleport(rig.Session(1), fell, 0f));
                rig.Kill(rig.Session(1));
            }

            using var reopened = SaveDatabase.Open(path);
            var stores = StoresIn(reopened);
            var restarted = new Rig(stores: stores);
            var bob = restarted.Join("bob");
            restarted.Run(10);

            var corpse = Assert.Single(Corpses(bob));
            Assert.Equal(fell, corpse.Position);
            Assert.True(restarted.Server.Teleport(restarted.Session(1), fell, 0f));
            bob.Send(new LootCorpse(corpse.Id));
            restarted.Run(5);

            var carried = restarted.Session(1).Carried;
            Assert.Equal(0, bob.RejectionCount);
            Assert.Equal(1, carried.CountOf(Rifle, ScopedRifle));
            Assert.Equal(6, carried.CountOf(Beans));
            Assert.Empty(stores.Corpses.All());
            Assert.Equal(DeathCause.Trauma, Assert.Single(stores.Memorials.All()).Cause);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static DeathStores StoresIn(SaveDatabase database) =>
        new(new SqliteContainerRepository(database, Items), new SqliteCorpseRepository(database), new SqliteMemorialRepository(database));
}
