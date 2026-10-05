using Zombies.Domain.Mods;

namespace Zombies.Engine.Core.Modding;

/// <summary>
/// The World options the base mod declares, and how the systems that read them turn a multiplier into the integer
/// percentage their deterministic code works in.
/// </summary>
public static class BaseWorldOptions
{
    public const string ZombieDensity = "base:world_option/zombie_density";
    public const string LootRarity = "base:world_option/loot_rarity";

    /// <summary>The zombie density multiplier as a percentage, for <c>SettlementPlanner.Plan</c>.</summary>
    public static int ZombieDensityPercent(WorldOptions options) => Percent(options, ZombieDensity);

    /// <summary>The loot rarity multiplier as a percentage, for <c>LootService</c>.</summary>
    public static int LootRarityPercent(WorldOptions options) => Percent(options, LootRarity);

    private static int Percent(WorldOptions options, string id)
    {
        ArgumentNullException.ThrowIfNull(options);
        return (int)Math.Round(options.Get(id) * 100, MidpointRounding.AwayFromZero);
    }
}
