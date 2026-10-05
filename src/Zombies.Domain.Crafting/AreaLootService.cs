using Zombies.Domain.Items;

namespace Zombies.Domain.Crafting;

public enum AreaLootError
{
    UnknownAreaType,
    NoRuleForContainerKind,
    NoTableAvailable,
    UnknownTable,
}

/// <summary>
/// Outcome of filling a container from an Area type. <see cref="Table"/> is the loot table that was chosen,
/// and <see cref="Fill"/> is what it produced.
/// </summary>
public sealed class AreaLootResult
{
    private AreaLootResult(AreaLootError? error, string? table, LootFillResult? fill)
    {
        Error = error;
        Table = table;
        Fill = fill;
    }

    public bool IsSuccess => Error is null;

    public AreaLootError? Error { get; }

    public string? Table { get; }

    public LootFillResult? Fill { get; }

    internal static AreaLootResult Success(string table, LootFillResult fill) => new(null, table, fill);

    internal static AreaLootResult Failure(AreaLootError error) => new(error, null, null);
}

/// <summary>
/// Chooses which loot table a container uses from its Area type, then rolls it. The same Area type, container kind,
/// Danger level, and seed always choose the same table and the same loot.
/// </summary>
public sealed class AreaLootService(AreaTypeCatalog areas, LootService loot)
{
    /// <summary>Chooses the loot table for a container, or reports why there is none.</summary>
    public AreaLootError? TryChooseTable(string areaTypeId, string containerKind, int dangerLevel, ulong seed, out string table)
    {
        table = string.Empty;
        if (!areas.TryGet(areaTypeId, out var areaType))
        {
            return AreaLootError.UnknownAreaType;
        }

        if (!areaType.TryGetRule(containerKind, out var rule))
        {
            return AreaLootError.NoRuleForContainerKind;
        }

        var weights = rule.Tables.Select(t => t.EffectiveWeight(dangerLevel)).ToList();
        var total = weights.Sum();
        if (total <= 0)
        {
            return AreaLootError.NoTableAvailable;
        }

        var pick = (long)new DeterministicRandom(seed).NextBelow((ulong)total);
        for (var i = 0; i < rule.Tables.Count; i++)
        {
            if (pick < weights[i])
            {
                table = rule.Tables[i].Table;
                return null;
            }

            pick -= weights[i];
        }

        table = rule.Tables[^1].Table;
        return null;
    }

    /// <summary>Chooses a table for the container and fills it. A container with no rule gets nothing, reported as an error.</summary>
    public AreaLootResult Fill(string areaTypeId, string containerKind, int dangerLevel, ulong seed, IItemSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        if (TryChooseTable(areaTypeId, containerKind, dangerLevel, seed, out var table) is { } error)
        {
            return AreaLootResult.Failure(error);
        }

        var fill = loot.Fill(table, DeterministicRandom.Combine(seed, 1), sink);
        return fill.IsSuccess
            ? AreaLootResult.Success(table, fill)
            : AreaLootResult.Failure(AreaLootError.UnknownTable);
    }
}
