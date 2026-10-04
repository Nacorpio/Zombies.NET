using Zombies.Domain.Items;

namespace Zombies.Domain.Inventory;

/// <summary>
/// Lets other contexts put Items into a Container through the Inventory commands, so mass and volume limits always apply.
/// When not everything fits, it accepts as many as it can and collects the events it raised.
/// </summary>
public sealed class ContainerItemSink(InventoryService inventory, ContainerId container) : IItemSink
{
    private readonly List<IDomainEvent> _events = [];

    /// <summary>Events raised by the Items accepted so far.</summary>
    public IReadOnlyList<IDomainEvent> Events => _events;

    public int Offer(ItemId item, int count)
    {
        for (var attempt = count; attempt >= 1; attempt--)
        {
            var result = inventory.AddItems(container, item, attempt);
            if (result.IsSuccess)
            {
                _events.AddRange(result.Events);
                return attempt;
            }

            if (result.Error is InventoryError.UnknownItem or InventoryError.UnknownContainer)
            {
                return 0;
            }
        }

        return 0;
    }
}
