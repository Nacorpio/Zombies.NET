using Zombies.Domain.Combat;
using Zombies.Domain.Items;
using Zombies.Domain.Statistics;

namespace Zombies.Domain.Tests;

public sealed class StatisticsTests
{
    private static readonly StatisticEventCatalog Events = new([typeof(ZombieKilled), typeof(DaySurvived), typeof(DistanceWalked), typeof(DamageTaken), typeof(WoundsBandaged)]);

    private static StatisticDefinition Statistic(string json) => StatisticJson.Parse(json, Events);

    private static readonly StatisticDefinition Kills = Statistic("""
        { "id": "test:statistic/kills", "counts": [ { "event": "ZombieKilled" } ] }
        """);

    private static readonly StatisticDefinition Days = Statistic("""
        { "id": "test:statistic/days", "scope": "life", "counts": [ { "event": "DaySurvived" } ] }
        """);

    private static readonly StatisticDefinition Distance = Statistic("""
        { "id": "test:statistic/distance", "counts": [ { "event": "DistanceWalked", "sum": "Meters" } ] }
        """);

    private static readonly StatisticDefinition Bites = Statistic("""
        { "id": "test:statistic/bites", "counts": [ { "event": "DamageTaken", "match": { "property": "Type", "value": "bite" } } ] }
        """);

    private static readonly StatisticDefinition LifeKills = Statistic("""
        { "id": "test:statistic/life_kills", "scope": "life", "counts": [ { "event": "ZombieKilled" } ] }
        """);

    private static Goal Achievement(string name, string statistic, string comparison, double target) =>
        GoalJson.Parse($$"""{ "id": "test:achievement/{{name}}", "statistic": "test:statistic/{{statistic}}", "comparison": "{{comparison}}", "target": {{target}} }""", "achievement");

    private static Goal Conduct(string name, string statistic, string comparison, double target) =>
        GoalJson.Parse($$"""{ "id": "test:conduct/{{name}}", "statistic": "test:statistic/{{statistic}}", "comparison": "{{comparison}}", "target": {{target}} }""", "conduct");

    private static readonly StatisticsCatalog Catalog = new(
        [Kills, Days, Distance, Bites, LifeKills],
        [
            Achievement("slayer", "kills", "atLeast", 3),
            Achievement("week", "days", "atLeast", 2),
            Achievement("hiker", "distance", "atLeast", 25),
            Achievement("bitten", "bites", "atLeast", 1),
        ],
        [Conduct("pacifist", "life_kills", "atMost", 0), Conduct("bold", "life_kills", "atLeast", 2)]);

    private static DamageTaken Hit(DamageType type) => new(new BodyId(1), BodyPart.Torso, type, 5);

    [Fact]
    public void AStatistic_CountsEachEventOfTheTypeItNames_AndNoOther()
    {
        var player = new PlayerStatistics(Catalog);

        player.Record(ZombieKilled.Instance);
        player.Record(ZombieKilled.Instance);
        player.Record(DaySurvived.Instance);

        Assert.Equal(2, player.Value("test:statistic/kills"));
        Assert.Equal(1, player.Value("test:statistic/days"));
        Assert.Equal(0, player.Value("test:statistic/distance"));
    }

    [Fact]
    public void AStatistic_CanSumANumberTheEventCarries()
    {
        var player = new PlayerStatistics(Catalog);

        player.Record(DistanceWalked.Step);
        player.Record(new DistanceWalked(5));

        Assert.Equal(15, player.Value("test:statistic/distance"));
    }

    [Fact]
    public void AStatistic_CanCountOnlyEventsWhosePropertyMatches()
    {
        var player = new PlayerStatistics(Catalog);

        player.Record(Hit(DamageType.Blunt));
        player.Record(Hit(DamageType.Bite));
        player.Record(Hit(DamageType.Bite));

        Assert.Equal(2, player.Value("test:statistic/bites"));
    }

    [Fact]
    public void AnEventNoStatisticNames_ChangesNothingAndCompletesNothing()
    {
        var player = new PlayerStatistics(Catalog);

        Assert.Empty(player.Record(new WoundsBandaged(new BodyId(1), BodyPart.Torso, 2)));
        Assert.Empty(player.ToSnapshot().Values);
    }

