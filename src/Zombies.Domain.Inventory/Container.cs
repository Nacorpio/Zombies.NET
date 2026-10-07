using UnitsNet;
using Zombies.Domain.Items;

namespace Zombies.Domain.Inventory;

/// <summary>Holds Stacks within a mass limit and a volume limit.</summary>
public sealed class Container
{
    private const double MassSlackKg = 1e-9;
    private const double VolumeSlackM3 = 1e-12;

    private sealed class Entry(StackId id, ItemId item, int count, ItemState? state)
    {
        public StackId Id { get; } = id;

        public ItemId Item { get; } = item;

        public ItemState? State { get; } = state;

        public int Count { get; set; } = count;
    }

    private static readonly IReadOnlyDictionary<ItemId, int> EmptyAttached = new Dictionary<ItemId, int>();

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

    public IReadOnlyList<ItemStack> Stacks => [.. _entries.Select(e => new ItemStack(e.Id, e.Item, e.Count, e.State))];

    public Mass TotalMass
    {
        get
        {
            var total = Mass.Zero;
            foreach (var entry in _entries)
            {
                total += UnitMass(entry.Item, entry.State) * entry.Count;
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
                total += UnitVolume(entry.Item, entry.State) * entry.Count;
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
        [.. _entries.Select(e => new StackSnapshot(e.Id.Value, e.Item.Value, e.Count, SnapshotOf(e.State)))]);

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

            container._entries.Add(new Entry(new StackId(stack.Id), item, stack.Count, StateOf(stack.State, item)));
        }

        container._nextStackId = Math.Max(snapshot.NextStackId, seen.Count == 0 ? 1 : seen.Max() + 1);
        return container;
    }

    /// <summary>Adds items the Container has room for, as when a worn Item leaves an Outfit. Fails with no change when they do not fit.</summary>
    public InventoryResult TryAdd(ItemId item, int count, ItemState? state = null)
    {
        state = state is { IsEmpty: true } ? null : state;
        if (count < 1)
        {
            return InventoryResult.Failure(InventoryError.InvalidCount);
        }

        if (CheckFits(item, count, state) is { } error)
        {
            return InventoryResult.Failure(error);
        }

        Add(item, count, state);
        return InventoryResult.Success(new ItemsAdded(Id, item, count, state));
    }

    /// <summary>
    /// Moves as much of every Stack into <paramref name="destination"/> as it has room for, with Item state intact, and leaves
    /// the rest here. Used to empty a character into a corpse and to loot one.
    /// </summary>
    public InventoryResult MoveFittingTo(Container destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (destination.Id == Id)
        {
            return InventoryResult.Failure(InventoryError.SameContainer);
        }

        var events = new List<IDomainEvent>();
        foreach (var entry in _entries.ToList())
        {
            var count = entry.Count;
            while (count > 0 && destination.CheckFits(entry.Item, count, entry.State) is not null)
            {
                count--;
            }

            if (count == 0)
            {
                continue;
            }

            RemoveFromStack(entry.Id, count);
            destination.Add(entry.Item, count, entry.State);
            events.Add(new ItemsMoved(Id, destination.Id, entry.Item, count, entry.State));
        }

        return InventoryResult.Success([.. events]);
    }

    public int CountOf(ItemId item) => _entries.Where(e => e.Item == item).Sum(e => e.Count);

    private static ItemStateSnapshot? SnapshotOf(ItemState? state) => state is null
        ? null
        : new ItemStateSnapshot([.. state.Values], [.. state.Attached.Select(a => new KeyValuePair<string, int>(a.Key.Value, a.Value))]);

    private static ItemState? StateOf(ItemStateSnapshot? snapshot, ItemId item)
    {
        if (snapshot is null)
        {
            return null;
        }

        var attached = new List<KeyValuePair<ItemId, int>>();
        foreach (var (attachedItem, count) in snapshot.Attached)
        {
            if (!ItemId.TryParse(attachedItem, out var id))
            {
                throw new ArgumentException($"'{attachedItem}' attached to {item} is not a valid Content ID.");
            }

            attached.Add(new(id, count));
        }

        try
        {
            // Like the Inventory commands, treat a state that holds nothing as no state, so equal Stacks can still merge.
            var state = ItemState.Create(snapshot.Values, attached);
            return state.IsEmpty ? null : state;
        }
        catch (ArgumentException ex)
        {
            throw new ArgumentException($"The state of {item} is not valid: {ex.Message}", ex);
        }
    }

