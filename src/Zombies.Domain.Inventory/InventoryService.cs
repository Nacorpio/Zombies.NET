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

    public InventoryResult AddItems(ContainerId containerId, ItemId item, int count)
    {
        if (count < 1)
        {
            return InventoryResult.Failure(InventoryError.InvalidCount);
        }

        if (!containers.TryGet(containerId, out var container))
        {
            return InventoryResult.Failure(InventoryError.UnknownContainer);
        }

        if (container.CheckFits(item, count) is { } error)
        {
            return InventoryResult.Failure(error);
        }

        container.Add(item, count);
        return InventoryResult.Success(new ItemsAdded(containerId, item, count));
    }

    public InventoryResult RemoveItems(ContainerId containerId, ItemId item, int count)
    {
        if (count < 1)
        {
            return InventoryResult.Failure(InventoryError.InvalidCount);
        }

        if (!containers.TryGet(containerId, out var container))
        {
            return InventoryResult.Failure(InventoryError.UnknownContainer);
        }

        if (container.CountOf(item) < count)
        {
            return InventoryResult.Failure(InventoryError.InsufficientItems);
        }

        container.Remove(item, count);
        return InventoryResult.Success(new ItemsRemoved(containerId, item, count));
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

        if (destination.CheckFits(found.Item, count) is { } error)
        {
            return InventoryResult.Failure(error);
        }

        source.RemoveFromStack(stack, count);
        destination.Add(found.Item, count);
        return InventoryResult.Success(new ItemsMoved(from, to, found.Item, count));
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
        return InventoryResult.Success(new StackSplit(containerId, stack, created, count));
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
}
