using System.Numerics;
using UnitsNet;
using Zombies.Domain.Crafting;
using Zombies.Domain.Items;
using Zombies.Domain.Mods;
using Zombies.Domain.Survival;
using Zombies.Engine.Core;
using Zombies.Engine.Core.Modding;
using Zombies.Engine.Net;
using Zombies.Engine.Voxel;

namespace Zombies.Engine.Tests;

/// <summary>Professions and Scenarios, driven through a Server and fake clients over the in-memory transport.</summary>
public sealed class ProfessionTests
{
    private static readonly GameIdentity Identity = new(NetProtocol.Version, WorldGenerator.GeneratorVersion, [new ModFingerprint("base", "1.0.0", "aa")]);

    private static readonly ItemId Bandage = new("test:item/bandage");
    private static readonly ItemId Pills = new("test:item/pills");
    private static readonly ItemId Wrench = new("test:item/wrench");
    private static readonly ItemId Scrubs = new("test:item/scrubs");
    private static readonly ItemId Shirt = new("test:item/shirt");
    private static readonly ItemId Jacket = new("test:item/jacket");
    private static readonly ItemId Beans = new("test:item/beans");

    private static readonly ItemCatalog Items = new(
    [
        new ItemDefinition(Bandage, Mass.FromKilograms(0.02), Volume.FromLiters(0.1), maxStack: 10),
        new ItemDefinition(Pills, Mass.FromKilograms(0.05), Volume.FromLiters(0.1), maxStack: 1),
        new ItemDefinition(Wrench, Mass.FromKilograms(1), Volume.FromLiters(1), maxStack: 1),
        new ItemDefinition(Scrubs, Mass.FromKilograms(0.4), Volume.FromLiters(1), maxStack: 1),
        new ItemDefinition(Shirt, Mass.FromKilograms(0.2), Volume.FromLiters(0.5), maxStack: 1),
        new ItemDefinition(Jacket, Mass.FromKilograms(1), Volume.FromLiters(3), maxStack: 1),
        new ItemDefinition(Beans, Mass.FromKilograms(0.4), Volume.FromLiters(0.35), maxStack: 4),
    ]);

    private static readonly WearableCatalog Wearables = new(
    [
        new WearableDefinition(Scrubs, ClothingLayer.Base, [BodyPart.Torso], ThermalResistance.FromSquareMeterKelvinsPerWatt(0.05)),
        new WearableDefinition(Shirt, ClothingLayer.Base, [BodyPart.Torso], ThermalResistance.FromSquareMeterKelvinsPerWatt(0.05)),
        new WearableDefinition(Jacket, ClothingLayer.Outer, [BodyPart.Torso], ThermalResistance.FromSquareMeterKelvinsPerWatt(0.1)),
    ]);

    private static readonly LootTableCatalog Loot = new(
    [
        new LootTable("test:loot/pantry", 3, 3, [new LootEntry(Beans, 1, 1, 3), new LootEntry(Pills, 1, 1, 1), new LootEntry(Bandage, 1, 1, 5)]),
    ]);

    private static readonly Profession Nurse = ProfessionJson.Parse("""
        {
          "id": "test:profession/nurse",
          "items": [ { "item": "test:item/bandage", "count": 4 }, { "item": "test:item/pills" } ],
          "outfit": [ "test:item/scrubs" ],
          "modifiers": [ { "stat": "treatment_speed", "operation": "multiply", "value": 1.5 } ]
        }
        """);

    private static readonly Profession Mechanic = ProfessionJson.Parse("""
        {
          "id": "test:profession/mechanic",
          "items": [ { "item": "test:item/wrench" } ],
          "outfit": [ "test:item/jacket" ]
        }
        """);

    private static readonly Profession Cook = ProfessionJson.Parse("""
        {
          "id": "test:profession/cook",
          "loot": [ "test:loot/pantry", "test:loot/pantry" ]
        }
        """);

    private static readonly Scenario Stranded = ScenarioJson.Parse("""
        {
          "id": "test:scenario/stranded",
          "startLocation": "wilderness",
          "timeOfDay": 0.75,
          "startingCondition": {
            "satiety": 0.5,
            "hydration": 0.25,
            "wounds": [ { "part": "leftLeg", "damageType": "cut", "damage": 6 } ]
          }
        }
        """);

    private static ServerOptions Options(Scenario? scenario = null, ulong seed = 7, IEnumerable<Profession>? professions = null) => new(Identity, seed)
    {
        Items = Items,
        Wearables = Wearables,
        Loot = Loot,
        Professions = new ProfessionCatalog(professions ?? [Nurse, Mechanic, Cook]),
        Scenario = scenario,
        DayLength = TimeSpan.FromSeconds(10),
    };

    private sealed class Rig
    {
        private readonly List<GameClient> _clients = [];
        private long _tick;

        public Rig(ServerOptions options) => Server = new GameServer(Network.CreateServer(), options);

        public InMemoryNetwork Network { get; } = new();

        public GameServer Server { get; }

