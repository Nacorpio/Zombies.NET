using Zombies.Domain.Items;

namespace Zombies.Domain.Crafting;

public sealed record LootDrop(ItemId Item, int Count);

public enum LootError
{
    UnknownTable,
}

/// <summary>
/// Outcome of filling a place with loot. <see cref="Rolled"/> is what the dice produced; <see cref="Placed"/> is what the sink
/// accepted; <see cref="Discarded"/> is the rest, which did not fit.
/// </summary>
public sealed class LootFillResult
{
    private static readonly LootDrop[] None = [];

    internal LootFillResult(LootError? error, IReadOnlyList<LootDrop> rolled, IReadOnlyList<LootDrop> placed, IReadOnlyList<LootDrop> discarded)
    {
        Error = error;
        Rolled = rolled;
        Placed = placed;
        Discarded = discarded;
    }

    public bool IsSuccess => Error is null;

    public LootError? Error { get; }

    public IReadOnlyList<LootDrop> Rolled { get; }

    public IReadOnlyList<LootDrop> Placed { get; }

    public IReadOnlyList<LootDrop> Discarded { get; }

    internal static LootFillResult Failure(LootError error) => new(error, None, None, None);
}

public interface ILootTableCatalog
{
    bool TryGet(string id, out LootTable table);
}

public sealed class LootTableCatalog : ILootTableCatalog
{
    private readonly Dictionary<string, LootTable> _tables = new(StringComparer.Ordinal);

    public LootTableCatalog(IEnumerable<LootTable> tables)
    {
        ArgumentNullException.ThrowIfNull(tables);
        foreach (var table in tables)
        {
            if (!_tables.TryAdd(table.Id, table))
            {
                throw new ArgumentException($"Duplicate loot table '{table.Id}'.", nameof(tables));
            }
        }
    }

    public bool TryGet(string id, out LootTable table) => _tables.TryGetValue(id, out table!);
}

/// <summary>Rolls loot tables deterministically and offers the result to an <see cref="IItemSink"/>.</summary>
public sealed class LootService(ILootTableCatalog tables)
{
    /// <summary>The same table and seed always produce the same drops, in the same order.</summary>
    public static IReadOnlyList<LootDrop> Roll(LootTable table, ulong seed)
    {
        ArgumentNullException.ThrowIfNull(table);
        var random = new DeterministicRandom(seed);
        var rolls = random.NextInt(table.MinRolls, table.MaxRolls);

        var drops = new List<LootDrop>();
        for (var i = 0; i < rolls; i++)
        {
            var pick = (long)random.NextBelow((ulong)table.TotalWeight);
            var entry = table.Entries[^1];
            foreach (var candidate in table.Entries)
            {
                if (pick < candidate.Weight)
                {
                    entry = candidate;
                    break;
                }

                pick -= candidate.Weight;
            }

            var count = random.NextInt(entry.MinCount, entry.MaxCount);
            var existing = drops.FindIndex(d => d.Item == entry.Item);
            if (existing >= 0)
            {
                drops[existing] = drops[existing] with { Count = drops[existing].Count + count };
            }
            else
            {
                drops.Add(new LootDrop(entry.Item, count));
            }
        }

        return drops;
    }

    /// <summary>Rolls a table and offers every drop to the sink, in roll order. Whatever the sink refuses is discarded.</summary>
    public LootFillResult Fill(string tableId, ulong seed, IItemSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        if (!tables.TryGet(tableId, out var table))
        {
            return LootFillResult.Failure(LootError.UnknownTable);
        }

        var rolled = Roll(table, seed);
        var placed = new List<LootDrop>();
        var discarded = new List<LootDrop>();
        foreach (var drop in rolled)
        {
            var accepted = Math.Clamp(sink.Offer(drop.Item, drop.Count), 0, drop.Count);
            if (accepted > 0)
            {
                placed.Add(new LootDrop(drop.Item, accepted));
            }

            if (accepted < drop.Count)
            {
                discarded.Add(new LootDrop(drop.Item, drop.Count - accepted));
            }
        }

        return new LootFillResult(null, rolled, placed, discarded);
    }
}
