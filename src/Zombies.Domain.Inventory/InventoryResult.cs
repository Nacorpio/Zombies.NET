using Zombies.Domain.Items;

namespace Zombies.Domain.Inventory;

public enum InventoryError
{
    UnknownContainer,
    UnknownStack,
    UnknownItem,
    InvalidCount,
    InsufficientItems,
    ExceedsMassLimit,
    ExceedsVolumeLimit,
    StackFull,
    ItemMismatch,
    SameStack,
    SameContainer,
    DuplicateContainer,
    NotWearable,
    LayerOccupied,
    NotWorn,
    InvalidWearState,
}

/// <summary>Outcome of a Domain command: either an error with no state change, or the events raised.</summary>
public sealed class InventoryResult
{
    private static readonly IDomainEvent[] NoEvents = [];

    private InventoryResult(InventoryError? error, IReadOnlyList<IDomainEvent> events)
    {
        Error = error;
        Events = events;
    }

    public bool IsSuccess => Error is null;

    public InventoryError? Error { get; }

    public IReadOnlyList<IDomainEvent> Events { get; }

    public static InventoryResult Success(params IDomainEvent[] events) => new(null, events);

    public static InventoryResult Failure(InventoryError error) => new(error, NoEvents);
}
