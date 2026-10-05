using Zombies.Domain.Items;

namespace Zombies.Domain.Inventory;

public sealed record ItemsAdded(ContainerId Container, ItemId Item, int Count, ItemState? State = null) : IDomainEvent;

public sealed record ItemsRemoved(ContainerId Container, ItemId Item, int Count, ItemState? State = null) : IDomainEvent;

public sealed record ItemsMoved(ContainerId From, ContainerId To, ItemId Item, int Count, ItemState? State = null) : IDomainEvent;

public sealed record StackSplit(ContainerId Container, StackId Source, StackId Created, int Count, ItemState? State = null) : IDomainEvent;

public sealed record StacksMerged(ContainerId Container, StackId Target, StackId Removed, int ResultingCount) : IDomainEvent;
