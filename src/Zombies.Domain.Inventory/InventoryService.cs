using Zombies.Domain.Items;

namespace Zombies.Domain.Inventory;

/// <summary>Domain commands for Containers and Stacks. Every command either fails with no state change or succeeds and returns its events.</summary>
public sealed class InventoryService(IItemCatalog catalog, IContainerRepository containers)
{
    public InventoryResult AddContainer(Container container)
    {
        ArgumentNullException.ThrowIfNull(container);
        return containers.TryAdd(container)
            ? InventoryResult.Success()
            : InventoryResult.Failure(InventoryError.DuplicateContainer);
    }

    public InventoryResult AddItems(ContainerId containerId, ItemId item, int count, ItemState? state = null)
    {
        state = Normalized(state);

        if (count < 1)
        {
            return InventoryResult.Failure(InventoryError.InvalidCount);
        }

        if (!containers.TryGet(containerId, out var container))
        {
            return InventoryResult.Failure(InventoryError.UnknownContainer);
        }

        if (container.CheckFits(item, count, state) is { } error)
        {
            return InventoryResult.Failure(error);
        }

        container.Add(item, count, state);
        return InventoryResult.Success(new ItemsAdded(containerId, item, count, state));
    }

    public InventoryResult RemoveItems(ContainerId containerId, ItemId item, int count, ItemState? state = null)
    {
        state = Normalized(state);

        if (count < 1)
        {
            return InventoryResult.Failure(InventoryError.InvalidCount);
        }

        if (!containers.TryGet(containerId, out var container))
        {
            return InventoryResult.Failure(InventoryError.UnknownContainer);
        }

        if (container.CountOf(item, state) < count)
        {
            return InventoryResult.Failure(InventoryError.InsufficientItems);
        }

        container.Remove(item, count, state);
        return InventoryResult.Success(new ItemsRemoved(containerId, item, count, state));
    }

    public InventoryResult MoveItems(ContainerId from, StackId stack, ContainerId to, int count)
    {
        if (from == to)
        {
            return InventoryResult.Failure(InventoryError.SameContainer);
        }

        if (!containers.TryGet(from, out var source) || !containers.TryGet(to, out var destination))
        {
            return InventoryResult.Failure(InventoryError.UnknownContainer);
        }

        if (source.Find(stack) is not { } found)
        {
            return InventoryResult.Failure(InventoryError.UnknownStack);
        }

        if (count < 1)
        {
            return InventoryResult.Failure(InventoryError.InvalidCount);
        }

        if (count > found.Count)
        {
            return InventoryResult.Failure(InventoryError.InsufficientItems);
        }

        if (destination.CheckFits(found.Item, count, found.State) is { } error)
        {
            return InventoryResult.Failure(error);
        }

        source.RemoveFromStack(stack, count);
        destination.Add(found.Item, count, found.State);
        return InventoryResult.Success(new ItemsMoved(from, to, found.Item, count, found.State));
    }

    public InventoryResult SplitStack(ContainerId containerId, StackId stack, int count)
    {
        if (!containers.TryGet(containerId, out var container))
        {
            return InventoryResult.Failure(InventoryError.UnknownContainer);
        }

        if (container.Find(stack) is not { } found)
        {
            return InventoryResult.Failure(InventoryError.UnknownStack);
        }

        if (count < 1 || count >= found.Count)
        {
            return InventoryResult.Failure(InventoryError.InvalidCount);
        }

        var created = container.Split(stack, count);
        return InventoryResult.Success(new StackSplit(containerId, stack, created, count, found.State));
    }

    public InventoryResult MergeStacks(ContainerId containerId, StackId target, StackId source)
    {
        if (target == source)
        {
            return InventoryResult.Failure(InventoryError.SameStack);
        }

        if (!containers.TryGet(containerId, out var container))
        {
            return InventoryResult.Failure(InventoryError.UnknownContainer);
        }

        if (container.Find(target) is not { } to || container.Find(source) is not { } from)
        {
            return InventoryResult.Failure(InventoryError.UnknownStack);
        }

        if (to.Item != from.Item)
        {
            return InventoryResult.Failure(InventoryError.ItemMismatch);
        }

        if (!Equals(to.State, from.State))
        {
            return InventoryResult.Failure(InventoryError.StateMismatch);
        }

        if (!catalog.TryGet(to.Item, out var definition))
        {
            return InventoryResult.Failure(InventoryError.UnknownItem);
        }

        if (to.Count + from.Count > definition.MaxStack)
        {
            return InventoryResult.Failure(InventoryError.StackFull);
        }

        var resulting = container.Merge(target, source);
        return InventoryResult.Success(new StacksMerged(containerId, target, source, resulting));
    }

