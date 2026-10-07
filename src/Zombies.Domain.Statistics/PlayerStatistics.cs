using Zombies.Domain.Items;

namespace Zombies.Domain.Statistics;

/// <summary>The value of one Statistic.</summary>
public sealed record StatisticValue(string Statistic, double Value);

/// <summary>What the end of a run left: the final value of each Statistic that counts one life, and the Conducts the player kept.</summary>
public sealed record RunSummary(IReadOnlyList<StatisticValue> Scores, IReadOnlyList<string> ConductsKept);

/// <summary>The plain values of a player's statistics, as stored in a save.</summary>
public sealed record PlayerStatisticsSnapshot(IReadOnlyList<StatisticValue> Values, IReadOnlyList<string> Completed);

/// <summary>
/// A player's statistics: the value of each Statistic the events they cause have added to, and the Achievements they have
/// completed. Lifetime values and completed Achievements outlast any life; <see cref="EndRun"/> closes the life and starts
/// the next from zero.
/// </summary>
public sealed class PlayerStatistics
{
    private static readonly string[] NoAchievements = [];

    private readonly StatisticsCatalog _catalog;
    private readonly Dictionary<string, double> _values = new(StringComparer.Ordinal);
    private readonly HashSet<string> _completed = new(StringComparer.Ordinal);

    public PlayerStatistics(StatisticsCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _catalog = catalog;
    }

    /// <summary>The Achievements this player has completed, in the order of their ids.</summary>
    public IReadOnlyList<string> Completed => [.. _completed.Order(StringComparer.Ordinal)];

    /// <summary>The value of a Statistic, which is 0 until an event has counted toward it.</summary>
    public double Value(string statistic) => _values.GetValueOrDefault(statistic);

    /// <summary>
    /// Counts a domain event toward every Statistic that names its type. Returns the ids of the Achievements this completed,
    /// which is empty for almost every event. An Achievement is completed once, however far its Statistic goes after.
    /// </summary>
    public IReadOnlyList<string> Record(IDomainEvent domainEvent)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        List<string>? completed = null;
        foreach (var (statistic, count) in _catalog.CountersOf(domainEvent.GetType()))
        {
            if (!count.TryAmount(domainEvent, out var amount))
            {
                continue;
            }

            var value = _values.GetValueOrDefault(statistic.Id) + amount;
            _values[statistic.Id] = value;
            foreach (var achievement in _catalog.AchievementsOn(statistic.Id))
            {
                if (achievement.IsMet(value) && _completed.Add(achievement.Id))
                {
                    (completed ??= []).Add(achievement.Id);
                }
            }
        }

        return completed ?? (IReadOnlyList<string>)NoAchievements;
    }

    /// <summary>
    /// Closes the player's current life: reports the final value of each Statistic that counts one life and the Conducts that are
    /// still kept, then starts those Statistics again from zero for the next life. Lifetime values stay.
    /// </summary>
    public RunSummary EndRun()
    {
        var scores = _catalog.Statistics
            .Where(s => s.Scope == StatisticScope.Life)
            .Select(s => new StatisticValue(s.Id, Value(s.Id)))
            .ToList();
        var kept = _catalog.Conducts.Where(c => c.IsMet(Value(c.Statistic))).Select(c => c.Id).ToList();
        foreach (var score in scores)
        {
            _values.Remove(score.Statistic);
        }

        return new RunSummary(scores, kept);
    }

    public PlayerStatisticsSnapshot ToSnapshot() => new(
        [.. _values.OrderBy(v => v.Key, StringComparer.Ordinal).Select(v => new StatisticValue(v.Key, v.Value))],
        Completed);

    /// <exception cref="StatisticsException">The snapshot names a Statistic or Achievement no loaded mod defines, repeats one, or holds a value that cannot be.</exception>
    public static PlayerStatistics Restore(PlayerStatisticsSnapshot snapshot, StatisticsCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(catalog);
        var statistics = new PlayerStatistics(catalog);
        foreach (var (statistic, value) in snapshot.Values)
        {
            if (!catalog.TryGetStatistic(statistic, out _))
            {
                throw new StatisticsException($"'{statistic}' is not a Statistic any loaded mod defines.");
            }

            if (!double.IsFinite(value) || value < 0)
            {
                throw new StatisticsException($"The Statistic '{statistic}' holds {value}, but a Statistic is a finite number from 0.");
            }

            if (!statistics._values.TryAdd(statistic, value))
            {
                throw new StatisticsException($"The Statistic '{statistic}' is stored twice.");
            }
        }

        foreach (var achievement in snapshot.Completed)
        {
            if (!catalog.Achievements.Any(a => a.Id == achievement))
            {
                throw new StatisticsException($"'{achievement}' is not an Achievement any loaded mod defines.");
            }

            if (!statistics._completed.Add(achievement))
            {
                throw new StatisticsException($"The Achievement '{achievement}' is stored twice.");
            }
        }

        return statistics;
    }
}

/// <summary>Stores the statistics of each player, keyed by the player's name.</summary>
public interface IStatisticsRepository
{
    /// <summary>Loads what is stored for the player. False when nothing is.</summary>
    bool TryGet(string player, StatisticsCatalog catalog, out PlayerStatistics statistics);

    /// <summary>Stores the statistics, replacing what was stored for this player.</summary>
    void Save(string player, PlayerStatistics statistics);
}

public sealed class InMemoryStatisticsRepository : IStatisticsRepository
{
    private readonly Dictionary<string, PlayerStatisticsSnapshot> _stored = new(StringComparer.Ordinal);

    public bool TryGet(string player, StatisticsCatalog catalog, out PlayerStatistics statistics)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (_stored.TryGetValue(player, out var snapshot))
        {
            statistics = PlayerStatistics.Restore(snapshot, catalog);
            return true;
        }

        statistics = null!;
        return false;
    }

    public void Save(string player, PlayerStatistics statistics)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(statistics);
        _stored[player] = statistics.ToSnapshot();
    }
}
