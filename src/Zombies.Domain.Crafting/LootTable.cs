using Zombies.Domain.Items;

namespace Zombies.Domain.Crafting;

public sealed class LootTableException(string message) : Exception(message);

/// <summary>One thing a loot table can drop: how likely it is relative to the others, and how many appear.</summary>
public sealed record LootEntry
{
    public LootEntry(ItemId item, int weight, int minCount, int maxCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(weight, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(minCount, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCount, minCount);
        Item = item;
        Weight = weight;
        MinCount = minCount;
        MaxCount = maxCount;
    }

    public ItemId Item { get; }

    public int Weight { get; }

    public int MinCount { get; }

    public int MaxCount { get; }
}

/// <summary>A weighted set of entries, rolled a random number of times.</summary>
public sealed class LootTable
{
    public LootTable(string id, int minRolls, int maxRolls, IEnumerable<LootEntry> entries)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentOutOfRangeException.ThrowIfNegative(minRolls);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxRolls, minRolls);

        var list = entries.ToList();
        if (list.Count == 0)
        {
            throw new ArgumentException("A loot table needs at least one entry.", nameof(entries));
        }

        Id = id;
        MinRolls = minRolls;
        MaxRolls = maxRolls;
        Entries = list;
        TotalWeight = list.Sum(e => (long)e.Weight);
    }

    public string Id { get; }

    public int MinRolls { get; }

    public int MaxRolls { get; }

    public IReadOnlyList<LootEntry> Entries { get; }

    public long TotalWeight { get; }

    /// <summary>Items the table can drop that the catalog does not define. A healthy table returns none.</summary>
    public IReadOnlyList<ItemId> UnknownItems(IItemCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return [.. Entries.Select(e => e.Item).Distinct().Where(i => !catalog.TryGet(i, out _))];
    }
}