    [Fact]
    public void AnAchievement_IsCompletedWhenItsTargetIsReached_AndOnlyThen()
    {
        var player = new PlayerStatistics(Catalog);

        Assert.Empty(player.Record(ZombieKilled.Instance));
        Assert.Empty(player.Record(ZombieKilled.Instance));
        Assert.Equal(["test:achievement/slayer"], player.Record(ZombieKilled.Instance));
        Assert.Empty(player.Record(ZombieKilled.Instance));
        Assert.Equal(["test:achievement/slayer"], player.Completed);
    }

    [Fact]
    public void AnEventStream_CompletesEachAchievementOnceInTheOrderItReachesThem()
    {
        var player = new PlayerStatistics(Catalog);
        IDomainEvent[] stream =
        [
            DistanceWalked.Step,
            Hit(DamageType.Cut),
            ZombieKilled.Instance,
            DistanceWalked.Step,
            Hit(DamageType.Bite),
            DistanceWalked.Step,
            ZombieKilled.Instance,
            ZombieKilled.Instance,
            Hit(DamageType.Bite),
        ];

        var completed = stream.SelectMany(player.Record).ToList();

        Assert.Equal(["test:achievement/bitten", "test:achievement/hiker", "test:achievement/slayer"], completed);
    }

    [Fact]
    public void EndingARun_ReportsTheScores_AndTheConductsStillKept()
    {
        var player = new PlayerStatistics(Catalog);
        player.Record(DaySurvived.Instance);
        player.Record(ZombieKilled.Instance);

        var run = player.EndRun();

        Assert.Equal([new StatisticValue("test:statistic/days", 1), new StatisticValue("test:statistic/life_kills", 1)], run.Scores);
        Assert.Empty(run.ConductsKept);
    }

    [Fact]
    public void AConduct_IsKeptWhileItsGoalHolds()
    {
        var player = new PlayerStatistics(Catalog);

        Assert.Equal(["test:conduct/pacifist"], player.EndRun().ConductsKept);

        player.Record(ZombieKilled.Instance);
        player.Record(ZombieKilled.Instance);
        Assert.Equal(["test:conduct/bold"], player.EndRun().ConductsKept);
    }

    [Fact]
    public void EndingARun_StartsLifeStatisticsOverButKeepsLifetimeOnesAndAchievements()
    {
        var player = new PlayerStatistics(Catalog);
        player.Record(DaySurvived.Instance);
        player.Record(DaySurvived.Instance);
        player.Record(ZombieKilled.Instance);

        player.EndRun();

        Assert.Equal(0, player.Value("test:statistic/days"));
        Assert.Equal(0, player.Value("test:statistic/life_kills"));
        Assert.Equal(1, player.Value("test:statistic/kills"));
        Assert.Equal(["test:achievement/week"], player.Completed);
    }

    [Fact]
    public void ACompletedAchievement_IsNotCompletedAgainInALaterLife()
    {
        var player = new PlayerStatistics(Catalog);
        player.Record(DaySurvived.Instance);
        Assert.Equal(["test:achievement/week"], player.Record(DaySurvived.Instance));
        player.EndRun();

        player.Record(DaySurvived.Instance);

        Assert.Empty(player.Record(DaySurvived.Instance));
    }

    [Fact]
    public void ASnapshot_RestoresTheSameStatistics()
    {
        var player = new PlayerStatistics(Catalog);
        player.Record(ZombieKilled.Instance);
        player.Record(DistanceWalked.Step);
        player.Record(Hit(DamageType.Bite));

        var restored = PlayerStatistics.Restore(player.ToSnapshot(), Catalog);

        Assert.Equal(player.ToSnapshot().Values, restored.ToSnapshot().Values);
        Assert.Equal(["test:achievement/bitten"], restored.Completed);
        Assert.Empty(restored.Record(Hit(DamageType.Bite)));
    }

    [Theory]
    [InlineData("test:statistic/nothing", 1.0)]
    [InlineData("test:statistic/kills", -1.0)]
    [InlineData("test:statistic/kills", double.NaN)]
    public void ASnapshot_ThatCouldNotHaveBeenReached_IsRefused(string statistic, double value)
    {
        var snapshot = new PlayerStatisticsSnapshot([new StatisticValue(statistic, value)], []);

        Assert.Throws<StatisticsException>(() => PlayerStatistics.Restore(snapshot, Catalog));
    }

