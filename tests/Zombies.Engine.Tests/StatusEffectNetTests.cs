using System.Numerics;
using Zombies.Domain.Combat;
using Zombies.Domain.Items;
using Zombies.Domain.StatusEffects;
using Zombies.Domain.Zombies;
using Zombies.Engine.Ecs;
using Zombies.Engine.Net;
using Zombies.Engine.Tests.Rigs;
using Zombies.Engine.Voxel;

namespace Zombies.Engine.Tests;

/// <summary>
/// Status effects in play, driven through a Server and fake clients over the in-memory transport: a bite can infect, consumables
/// apply and cure effects through a Domain command, and only the Server decides and only the affected player is told.
/// </summary>
public sealed class StatusEffectNetTests
{
    private static readonly GameIdentity Identity = new(NetProtocol.Version, WorldGenerator.GeneratorVersion, [new ModFingerprint("base", "1.0.0", "aa")]);

    private const string Infection = "test:status_effect/infection";
    private const string Painkiller = "test:status_effect/painkiller";
    private const string Poison = "test:status_effect/food_poisoning";

    private static readonly ItemId Antibiotics = new("test:item/antibiotics");
    private static readonly ItemId Pills = new("test:item/painkillers");
    private static readonly ItemId Spoiled = new("test:item/spoiled_food");
    private static readonly ItemId Crowbar = new("test:item/crowbar");

    private static readonly StatusEffectCatalog Effects = new(
        new[]
        {
            """
            { "id": "test:status_effect/infection", "category": "ailment",
              "stages": [ { "name": "mild", "after": 0 }, { "name": "severe", "after": 2 } ],
              "curedBy": { "items": [ "test:item/antibiotics" ] } }
            """,
            """{ "id": "test:status_effect/painkiller", "category": "buff", "duration": 3 }""",
            """{ "id": "test:status_effect/food_poisoning", "category": "ailment", "duration": 5 }""",
        }.Select(StatusEffectJson.Parse));

    private static readonly ItemCatalog Items = new(
        new[]
        {
            """{ "id": "test:item/antibiotics", "mass": "30 g", "volume": "120 ml", "maxStack": 5, "edible": true }""",
            """{ "id": "test:item/painkillers", "mass": "20 g", "volume": "60 ml", "maxStack": 10, "edible": true, "onConsume": [ { "effect": "test:status_effect/painkiller" } ] }""",
            """{ "id": "test:item/spoiled_food", "mass": "0.4 kg", "volume": "350 ml", "maxStack": 4, "edible": true, "onConsume": [ { "effect": "test:status_effect/food_poisoning", "chance": 1 } ] }""",
            """{ "id": "test:item/crowbar", "mass": "2 kg", "volume": "1 l" }""",
        }.Select(ItemDefinitionJson.Parse));

    private static string Biter(string name, double chance) => $$"""
        {
          "id": "test:zombie/{{name}}",
          "stats": { "partHealth": 40, "damage": 10, "speed": 1.4 },
          "senses": { "sight": 20, "hearing": 30 },
          "appearance": { "skinTones": ["#8fa07a"] },
          "bite": { "effect": "test:status_effect/infection", "chance": {{chance.ToString(System.Globalization.CultureInfo.InvariantCulture)}} }
        }
        """;

    private const string Harmless = """
        {
          "id": "test:zombie/harmless",
          "stats": { "partHealth": 40, "damage": 10, "speed": 1.4 },
          "senses": { "sight": 20, "hearing": 30 },
          "appearance": { "skinTones": ["#8fa07a"] }
        }
        """;

    private sealed class Rig
    {
        public Rig(ulong seed = 7)
        {
            Server = new GameServer(Network.CreateServer(), new ServerOptions(Identity, seed) { Items = Items, Effects = Effects });
            var traits = new TraitRegistry();
            BaseTraits.Register(traits);
            var catalog = new ZombieCatalog(new[] { Biter("always", 1), Biter("never", 0), Harmless }.Select(ZombieTypeJson.Parse));
            Zombies = new ZombieSystem(Server.World, catalog, traits, RigTestData.BaseSkeleton("humanoid"), RigTestData.BaseClips("humanoid"));
        }

        public InMemoryNetwork Network { get; } = new();

        public GameServer Server { get; }

        public ZombieSystem Zombies { get; }

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

        public uint Spawn(string type, int level = 1)
        {
            Assert.True(Zombies.TrySpawn(new ZombieSpec(1, $"test:zombie/{type}", level), Vector3.Zero, 0f, out var id, out var problem), problem);
            return id;
        }
    }

    private static (Rig Rig, GameClient Alice, GameClient Bob, PlayerSession AliceSession) TwoPlayers(ulong seed = 7)
    {
        var rig = new Rig(seed);
        var alice = rig.Join("alice");
        var bob = rig.Join("bob");
        rig.Run(10);
        return (rig, alice, bob, rig.Session(1));
    }