    /// <summary>
    /// Takes one Item from a Stack and fits it to a single Item elsewhere, as when an Attachment is fitted to a weapon. The
    /// caller decides the host's new state, which must hold exactly one more of the part; this checks only that the Stacks
    /// exist and that the host's Container still has room. Nothing changes on a failure.
    /// </summary>
    public InventoryResult FitInto(ContainerId from, StackId part, ContainerId hostContainer, StackId host, ItemState hostState)
    {
        ArgumentNullException.ThrowIfNull(hostState);
        if (!containers.TryGet(from, out var source) || !containers.TryGet(hostContainer, out var destination))
        {
            return InventoryResult.Failure(InventoryError.UnknownContainer);
        }

        if (source.Find(part) is not { } fitted || destination.Find(host) is not { } holder)
        {
            return InventoryResult.Failure(InventoryError.UnknownStack);
        }

        if (from == hostContainer && part == host)
        {
            return InventoryResult.Failure(InventoryError.SameStack);
        }

        if (holder.Count != 1)
        {
            return InventoryResult.Failure(InventoryError.InvalidCount);
        }

        // Attached Items carry no state of their own, so an Item with state cannot be fitted without losing it.
        if (fitted.State is not null || AttachedCount(hostState, fitted.Item) != AttachedCount(holder.State, fitted.Item) + 1)
        {
            return InventoryResult.Failure(InventoryError.StateMismatch);
        }

        if (destination.CheckRestate(holder, hostState, leaving: from == hostContainer ? fitted.Item : null) is { } error)
        {
            return InventoryResult.Failure(error);
        }

        source.RemoveFromStack(part, 1);
        destination.Restate(host, hostState);
        return InventoryResult.Success(new ItemsRemoved(from, fitted.Item, 1), new StackStateChanged(hostContainer, host, holder.Item, hostState));
    }

    /// <summary>
    /// Takes one Item out of a single Item's state and puts it in a Container, as when an Attachment is removed from a weapon.
    /// The caller decides the host's new state, which must hold exactly one fewer of the part. Nothing changes on a failure.
    /// </summary>
    public InventoryResult TakeOutOf(ContainerId hostContainer, StackId host, ItemState? hostState, ItemId part, ContainerId to)
    {
        hostState = Normalized(hostState);
        if (!containers.TryGet(hostContainer, out var source) || !containers.TryGet(to, out var destination))
        {
            return InventoryResult.Failure(InventoryError.UnknownContainer);
        }

        if (source.Find(host) is not { } holder)
        {
            return InventoryResult.Failure(InventoryError.UnknownStack);
        }

        if (holder.Count != 1)
        {
            return InventoryResult.Failure(InventoryError.InvalidCount);
        }

        if (AttachedCount(holder.State, part) != AttachedCount(hostState, part) + 1)
        {
            return InventoryResult.Failure(InventoryError.StateMismatch);
        }

        var error = to == hostContainer
            ? source.CheckRestate(holder, hostState, arriving: part)
            : source.CheckRestate(holder, hostState) ?? destination.CheckFits(part, 1);
        if (error is not null)
        {
            return InventoryResult.Failure(error.Value);
        }

        source.Restate(host, hostState);
        destination.Add(part, 1);
        return InventoryResult.Success(new StackStateChanged(hostContainer, host, holder.Item, hostState), new ItemsAdded(to, part, 1));
    }

    private static int AttachedCount(ItemState? state, ItemId item) => state?.Attached.GetValueOrDefault(item) ?? 0;

    private static ItemState? Normalized(ItemState? state) => state is { IsEmpty: true } ? null : state;
}
