using UnitsNet;
using Zombies.Domain.Items;

namespace Zombies.Domain.Inventory;

/// <summary>Holds Stacks within a mass limit and a volume limit.</summary>
public sealed class Container
{
    private const double MassSlackKg = 1e-9;
    private const double VolumeSlackM3 = 1e-12;

    private sealed class Entry(StackId id, ItemId item, int count)
    {
        public StackId Id { get; } = id;

        public ItemId Item { get; } = item;

        public int Count { get; set; } = count;
    }

    private readonly IItemCatalog _catalog;
    private readonly List<Entry> _entries = [];
    private int _nextStackId = 1;

    public Container(ContainerId id, Mass massLimit, Volume volumeLimit, IItemCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        Id = id;
        MassLimit = massLimit;
        VolumeLimit = volumeLimit;
        _catalog = catalog;
    }

    public ContainerId Id { get; }

    public Mass MassLimit { get; }

    public Volume VolumeLimit { get; }

    public IReadOnlyList<ItemStack> Stacks => [.. _entries.Select(e => new ItemStack(e.Id, e.Item, e.Count))];

    public Mass TotalMass
    {
        get
        {
            var total = Mass.Zero;
            foreach (var entry in _entries)
            {
                if (_catalog.TryGet(entry.Item, out var definition))
                {
                    total += definition.UnitMass * entry.Count;
                }
            }

            return total;
        }
    }

    public Volume TotalVolume
    {
        get
        {
            var total = Volume.Zero;
            foreach (var entry in _entries)
            {
                if (_catalog.TryGet(entry.Item, out var definition))
                {
                    total += definition.UnitVolume * entry.Count;
                }
            }

            return total;
        }
    }

    /// <summary>Captures the Container as plain values, so a save can store it without reaching into its state.</summary>
    public ContainerSnapshot ToSnapshot() => new(
        Id.Value,
        MassLimit.Kilograms,
        VolumeLimit.CubicMeters,
        _nextStackId,
        [.. _entries.Select(e => new StackSnapshot(e.Id.Value, e.Item.Value, e.Count))]);

    /// <summary>Rebuilds a Container from a snapshot. Throws <see cref="ArgumentException"/> when the snapshot is not a state a Container can be in.</summary>
    public static Container Restore(ContainerSnapshot snapshot, IItemCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(catalog);
        if (!double.IsFinite(snapshot.MassLimitKg) || !double.IsFinite(snapshot.VolumeLimitM3) || snapshot.MassLimitKg < 0 || snapshot.VolumeLimitM3 < 0)
        {
            throw new ArgumentException("A Container's limits must be finite and not negative.", nameof(snapshot));
        }

        var container = new Container(new ContainerId(snapshot.Id), Mass.FromKilograms(snapshot.MassLimitKg), Volume.FromCubicMeters(snapshot.VolumeLimitM3), catalog);
        var seen = new HashSet<int>();
        foreach (var stack in snapshot.Stacks)
        {
            if (!ItemId.TryParse(stack.Item, out var item))
            {
                throw new ArgumentException($"'{stack.Item}' is not a valid Content ID.", nameof(snapshot));
            }

            if (stack.Count < 1 || stack.Id < 1 || !seen.Add(stack.Id))
            {
                throw new ArgumentException($"Stack {stack.Id} of {item} is not valid: it needs a unique positive id and a positive count.", nameof(snapshot));
            }

            container._entries.Add(new Entry(new StackId(stack.Id), item, stack.Count));
        }

        container._nextStackId = Math.Max(snapshot.NextStackId, seen.Count == 0 ? 1 : seen.Max() + 1);
        return container;
    }

    public int CountOf(ItemId item) => _entries.Where(e => e.Item == item).Sum(e => e.Count);

    internal ItemStack? Find(StackId id)
    {
        var entry = _entries.Find(e => e.Id == id);
        return entry is null ? null : new ItemStack(entry.Id, entry.Item, entry.Count);
    }

    internal InventoryError? CheckFits(ItemId item, int count)
    {
        if (!_catalog.TryGet(item, out var definition))
        {
            return InventoryError.UnknownItem;
        }

        if (TotalMass.Kilograms + (definition.UnitMass * count).Kilograms > MassLimit.Kilograms + MassSlackKg)
        {
            return InventoryError.ExceedsMassLimit;
        }

        if (TotalVolume.CubicMeters + (definition.UnitVolume * count).CubicMeters > VolumeLimit.CubicMeters + VolumeSlackM3)
        {
            return InventoryError.ExceedsVolumeLimit;
        }

        return null;
    }

    /// <summary>Adds items, topping up partial Stacks first. The caller has already checked limits.</summary>
    internal void Add(ItemId item, int count)
    {
        var max = _catalog.TryGet(item, out var definition) ? definition.MaxStack : 1;
        var remaining = count;

        foreach (var entry in _entries.Where(e => e.Item == item && e.Count < max))
        {
            var take = Math.Min(max - entry.Count, remaining);
            entry.Count += take;
            remaining -= take;
            if (remaining == 0)
            {
                return;
            }
        }

        while (remaining > 0)
        {
            var take = Math.Min(max, remaining);
            _entries.Add(new Entry(new StackId(_nextStackId++), item, take));
            remaining -= take;
        }
    }

    /// <summary>Removes items from the newest Stacks first. The caller has already checked availability.</summary>
    internal void Remove(ItemId item, int count)
    {
        var remaining = count;
        for (var i = _entries.Count - 1; i >= 0 && remaining > 0; i--)
        {
            var entry = _entries[i];
            if (entry.Item != item)
            {
                continue;
            }

            var take = Math.Min(entry.Count, remaining);
            entry.Count -= take;
            remaining -= take;
            if (entry.Count == 0)
            {
                _entries.RemoveAt(i);
            }
        }
    }

    internal void RemoveFromStack(StackId id, int count)
    {
        var index = _entries.FindIndex(e => e.Id == id);
        _entries[index].Count -= count;
        if (_entries[index].Count == 0)
        {
            _entries.RemoveAt(index);
        }
    }

    internal StackId Split(StackId id, int count)
    {
        var entry = _entries.Find(e => e.Id == id)!;
        entry.Count -= count;
        var created = new Entry(new StackId(_nextStackId++), entry.Item, count);
        _entries.Add(created);
        return created.Id;
    }

    internal int Merge(StackId target, StackId source)
    {
        var to = _entries.Find(e => e.Id == target)!;
        var from = _entries.Find(e => e.Id == source)!;
        to.Count += from.Count;
        _entries.Remove(from);
        return to.Count;
    }
}