    [Fact]
    public void ABite_FromAZombieWhoseDataSaysItAlwaysInfects_AppliesInfectionAndWounds()
    {
        var (rig, alice, _, session) = TwoPlayers();
        var zombie = rig.Spawn("always");
        var before = session.Body.Health(BodyPart.Torso);

        var result = rig.Server.Bite(session, rig.Zombies, zombie, BodyPart.Torso);
        rig.Run(5);

        Assert.True(result.IsSuccess);
        Assert.True(session.Body.Health(BodyPart.Torso) < before);
        Assert.True(session.Effects.Has(Infection));
        var effect = Assert.Single(alice.Effects);
        Assert.Equal(Infection, effect.Effect);
        Assert.Equal("mild", effect.Stage);
    }

    [Fact]
    public void ABite_FromAZombieWhoseDataSaysItNeverInfects_OnlyWounds()
    {
        var (rig, alice, _, session) = TwoPlayers();
        var zombie = rig.Spawn("never");

        for (var i = 0; i < 3; i++)
        {
            Assert.True(rig.Server.Bite(session, rig.Zombies, zombie, BodyPart.LeftArm).IsSuccess);
        }

        rig.Run(5);

        Assert.Equal(0, session.Effects.Count);
        Assert.Empty(alice.Effects);
    }

    [Fact]
    public void ABite_FromAZombieWithNoBiteData_OnlyWounds()
    {
        var (rig, alice, _, session) = TwoPlayers();

        Assert.True(rig.Server.Bite(session, rig.Zombies, rig.Spawn("harmless"), BodyPart.Torso).IsSuccess);
        rig.Run(5);

        Assert.Empty(alice.Effects);
    }

    private static int Infections(ulong seed, int bites, int chanceBasis)
    {
        var rig = new Rig(seed);
        var alice = rig.Join("alice");
        rig.Run(10);
        var session = rig.Session(1);
        var infected = 0;
        for (var i = 0; i < bites; i++)
        {
            // Tiny wounds, so the player outlives the whole run.
            Assert.True(rig.Server.Bite(session, BodyPart.Torso, 0.001, new ZombieBite(Infection, chanceBasis)).IsSuccess);
            if (session.Effects.Has(Infection))
            {
                infected++;
                Assert.True(rig.Server.RemoveEffect(session, Infection).IsSuccess);
            }
        }

        Assert.NotNull(alice);
        return infected;
    }

    [Fact]
    public void TheChanceOfInfection_FollowsTheDataAndTheServersSeed()
    {
        const int bites = 400;

        var half = Infections(seed: 11, bites, chanceBasis: 5000);

        Assert.InRange(half, 140, 260);
        Assert.Equal(half, Infections(seed: 11, bites, chanceBasis: 5000));
        Assert.NotEqual(half, Infections(seed: 12, bites, chanceBasis: 5000));
        Assert.Equal(0, Infections(seed: 11, bites, chanceBasis: 0));
        Assert.Equal(bites, Infections(seed: 11, bites, chanceBasis: 10_000));
        Assert.InRange(Infections(seed: 11, bites, chanceBasis: 3000), 70, 170);
    }

    [Fact]
    public void ABiteThatKillsThePlayer_LeavesNoEffectOnTheNextLife()
    {
        var (rig, alice, _, session) = TwoPlayers();

        var result = rig.Server.Bite(session, BodyPart.Torso, 1000, new ZombieBite(Infection, 10_000));
        rig.Run(5);

        Assert.True(result.IsSuccess);
        Assert.True(session.IsDead);
        Assert.Equal(0, session.Effects.Count);
        Assert.Empty(alice.Effects);
    }

    [Fact]
    public void DyingWithAnEffect_ClearsItForTheNextLife()
    {
        var (rig, alice, _, session) = TwoPlayers();
        rig.Server.ApplyEffect(session, Infection);
        rig.Run(5);
        Assert.NotEmpty(alice.Effects);

        rig.Server.Damage(session, BodyPart.Torso, DamageType.Cut, 1000);
        rig.Run(5);

        Assert.Empty(alice.Effects);
    }

    [Fact]
    public void Effects_ReplicateToTheAffectedPlayerOnly()
    {
        var (rig, alice, bob, session) = TwoPlayers();

        rig.Server.ApplyEffect(session, Infection);
        rig.Server.ApplyEffect(session, Poison);
        rig.Run(10);

        Assert.Equal([Poison, Infection], alice.Effects.Select(e => e.Effect).Order().ToList());
        Assert.Empty(bob.Effects);
        Assert.Equal(0, rig.Session(2).Effects.Count);
    }

    [Fact]
    public void AnEffectsStages_AreReplicatedAsTimePasses_AndItsEndIsToo()
    {
        var (rig, alice, _, session) = TwoPlayers();
        rig.Server.ApplyEffect(session, Poison);
        rig.Server.ApplyEffect(session, Infection);
        rig.Run(15);
        Assert.Equal("mild", alice.Effects.Single(e => e.Effect == Infection).Stage);

        rig.Run(60);
        Assert.Equal("severe", alice.Effects.Single(e => e.Effect == Infection).Stage);
        Assert.True(alice.Effects.Single(e => e.Effect == Poison).Remaining < TimeSpan.FromSeconds(5));

        rig.Run(120);
        Assert.DoesNotContain(alice.Effects, e => e.Effect == Poison);
        Assert.Contains(alice.Effects, e => e.Effect == Infection);
    }

