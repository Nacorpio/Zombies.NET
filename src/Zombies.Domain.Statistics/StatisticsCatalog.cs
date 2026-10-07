using Zombies.Domain.Mods;

namespace Zombies.Domain.Statistics;

/// <summary>Every Statistic, Achievement and Conduct the loaded mods define.</summary>
public sealed class StatisticsCatalog
{
    private static readonly (StatisticDefinition Statistic, StatisticCount Count)[] NoCounters = [];
    private static readonly Goal[] NoGoals = [];

    private readonly Dictionary<string, StatisticDefinition> _statistics = [];
    private readonly Dictionary<Type, (StatisticDefinition Statistic, StatisticCount Count)[]> _counters;
    private readonly Dictionary<string, Goal[]> _achievementsOn;

    /// <exception cref="StatisticsException">An id is used twice, or an Achievement or Conduct names a Statistic nobody defines.</exception>
    public StatisticsCatalog(IEnumerable<StatisticDefinition> statistics, IEnumerable<Goal> achievements, IEnumerable<Goal> conducts)
    {
        ArgumentNullException.ThrowIfNull(statistics);
        ArgumentNullException.ThrowIfNull(achievements);
        ArgumentNullException.ThrowIfNull(conducts);
        foreach (var statistic in statistics)
        {
            if (!_statistics.TryAdd(statistic.Id, statistic))
            {
                throw new StatisticsException($"The Statistic '{statistic.Id}' is defined twice.");
            }
        }

        _counters = _statistics.Values
            .SelectMany(s => s.Counts.Select(c => (Statistic: s, Count: c)))
            .GroupBy(c => c.Count.EventType)
            .ToDictionary(g => g.Key, g => g.ToArray());
        Achievements = Checked(achievements);
        Conducts = Checked(conducts);
        _achievementsOn = Achievements.GroupBy(a => a.Statistic).ToDictionary(g => g.Key, g => g.ToArray());
    }

    public static StatisticsCatalog Empty { get; } = new([], [], []);

    public IReadOnlyList<StatisticDefinition> Statistics => [.. _statistics.Values.OrderBy(s => s.Id, StringComparer.Ordinal)];

    public IReadOnlyList<Goal> Achievements { get; }

    public IReadOnlyList<Goal> Conducts { get; }

    public bool TryGetStatistic(string id, out StatisticDefinition statistic) => _statistics.TryGetValue(id, out statistic!);

    /// <summary>The Statistics that count events of this type, with how each counts them. Empty when none does.</summary>
    internal (StatisticDefinition Statistic, StatisticCount Count)[] CountersOf(Type eventType) =>
        _counters.TryGetValue(eventType, out var counters) ? counters : NoCounters;

    /// <summary>The Achievements that compare this Statistic. Empty when none does.</summary>
    internal Goal[] AchievementsOn(string statistic) => _achievementsOn.TryGetValue(statistic, out var goals) ? goals : NoGoals;

    /// <exception cref="StatisticsException">A Statistic, Achievement or Conduct definition is invalid.</exception>
    public static StatisticsCatalog From(DefinitionRegistry registry, StatisticEventCatalog events)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(events);
        return new StatisticsCatalog(
            registry.OfKind("statistic").Select(d => StatisticJson.Parse(d.Json, events)),
            registry.OfKind("achievement").Select(d => GoalJson.Parse(d.Json, "achievement")),
            registry.OfKind("conduct").Select(d => GoalJson.Parse(d.Json, "conduct")));
    }

    private List<Goal> Checked(IEnumerable<Goal> goals)
    {
        var all = goals.OrderBy(g => g.Id, StringComparer.Ordinal).ToList();
        foreach (var goal in all)
        {
            if (!_statistics.ContainsKey(goal.Statistic))
            {
                throw new StatisticsException($"'{goal.Id}' compares the Statistic '{goal.Statistic}', which no loaded mod defines.");
            }
        }

        return all;
    }
}