        public GameClient Join(string name, string? profession = null)
        {
            var client = new GameClient(Network.Connect(), Identity, name, profession);
            _clients.Add(client);
            Run(5);
            return client;
        }

        public PlayerSession Session(GameClient client) =>
            Server.TryGetPlayer(new ConnectionId(_clients.IndexOf(client) + 1), out var session) ? session : throw new InvalidOperationException("The client has not joined.");

        public void Run(int ticks)
        {
            for (var i = 0; i < ticks; i++)
            {
                Server.Tick(_tick++);
                foreach (var client in _clients)
                {
                    client.Poll();
                }
            }
        }
    }

    private static Dictionary<ItemId, int> Carried(PlayerSession session) =>
        session.Carried.Stacks.GroupBy(s => s.Item).ToDictionary(g => g.Key, g => g.Sum(s => s.Count));

    [Fact]
    public void AProfession_GrantsItsItemsAndOutfit_AndItsModifiers()
    {
        var rig = new Rig(Options());

        var alice = rig.Join("alice", Nurse.Id);

        Assert.Equal(ClientState.Joined, alice.State);
        var session = rig.Session(alice);
        Assert.Equal(Nurse.Id, session.Profession);
        Assert.Equal(new Dictionary<ItemId, int> { [Bandage] = 4, [Pills] = 1 }, Carried(session));
        Assert.Equal([Scrubs], session.Outfit.WornItems);
        var modifier = Assert.Single(session.Modifiers.All);
        Assert.Equal((new StatName("treatment_speed"), ModifierOperation.Multiply, 1.5, new ModifierSource(Nurse.Id)), (modifier.Stat, modifier.Operation, modifier.Value, modifier.Source));
        Assert.Equal(1.5, session.Modifiers.EffectiveValue(new StatName("treatment_speed"), 1));
    }

    [Fact]
    public void EachCoOpPlayer_PicksTheirOwnProfession()
    {
        var rig = new Rig(Options());

        var alice = rig.Join("alice", Nurse.Id);
        var bob = rig.Join("bob", Mechanic.Id);
        var carol = rig.Join("carol");

        Assert.Equal(Nurse.Id, rig.Session(alice).Profession);
        Assert.Equal(Mechanic.Id, rig.Session(bob).Profession);
        Assert.Equal([Jacket], rig.Session(bob).Outfit.WornItems);
        Assert.Equal(new Dictionary<ItemId, int> { [Wrench] = 1 }, Carried(rig.Session(bob)));
        Assert.Null(rig.Session(carol).Profession);
        Assert.Empty(rig.Session(carol).Carried.Stacks);
        Assert.Empty(rig.Session(carol).Outfit.WornItems);
        Assert.Empty(rig.Session(carol).Modifiers.All);
    }

    [Fact]
    public void ALoadoutRolledFromLootTables_IsTheSameForTheSamePlayerInTheSameWorld()
    {
        var first = new Rig(Options(seed: 42));
        var second = new Rig(Options(seed: 42));

        var a = Carried(first.Session(first.Join("alice", Cook.Id)));
        var b = Carried(second.Session(second.Join("alice", Cook.Id)));

        Assert.NotEmpty(a);
        Assert.Equal(a, b);
    }

    [Fact]
    public void ALoadoutRolledFromLootTables_DiffersBetweenPlayersAndWorlds()
    {
        var rolls = new HashSet<string>();
        foreach (var (seed, name) in new[] { (1UL, "alice"), (1UL, "bob"), (2UL, "alice"), (3UL, "alice"), (4UL, "alice"), (5UL, "bob") })
        {
            var rig = new Rig(Options(seed: seed));
            rolls.Add(string.Join(",", Carried(rig.Session(rig.Join(name, Cook.Id))).OrderBy(c => c.Key.Value).Select(c => $"{c.Key}:{c.Value}")));
        }

        Assert.True(rolls.Count > 1);
    }

    [Fact]
    public void AnUnknownProfession_RefusesTheJoin()
    {
        var rig = new Rig(Options());

        var client = rig.Join("alice", "test:profession/astronaut");

        Assert.Equal(ClientState.Refused, client.State);
        Assert.Equal(JoinRefusal.UnknownProfession, client.Refusal);
        Assert.Contains("test:profession/astronaut", client.RefusalDetail);
        Assert.Equal(0, rig.Server.PlayerCount);
    }

    [Fact]
    public void AServerWithoutProfessions_RefusesAJoinThatNamesOne()
    {
        var rig = new Rig(Options(professions: []));

        Assert.Equal(JoinRefusal.UnknownProfession, rig.Join("alice", Nurse.Id).Refusal);
        Assert.Equal(ClientState.Joined, rig.Join("bob").State);
    }

    [Fact]
    public void AScenario_StartsThePlayerHungryAndWoundedAtItsTimeOfDay()
    {
        var rig = new Rig(Options(Stranded));

        var alice = rig.Join("alice");

        var session = rig.Session(alice);
        Assert.Equal((0.5, 0.25), (session.Needs.Satiety, session.Needs.Hydration));
        var wound = Assert.Single(session.Body.Wounds);
        Assert.Equal((BodyPart.LeftLeg, DamageType.Cut), (wound.Part, wound.Type));
        Assert.Equal(0.75, alice.StartTimeOfDay, 2);
    }