    [Fact]
    public void AnEffectLasts_AsLongAsItsDataSays_EvenOverManyTicks()
    {
        var (rig, alice, _, session) = TwoPlayers();
        rig.Server.ApplyEffect(session, Painkiller);

        rig.Run(88);
        Assert.Contains(session.Effects.Effects, e => e.Effect == Painkiller);

        rig.Run(5);
        Assert.Equal(0, session.Effects.Count);
        rig.Run(5);
        Assert.Empty(alice.Effects);
    }

    [Fact]
    public void UsingAConsumable_ThroughTheDomainCommand_AppliesItsEffectAndSpendsOne()
    {
        var (rig, alice, bob, session) = TwoPlayers();
        Assert.True(session.Carried.TryAdd(Pills, 3).IsSuccess);

        alice.Send(new UseItem(Pills));
        rig.Run(5);

        Assert.Equal(0, alice.RejectionCount);
        Assert.Equal(2, session.Carried.CountOf(Pills));
        Assert.Equal(Painkiller, Assert.Single(alice.Effects).Effect);
        Assert.Empty(bob.Effects);
    }

    [Fact]
    public void UsingSpoiledFood_Poisons()
    {
        var (rig, alice, _, session) = TwoPlayers();
        Assert.True(session.Carried.TryAdd(Spoiled, 1).IsSuccess);

        alice.Send(new UseItem(Spoiled));
        rig.Run(5);

        Assert.Equal(Poison, Assert.Single(alice.Effects).Effect);
        Assert.Equal(0, session.Carried.CountOf(Spoiled));
    }

    [Fact]
    public void UsingAMedicine_CuresTheEffectItCures()
    {
        var (rig, alice, _, session) = TwoPlayers();
        Assert.True(session.Carried.TryAdd(Antibiotics, 2).IsSuccess);
        rig.Server.ApplyEffect(session, Infection);
        rig.Run(5);
        Assert.NotEmpty(alice.Effects);

        alice.Send(new UseItem(Antibiotics));
        rig.Run(5);

        Assert.Equal(0, alice.RejectionCount);
        Assert.Empty(alice.Effects);
        Assert.Equal(1, session.Carried.CountOf(Antibiotics));
    }

    [Fact]
    public void AMedicineWithNothingToCure_IsRejectedAndKept()
    {
        var (rig, alice, _, session) = TwoPlayers();
        Assert.True(session.Carried.TryAdd(Antibiotics, 1).IsSuccess);

        alice.Send(new UseItem(Antibiotics));
        rig.Run(5);

        Assert.Equal(1, alice.RejectionCount);
        Assert.Equal(CommandRejection.Invalid, alice.LastRejection!.Reason);
        Assert.Equal(1, session.Carried.CountOf(Antibiotics));
    }

    [Fact]
    public void TheServerRejectsUsingWhatTheSenderDoesNotCarry_OrCannotConsume_OrDoesNotKnow()
    {
        var (rig, alice, bob, session) = TwoPlayers();
        Assert.True(session.Carried.TryAdd(Crowbar, 1).IsSuccess);

        alice.Send(new UseItem(Pills));
        alice.Send(new UseItem(Crowbar));
        alice.Send(new UseItem(new ItemId("test:item/made_up")));
        rig.Run(5);

        Assert.Equal(3, alice.RejectionCount);
        Assert.Equal(0, session.Effects.Count);
        Assert.Equal(1, session.Carried.CountOf(Crowbar));
        Assert.Empty(alice.Effects);
        Assert.Empty(bob.Effects);
    }

    [Fact]
    public void AClientCannotUseAnotherPlayersItems()
    {
        var (rig, alice, bob, session) = TwoPlayers();
        Assert.True(session.Carried.TryAdd(Pills, 1).IsSuccess);

        bob.Send(new UseItem(Pills));
        rig.Run(5);

        Assert.Equal(1, bob.RejectionCount);
        Assert.Equal(1, session.Carried.CountOf(Pills));
        Assert.Empty(alice.Effects);
    }

    [Fact]
    public void ADeadPlayerCannotUseItems()
    {
        var (rig, alice, _, session) = TwoPlayers();
        Assert.True(session.Carried.TryAdd(Pills, 1).IsSuccess);
        rig.Server.Damage(session, BodyPart.Torso, DamageType.Cut, 1000);
        rig.Run(5);

        alice.Send(new UseItem(Pills));
        rig.Run(5);

        Assert.Equal(CommandRejection.Dead, alice.LastRejection!.Reason);
    }

    [Fact]
    public void AMalformedUseCommand_IsRejectedAsMalformed()
    {
        var (rig, alice, _, _) = TwoPlayers();
        var writer = new NetWriter();
        writer.WriteByte((byte)MessageType.Command);
        writer.WriteUInt32(99);
        writer.WriteUInt16(UseItem.CommandId);
        writer.WriteString("not a content id");

        alice.SendRaw(writer.Written);
        rig.Run(5);

        Assert.Equal(CommandRejection.Malformed, alice.LastRejection!.Reason);
    }
}
