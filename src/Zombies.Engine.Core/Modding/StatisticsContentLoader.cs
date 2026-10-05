using Zombies.Domain.Combat;
using Zombies.Domain.Mods;
using Zombies.Domain.Statistics;

namespace Zombies.Engine.Core.Modding;

/// <summary>Builds the Statistics, Achievements and Conducts of the loaded mods, so a new goal is only data.</summary>
public static class StatisticsContentLoader
{
    /// <summary>The domain events a Statistic can count. This is the one place that sees every context's events.</summary>
    public static StatisticEventCatalog Events { get; } = new(
    [
        typeof(ZombieKilled),
        typeof(ItemsLooted),
        typeof(DistanceWalked),
        typeof(DaySurvived),
        typeof(DamageTaken),
        typeof(BodyPartLost),
        typeof(WoundsBandaged),
        typeof(WoundsTreated),
    ]);

    /// <exception cref="StatisticsException">A Statistic, Achievement or Conduct definition is invalid.</exception>
    public static StatisticsCatalog Load(DefinitionRegistry registry) => StatisticsCatalog.From(registry, Events);
}