    public int CountOf(ItemId item, ItemState? state) => _entries.Where(e => e.Item == item && Equals(e.State, state)).Sum(e => e.Count);

    private Mass UnitMass(ItemId item, ItemState? state)
    {
        var total = _catalog.TryGet(item, out var definition) ? definition.UnitMass : Mass.Zero;
        foreach (var (attached, count) in state?.Attached ?? EmptyAttached)
        {
            total += UnitMass(attached, null) * count;
        }

        return total;
    }

    private Volume UnitVolume(ItemId item, ItemState? state)
    {
        var total = _catalog.TryGet(item, out var definition) ? definition.UnitVolume : Volume.Zero;
        foreach (var (attached, count) in state?.Attached ?? EmptyAttached)
        {
            total += UnitVolume(attached, null) * count;
        }

        return total;
    }

    internal ItemStack? Find(StackId id)
    {
        var entry = _entries.Find(e => e.Id == id);
        return entry is null ? null : new ItemStack(entry.Id, entry.Item, entry.Count, entry.State);
    }

    internal InventoryError? CheckFits(ItemId item, int count, ItemState? state = null)
    {
        if (!_catalog.TryGet(item, out _) || (state is not null && state.Attached.Keys.Any(a => !_catalog.TryGet(a, out _))))
        {
            return InventoryError.UnknownItem;
        }

        if (TotalMass.Kilograms + (UnitMass(item, state) * count).Kilograms > MassLimit.Kilograms + MassSlackKg)
        {
            return InventoryError.ExceedsMassLimit;
        }

        if (TotalVolume.CubicMeters + (UnitVolume(item, state) * count).CubicMeters > VolumeLimit.CubicMeters + VolumeSlackM3)
        {
            return InventoryError.ExceedsVolumeLimit;
        }

        return null;
    }

    /// <summary>
    /// Whether a single-item Stack can take a new state and still fit, as when an Item is fitted to it or taken out of it.
    /// <paramref name="leaving"/> and <paramref name="arriving"/> name one Item that leaves or joins this Container at the same time.
    /// </summary>
    internal InventoryError? CheckRestate(ItemStack host, ItemState? state, ItemId? leaving = null, ItemId? arriving = null)
    {
        if (state is not null && state.Attached.Keys.Any(a => !_catalog.TryGet(a, out _)))
        {
            return InventoryError.UnknownItem;
        }

        var mass = TotalMass - UnitMass(host.Item, host.State) + UnitMass(host.Item, state);
        var volume = TotalVolume - UnitVolume(host.Item, host.State) + UnitVolume(host.Item, state);
        if (leaving is { } left)
        {
            mass -= UnitMass(left, null);
            volume -= UnitVolume(left, null);
        }

        if (arriving is { } joined)
        {
            mass += UnitMass(joined, null);
            volume += UnitVolume(joined, null);
        }

        if (mass.Kilograms > MassLimit.Kilograms + MassSlackKg)
        {
            return InventoryError.ExceedsMassLimit;
        }

        return volume.CubicMeters > VolumeLimit.CubicMeters + VolumeSlackM3 ? InventoryError.ExceedsVolumeLimit : null;
    }

    /// <summary>Gives a Stack a new Item state and keeps its id and place. The caller has already checked limits.</summary>
    internal void Restate(StackId id, ItemState? state)
    {
        var index = _entries.FindIndex(e => e.Id == id);
        var entry = _entries[index];
        _entries[index] = new Entry(entry.Id, entry.Item, entry.Count, state);
    }

    /// <summary>Adds items, topping up partial Stacks first. The caller has already checked limits.</summary>
    internal void Add(ItemId item, int count, ItemState? state = null)
    {
        var max = _catalog.TryGet(item, out var definition) ? definition.MaxStack : 1;
        var remaining = count;

        foreach (var entry in _entries.Where(e => e.Item == item && Equals(e.State, state) && e.Count < max))
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
            _entries.Add(new Entry(new StackId(_nextStackId++), item, take, state));
            remaining -= take;
        }
    }

    /// <summary>Removes items from the newest Stacks first. The caller has already checked availability.</summary>
    internal void Remove(ItemId item, int count, ItemState? state = null)
    {
        var remaining = count;
        for (var i = _entries.Count - 1; i >= 0 && remaining > 0; i--)
        {
            var entry = _entries[i];
            if (entry.Item != item || !Equals(entry.State, state))
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
        var created = new Entry(new StackId(_nextStackId++), entry.Item, count, entry.State);
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
