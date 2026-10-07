using System.Numerics;
using UnitsNet;
using Zombies.Domain.Combat;
using Zombies.Domain.Items;
using Zombies.Domain.Mods;
using Zombies.Domain.Statistics;
using Zombies.Engine.Core;
using Zombies.Engine.Core.Modding;
using Zombies.Engine.Net;
using Zombies.Engine.Voxel;

namespace Zombies.Engine.Tests;

/// <summary>The base mod's statistics, achievements and conducts over an event stream, and how a Server keeps and replicates them.</summary>
public sealed class StatisticsTests
{
    private static readonly GameIdentity Identity = new(NetProtocol.Version, WorldGenerator.GeneratorVersion, [new ModFingerprint("base", "1.0.0", "aa")]);

    private static readonly StatisticsCatalog Base = LoadBaseCatalog();

    private static StatisticsCatalog LoadBaseCatalog()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Zombies.slnx")))
        {
            directory = directory.Parent;
        }

        var mods = ModLoader.Load(DirectoryModSource.Read(Path.Combine(directory?.FullName ?? throw new InvalidOperationException("Could not find the repository root."), "mods")));
        Assert.True(mods.IsSuccess, string.Join(Environment.NewLine, mods.Errors));
        return StatisticsContentLoader.Load(mods.Registry);
    }

    private static DamageTaken Hit(DamageType type) => new(new BodyId(1), BodyPart.Torso, type, 5);

    private static IEnumerable<IDomainEvent> Repeat(IDomainEvent domainEvent, int times) => Enumerable.Repeat(domainEvent, times);

    private static List<string> Play(PlayerStatistics player, IEnumerable<IDomainEvent> stream) => [.. stream.SelectMany(player.Record)];

    [Fact]
    public void BaseMod_DefinesFiveAchievementsAndTwoConducts()
    {
        Assert.Equal(5, Base.Achievements.Count);
        Assert.Equal(2, Base.Conducts.Count);
        Assert.All([.. Base.Achievements, .. Base.Conducts], goal => Assert.True(Base.TryGetStatistic(goal.Statistic, out _)));
    }

    [Fact]
    public void SurvivingSevenDays_CompletesAWeekAlive_OnlyWithinOneLife()
    {
        var player = new PlayerStatistics(Base);

        Assert.Empty(Play(player, Repeat(DaySurvived.Instance, 6)));
        player.EndRun();
        Assert.Empty(Play(player, Repeat(DaySurvived.Instance, 6)));
        Assert.Equal(["base:achievement/survive_seven_days"], Play(player, Repeat(DaySurvived.Instance, 1)));
    }

    [Fact]
    public void Kills_CompleteZombieSlayer_AcrossLives()
    {
        var player = new PlayerStatistics(Base);

        Assert.Empty(Play(player, Repeat(ZombieKilled.Instance, 30)));
        player.EndRun();

        Assert.Equal(["base:achievement/zombie_slayer"], Play(player, Repeat(ZombieKilled.Instance, 20)));
    }

    [Fact]
    public void LootedItems_CompleteScavenger_BySummingEachHaul()
    {
        var player = new PlayerStatistics(Base);

        Assert.Empty(Play(player, [new ItemsLooted(60), new ItemsLooted(39)]));
        Assert.Equal(["base:achievement/scavenger"], Play(player, [new ItemsLooted(1)]));
    }

    [Fact]
    public void WalkedDistance_CompletesMarathon()
    {
        var player = new PlayerStatistics(Base);

        Assert.Empty(Play(player, Repeat(DistanceWalked.Step, 999)));
        Assert.Equal(["base:achievement/marathon"], Play(player, Repeat(DistanceWalked.Step, 1)));
    }

    [Fact]
    public void OnlyABite_CompletesFirstBite()
    {
        var player = new PlayerStatistics(Base);

        Assert.Empty(Play(player, [Hit(DamageType.Cut), Hit(DamageType.Blunt)]));
        Assert.Equal(["base:achievement/first_bite"], Play(player, [Hit(DamageType.Bite)]));
    }

    [Fact]
    public void ARunWithNoKillsAndNoHits_KeepsBothConducts()
    {
        var player = new PlayerStatistics(Base);

        var run = player.EndRun();

        Assert.Equal(["base:conduct/pacifist", "base:conduct/untouched"], run.ConductsKept);
    }

    [Fact]
    public void AKill_BreaksPacifist_AndAHit_BreaksUntouched()
    {
        var killer = new PlayerStatistics(Base);
        killer.Record(ZombieKilled.Instance);
        var hurt = new PlayerStatistics(Base);
        hurt.Record(Hit(DamageType.Blunt));

        Assert.Equal(["base:conduct/untouched"], killer.EndRun().ConductsKept);
        Assert.Equal(["base:conduct/pacifist"], hurt.EndRun().ConductsKept);
    }

    [Fact]
    public void TheScoresOfARun_AreTheStatisticsThatCountOneLife()
    {
        var player = new PlayerStatistics(Base);
        Play(player, [DaySurvived.Instance, DaySurvived.Instance, ZombieKilled.Instance, Hit(DamageType.Bite), new ItemsLooted(4)]);

        var run = player.EndRun();

        Assert.Equal(
            [
                new StatisticValue("base:statistic/days_survived", 2),
                new StatisticValue("base:statistic/hits_taken_this_life", 1),
                new StatisticValue("base:statistic/kills_this_life", 1),
            ],
            run.Scores);
    }

    private sealed class Rig(GameServer server, InMemoryNetwork network)
    {
        public GameServer Server { get; } = server;

        public InMemoryNetwork Network { get; } = network;

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
    }

    private static readonly ItemId Beans = new("base:item/canned_beans");

    private static Rig NewRig(IStatisticsRepository? repository = null, TimeSpan? dayLength = null)
    {
        var network = new InMemoryNetwork();
        var options = new ServerOptions(Identity, WorldSeed: 7)
        {
            Items = new ItemCatalog([new ItemDefinition(Beans, Mass.FromKilograms(0.4), Volume.FromLiters(0.35), maxStack: 4)]),
            Statistics = Base,
            Death = new Zombies.Domain.Death.DeathOptions { RespawnDelay = TimeSpan.FromSeconds(2) },
            DayLength = dayLength ?? TimeSpan.FromSeconds(1),
        };
        return new Rig(new GameServer(network.CreateServer(), options, statistics: repository), network);
    }

    private static void Kill(Rig rig, PlayerSession session)
    {
        Assert.Contains(rig.Server.Damage(session, BodyPart.Torso, DamageType.Cut, 1000).Events, e => e is BodyDied);
        rig.Run(5);
    }

    [Fact]
    public void CompletingAnAchievement_IsReplicatedToThePlayerWhoCompletedItAndNobodyElse()
    {
        var rig = NewRig();
        var alice = rig.Join("alice");
        var bob = rig.Join("bob");
        rig.Run(10);

        for (var i = 0; i < 50; i++)
        {
            rig.Session(1).CreditKill();
        }

        rig.Run(5);

        Assert.True(alice.TryTakeCompletedAchievement(out var achievement));
        Assert.Equal("base:achievement/zombie_slayer", achievement);
        Assert.False(alice.TryTakeCompletedAchievement(out _));
        Assert.False(bob.TryTakeCompletedAchievement(out _));
    }

    [Fact]
    public void ADeath_SendsThePlayerTheirScoresAndKeptConducts_AndStartsTheNextLifeFromZero()
    {
        var rig = NewRig();
        var alice = rig.Join("alice");
        rig.Run(95);
        var session = rig.Session(1);
        Assert.True(rig.Server.Damage(session, BodyPart.Torso, DamageType.Cut, 1).IsSuccess);

        Kill(rig, session);

        Assert.NotNull(alice.LastRun);
        Assert.Equal(new StatisticValue("base:statistic/days_survived", 3), alice.LastRun.Scores.Single(s => s.Statistic == "base:statistic/days_survived"));
        Assert.Equal(["base:conduct/pacifist"], alice.LastRun.ConductsKept);
        Assert.Equal(0, session.Statistics.Value("base:statistic/days_survived"));
    }

    [Fact]
    public void Statistics_SurviveARespawn_AndAchievementsAreNotAwardedTwice()
    {
        var rig = NewRig();
        var alice = rig.Join("alice");
        rig.Run(10);
        var session = rig.Session(1);
        for (var i = 0; i < 50; i++)
        {
            session.CreditKill();
        }

        Kill(rig, session);
        rig.Run(90);

        Assert.False(session.IsDead);
        Assert.Equal(50, session.Statistics.Value("base:statistic/zombies_killed"));
        Assert.Equal(0, session.Statistics.Value("base:statistic/kills_this_life"));
        Assert.Equal(["base:achievement/zombie_slayer"], session.Statistics.Completed);
        Assert.True(alice.TryTakeCompletedAchievement(out _));
        session.CreditKill();
        rig.Run(5);
        Assert.False(alice.TryTakeCompletedAchievement(out _));
    }

    [Fact]
    public void Statistics_AreKeptByTheRepository_SoANewServerGivesThePlayerTheirsBack()
    {
        var repository = new InMemoryStatisticsRepository();
        var first = NewRig(repository);
        first.Join("alice");
        first.Run(10);
        for (var i = 0; i < 50; i++)
        {
            first.Session(1).CreditKill();
        }

        var second = NewRig(repository);
        second.Join("alice");
        second.Join("bob");
        second.Run(10);

        Assert.Equal(50, second.Session(1).Statistics.Value("base:statistic/zombies_killed"));
        Assert.Equal(["base:achievement/zombie_slayer"], second.Session(1).Statistics.Completed);
        Assert.Equal(0, second.Session(2).Statistics.Value("base:statistic/zombies_killed"));
    }

    [Fact]
    public void SurvivingADay_IsCountedForALivingPlayerOnly()
    {
        var rig = NewRig();
        rig.Join("alice");
        rig.Join("bob");
        rig.Run(10);
        Kill(rig, rig.Session(2));

        rig.Run(55);

        Assert.True(rig.Session(1).Statistics.Value("base:statistic/days_survived") >= 2);
        Assert.Equal(0, rig.Session(2).Statistics.Value("base:statistic/days_survived"));
    }

    [Fact]
    public void WalkingOnTheServer_CountsWholeStepsOfDistance()
    {
        var rig = NewRig(dayLength: TimeSpan.FromHours(1));
        var alice = rig.Join("alice");
        rig.Run(10);
        var start = rig.Server.World.TryGet(alice.PlayerEntityId, out var before) ? before.Position : default;

        for (var i = 0; i < 300; i++)
        {
            alice.SendInput(new PlayerInput(1f, 0f, 0f, 0f, false, false, false, false, false));
            rig.Run(1);
        }

        Assert.True(rig.Server.World.TryGet(alice.PlayerEntityId, out var after));
        var walked = rig.Session(1).Statistics.Value("base:statistic/distance_walked");
        var covered = Math.Sqrt(Math.Pow(after.Position.X - start.X, 2) + Math.Pow(after.Position.Z - start.Z, 2));
        Assert.True(walked > 0);
        Assert.Equal(0, walked % DistanceWalked.StepMeters);
        Assert.InRange(walked, covered - DistanceWalked.StepMeters, covered);
    }

    [Fact]
    public void LootingACorpse_CountsTheItemsTheLooterTook()
    {
        var rig = NewRig();
        rig.Join("alice");
        var bob = rig.Join("bob");
        rig.Run(10);
        Assert.True(rig.Session(1).Carried.TryAdd(Beans, 6).IsSuccess);
        Assert.True(rig.Server.Teleport(rig.Session(2), new Vector3(8, 80, 8), 0f));
        Kill(rig, rig.Session(1));
        var corpse = Assert.Single(bob.World.Entities.ToArray(), e => e.Kind == EntityKind.Corpse);

        bob.Send(new LootCorpse(corpse.Id));
        rig.Run(5);

        Assert.Equal(6, rig.Session(2).Statistics.Value("base:statistic/items_looted"));
    }
}