    [Fact]
    public void ASnapshot_NamingAnUnknownAchievement_IsRefused()
    {
        var snapshot = new PlayerStatisticsSnapshot([], ["test:achievement/nothing"]);

        Assert.Throws<StatisticsException>(() => PlayerStatistics.Restore(snapshot, Catalog));
    }

    [Fact]
    public void TheRepository_KeepsEachPlayersStatisticsApart()
    {
        var repository = new InMemoryStatisticsRepository();
        var alice = new PlayerStatistics(Catalog);
        alice.Record(ZombieKilled.Instance);
        repository.Save("alice", alice);

        Assert.True(repository.TryGet("alice", Catalog, out var loaded));
        Assert.Equal(1, loaded.Value("test:statistic/kills"));
        Assert.False(repository.TryGet("bob", Catalog, out _));
    }

    [Theory]
    [InlineData("""{ "id": "test:statistic/x", "counts": [ { "event": "Nothing" } ] }""")]
    [InlineData("""{ "id": "test:statistic/x", "counts": [ { "event": "ZombieKilled", "sum": "Meters" } ] }""")]
    [InlineData("""{ "id": "test:statistic/x", "counts": [ { "event": "DamageTaken", "sum": "Part" } ] }""")]
    [InlineData("""{ "id": "test:statistic/x", "counts": [ { "event": "DamageTaken", "match": { "property": "Type", "value": "fire" } } ] }""")]
    [InlineData("""{ "id": "test:statistic/x", "counts": [ { "event": "DamageTaken", "match": { "property": "Nothing", "value": "1" } } ] }""")]
    [InlineData("""{ "id": "test:statistic/x", "counts": [] }""")]
    [InlineData("""{ "id": "test:item/x", "counts": [ { "event": "ZombieKilled" } ] }""")]
    [InlineData("""{ "id": "test:statistic/x", "scope": "forever", "counts": [ { "event": "ZombieKilled" } ] }""")]
    [InlineData("""{ "counts": [ { "event": "ZombieKilled" } ] }""")]
    public void AStatisticDefinition_ThatCannotCountAnything_IsRefused(string json)
    {
        Assert.Throws<StatisticsException>(() => StatisticJson.Parse(json, Events));
    }

    [Theory]
    [InlineData("""{ "id": "test:achievement/x", "statistic": "test:statistic/kills", "comparison": "atLeast" }""")]
    [InlineData("""{ "id": "test:achievement/x", "statistic": "test:statistic/kills", "comparison": "more", "target": 1 }""")]
    [InlineData("""{ "id": "test:achievement/x", "statistic": "test:item/kills", "comparison": "atLeast", "target": 1 }""")]
    [InlineData("""{ "id": "test:conduct/x", "statistic": "test:statistic/kills", "comparison": "atLeast", "target": 1 }""")]
    public void AnAchievementDefinition_WithoutAStatisticAndComparisonAndTarget_IsRefused(string json)
    {
        Assert.Throws<StatisticsException>(() => GoalJson.Parse(json, "achievement"));
    }

    [Fact]
    public void ACatalog_RefusesAnAchievementOfAStatisticNobodyDefines()
    {
        Assert.Throws<StatisticsException>(() => new StatisticsCatalog([Kills], [Achievement("lost", "nothing", "atLeast", 1)], []));
    }

    [Fact]
    public void ACatalog_RefusesAStatisticDefinedTwice()
    {
        Assert.Throws<StatisticsException>(() => new StatisticsCatalog([Kills, Kills], [], []));
    }

    [Theory]
    [InlineData("atLeast", 5, 4, false)]
    [InlineData("atLeast", 5, 5, true)]
    [InlineData("atMost", 5, 5, true)]
    [InlineData("atMost", 5, 6, false)]
    [InlineData("exactly", 5, 5, true)]
    [InlineData("exactly", 5, 6, false)]
    public void AGoal_ComparesAValueWithItsTarget(string comparison, double target, double value, bool met)
    {
        Assert.Equal(met, Achievement("x", "kills", comparison, target).IsMet(value));
    }
}
