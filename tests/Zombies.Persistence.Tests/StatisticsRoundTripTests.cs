using Zombies.Domain.Statistics;
using Zombies.Persistence.Sqlite;

namespace Zombies.Persistence.Tests;

public sealed class StatisticsRoundTripTests
{
    private static readonly StatisticEventCatalog Events = new([typeof(ZombieKilled), typeof(DaySurvived)]);

    private static readonly StatisticsCatalog Catalog = new(
        [
            StatisticJson.Parse("""{ "id": "test:statistic/kills", "counts": [ { "event": "ZombieKilled" } ] }""", Events),
            StatisticJson.Parse("""{ "id": "test:statistic/days", "scope": "life", "counts": [ { "event": "DaySurvived" } ] }""", Events),
        ],
        [GoalJson.Parse("""{ "id": "test:achievement/slayer", "statistic": "test:statistic/kills", "comparison": "atLeast", "target": 2 }""", "achievement")],
        []);

    private static PlayerStatistics Slayer()
    {
        var statistics = new PlayerStatistics(Catalog);
        statistics.Record(ZombieKilled.Instance);
        statistics.Record(ZombieKilled.Instance);
        statistics.Record(DaySurvived.Instance);
        return statistics;
    }

    [Fact]
    public void Statistics_SurviveASaveAndLoad_PerPlayer()
    {
        using var save = new TempSave();
        using (var database = save.Open())
        {
            var repository = new SqliteStatisticsRepository(database);
            repository.Save("alice", Slayer());
            repository.Save("bob", new PlayerStatistics(Catalog));
        }

        using var reopened = save.Open();
        var stored = new SqliteStatisticsRepository(reopened);

        Assert.True(stored.TryGet("alice", Catalog, out var alice));
        Assert.Equal(2, alice.Value("test:statistic/kills"));
        Assert.Equal(1, alice.Value("test:statistic/days"));
        Assert.Equal(["test:achievement/slayer"], alice.Completed);
        Assert.False(stored.TryGet("bob", Catalog, out _));
        Assert.False(stored.TryGet("carol", Catalog, out _));
    }

    [Fact]
    public void Statistics_KeptAcrossARespawn_AreWhatTheNextSaveStores()
    {
        using var save = new TempSave();
        using var database = save.Open();
        var repository = new SqliteStatisticsRepository(database);
        var statistics = Slayer();
        statistics.EndRun();

        repository.Save("alice", statistics);

        Assert.True(repository.TryGet("alice", Catalog, out var loaded));
        Assert.Equal(2, loaded.Value("test:statistic/kills"));
        Assert.Equal(0, loaded.Value("test:statistic/days"));
    }

    [Fact]
    public void Saving_ReplacesWhatWasStoredForThePlayer()
    {
        using var save = new TempSave();
        using var database = save.Open();
        var repository = new SqliteStatisticsRepository(database);
        repository.Save("alice", Slayer());
        var later = Slayer();
        later.Record(ZombieKilled.Instance);

        repository.Save("alice", later);

        Assert.True(repository.TryGet("alice", Catalog, out var loaded));
        Assert.Equal(3, loaded.Value("test:statistic/kills"));
    }

    [Fact]
    public void Statistics_NamingAStatisticNoModDefines_ThrowASaveCorruptException()
    {
        using var save = new TempSave();
        using var database = save.Open();
        var repository = new SqliteStatisticsRepository(database);
        repository.Save("alice", Slayer());
        database.Transact(t => database.Command(t, "UPDATE player_statistics SET statistic = 'test:statistic/gone' WHERE statistic = 'test:statistic/kills'").ExecuteNonQuery());

        Assert.Throws<SaveCorruptException>(() => repository.TryGet("alice", Catalog, out _));
    }

    [Fact]
    public void Save_FromBeforeStatistics_IsMigratedAndThenStoresStatistics()
    {
        using var save = new TempSave();
        using (var old = SaveDatabase.Open(save.Path, SaveSchema.Migrations[..3]))
        {
            Assert.Equal(3, old.SchemaVersion);
        }

        using var migrated = save.Open();
        var repository = new SqliteStatisticsRepository(migrated);

        Assert.Equal(SaveSchema.CurrentVersion, migrated.SchemaVersion);
        Assert.False(repository.TryGet("alice", Catalog, out _));
        repository.Save("alice", Slayer());
        Assert.True(repository.TryGet("alice", Catalog, out _));
    }
}
