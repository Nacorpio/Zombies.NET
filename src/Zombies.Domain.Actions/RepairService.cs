using Zombies.Domain.Inventory;
using Zombies.Domain.Items;

namespace Zombies.Domain.Actions;

public enum RepairError
{
    UnknownFault,
    NotFaulty,
    UnknownContainer,
    ItemNotHeld,
    RepairItemMissing,
}

/// <summary>An Item had a Fault repaired.</summary>
public sealed record ItemRepaired(ContainerId Container, ItemId Item, string Fault) : IDomainEvent;

/// <summary>
/// Outcome of a repair: either an error with nothing changed, or the events raised and the time the repair takes, which the
/// caller takes from the player.
/// </summary>
public sealed class RepairResult
{
    private RepairResult(RepairError? error, TimeSpan time, IReadOnlyList<IDomainEvent> events)
    {
        Error = error;
        Time = time;
        Events = events;
    }

    public bool IsSuccess => Error is null;

    public RepairError? Error { get; }

    public TimeSpan Time { get; }

    public IReadOnlyList<IDomainEvent> Events { get; }

    public static RepairResult Success(TimeSpan time, IReadOnlyList<IDomainEvent> events) => new(null, time, events);

    public static RepairResult Failure(RepairError error) => new(error, TimeSpan.Zero, []);
}

/// <summary>The Item action that repairs: uses up the Item a Fault's repair needs and takes the Fault off one Item of a Stack.</summary>
public sealed class RepairService(FaultCatalog faults, InventoryService inventory)
{
    /// <summary>
    /// Repairs one Item, held in <paramref name="container"/> with <paramref name="state"/>, of <paramref name="fault"/>. The
    /// Item the repair needs must be in the same Container.
    /// </summary>
    public RepairResult Repair(ContainerId container, ItemId item, ItemState? state, string fault)
    {
        if (!faults.TryGet(fault, out var definition))
        {
            return RepairResult.Failure(RepairError.UnknownFault);
        }

        if (!ItemFaults.Has(state, fault))
        {
            return RepairResult.Failure(RepairError.NotFaulty);
        }

        var taken = inventory.RemoveItems(container, item, 1, state);
        if (!taken.IsSuccess)
        {
            return RepairResult.Failure(taken.Error == InventoryError.UnknownContainer ? RepairError.UnknownContainer : RepairError.ItemNotHeld);
        }

        var used = inventory.RemoveItems(container, definition.Repair.Consumes, 1);
        if (!used.IsSuccess)
        {
            Require(inventory.AddItems(container, item, 1, state));
            return RepairResult.Failure(RepairError.RepairItemMissing);
        }

        var added = inventory.AddItems(container, item, 1, ItemFaults.Without(state, fault));
        Require(added);
        return RepairResult.Success(definition.Repair.Time, [.. used.Events, new ItemRepaired(container, item, fault), .. added.Events]);
    }

    private static void Require(InventoryResult result)
    {
        if (!result.IsSuccess)
        {
            throw new InvalidOperationException($"A repair could not put an Item back: {result.Error}.");
        }
    }
}