    [Fact]
    public void WithoutAScenario_PlayersStartUnhurtInTheMorning()
    {
        var rig = new Rig(Options());

        var alice = rig.Join("alice");

        Assert.Equal(1, rig.Session(alice).Needs.Satiety);
        Assert.Empty(rig.Session(alice).Body.Wounds);
        Assert.Equal(GameServer.DefaultStartTimeOfDay, alice.StartTimeOfDay, 2);
    }

    [Fact]
    public void TimeOfDay_AdvancesWithTheDayLength_AndWrapsAroundMidnight()
    {
        var rig = new Rig(Options(Stranded));
        Assert.Equal(0.75, rig.Server.TimeOfDay);

        rig.Run(Simulation.TickRateHz * 10 / 2);

        Assert.Equal(0.25, rig.Server.TimeOfDay, 2);
    }

    [Fact]
    public void AScenarioWithAStartPoint_PutsThePlayerThere()
    {
        var start = new Vector3(100, 80, -40);
        StartLocationKind? asked = null;
        var rig = new Rig(Options(Stranded) with
        {
            StartPoint = kind =>
            {
                asked = kind;
                return start;
            },
        });

        var alice = rig.Join("alice");

        Assert.Equal(StartLocationKind.Wilderness, asked);
        Assert.Equal(start, alice.Local.State.Position);
        Assert.True(rig.Server.World.TryGet(alice.PlayerEntityId, out var entity));
        Assert.Equal(start, entity.Position);
    }

    [Fact]
    public void AProfessionTheServerCannotGrant_IsRefusedWhenTheServerStarts()
    {
        var unknownItem = ProfessionJson.Parse("""{ "id": "test:profession/a", "items": [ { "item": "test:item/ghost" } ] }""");
        var unknownOutfit = ProfessionJson.Parse("""{ "id": "test:profession/a", "outfit": [ "test:item/ghost" ] }""");
        var unwearable = ProfessionJson.Parse("""{ "id": "test:profession/a", "outfit": [ "test:item/wrench" ] }""");
        var twoShirts = ProfessionJson.Parse("""{ "id": "test:profession/a", "outfit": [ "test:item/scrubs", "test:item/shirt" ] }""");
        var unknownLoot = ProfessionJson.Parse("""{ "id": "test:profession/a", "loot": [ "test:loot/ghost" ] }""");
        var tooHeavy = ProfessionJson.Parse("""{ "id": "test:profession/a", "items": [ { "item": "test:item/wrench", "count": 500 } ] }""");

        foreach (var profession in new[] { unknownItem, unknownOutfit, unwearable, twoShirts, unknownLoot, tooHeavy })
        {
            Assert.Throws<ArgumentException>(() => new Rig(Options(professions: [profession])));
        }
    }

    [Fact]
    public void BaseMod_OffersAtLeastThreeProfessionsAndAScenario_ThatEveryProfessionCanJoinWith()
    {
        var mods = ModLoader.Load(DirectoryModSource.Read(Path.Combine(RepoRoot(), "mods")));
        Assert.True(mods.IsSuccess, string.Join(Environment.NewLine, mods.Errors));
        var professions = StartingContentLoader.LoadProfessions(mods.Registry).All;
        var scenarios = StartingContentLoader.LoadScenarios(mods.Registry).All;
        Assert.True(professions.Count >= 3);
        Assert.NotEmpty(scenarios);
        var options = new ServerOptions(GameIdentity.From(mods, WorldGenerator.GeneratorVersion), 12345)
        {
            Items = new ItemCatalog(mods.Registry.OfKind("item").Select(d => ItemDefinitionJson.Parse(d.Json))),
            Wearables = StartingContentLoader.LoadWearables(mods.Registry),
            Loot = StartingContentLoader.LoadLoot(mods.Registry),
            Professions = StartingContentLoader.LoadProfessions(mods.Registry),
            Scenario = scenarios[0],
        };
        var network = new InMemoryNetwork();
        var server = new GameServer(network.CreateServer(), options);

        var clients = professions.Select((p, i) => new GameClient(network.Connect(), options.Identity, $"player{i}", p.Id)).ToList();
        for (var tick = 0; tick < 10; tick++)
        {
            server.Tick(tick);
            clients.ForEach(c => c.Poll());
        }

        Assert.All(clients, c => Assert.Equal(ClientState.Joined, c.State));
        for (var i = 0; i < clients.Count; i++)
        {
            Assert.True(server.TryGetPlayer(new ConnectionId(i + 1), out var session));
            Assert.Equal(professions[i].Id, session.Profession);
            Assert.Equal(professions[i].Outfit, session.Outfit.WornItems);
            Assert.All(professions[i].Items, item => Assert.True(Carried(session).GetValueOrDefault(item.Item) >= item.Count));
        }
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
}
