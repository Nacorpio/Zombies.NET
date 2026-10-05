using Zombies.Domain.Statistics;

namespace Zombies.Persistence.Sqlite;

/// <summary>Stores the statistics of each player in the save, so they outlast a restart and every life.</summary>
public sealed class SqliteStatisticsRepository(SaveDatabase database) : IStatisticsRepository
{
    public bool TryGet(string player, StatisticsCatalog catalog, out PlayerStatistics statistics)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(catalog);
        statistics = null!;
        var values = new List<StatisticValue>();
        using (var command = database.Command(null, "SELECT statistic, value FROM player_statistics WHERE player = $player ORDER BY statistic", ("$player", player)))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                values.Add(new StatisticValue(reader.GetString(0), reader.GetDouble(1)));
            }
        }

        var completed = new List<string>();
        using (var command = database.Command(null, "SELECT achievement FROM player_achievements WHERE player = $player ORDER BY achievement", ("$player", player)))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                completed.Add(reader.GetString(0));
            }
        }

        if (values.Count == 0 && completed.Count == 0)
        {
            return false;
        }

        try
        {
            statistics = PlayerStatistics.Restore(new PlayerStatisticsSnapshot(values, completed), catalog);
            return true;
        }
        catch (StatisticsException ex)
        {
            throw new SaveCorruptException($"The statistics of {player} are not valid: {ex.Message}", ex);
        }
    }

    public void Save(string player, PlayerStatistics statistics)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(statistics);
        var snapshot = statistics.ToSnapshot();
        database.Transact(transaction =>
        {
            database.Command(transaction, "DELETE FROM player_statistics WHERE player = $player", ("$player", player)).ExecuteNonQuery();
            database.Command(transaction, "DELETE FROM player_achievements WHERE player = $player", ("$player", player)).ExecuteNonQuery();
            foreach (var value in snapshot.Values)
            {
                database.Command(
                    transaction,
                    "INSERT INTO player_statistics (player, statistic, value) VALUES ($player, $statistic, $value)",
                    ("$player", player),
                    ("$statistic", value.Statistic),
                    ("$value", value.Value)).ExecuteNonQuery();
            }

            foreach (var achievement in snapshot.Completed)
            {
                database.Command(
                    transaction,
                    "INSERT INTO player_achievements (player, achievement) VALUES ($player, $achievement)",
                    ("$player", player),
                    ("$achievement", achievement)).ExecuteNonQuery();
            }
        });
    }
}
