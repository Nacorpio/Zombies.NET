using Zombies.Domain.Items;

namespace Zombies.Domain.Inventory;

public sealed record ItemsAdded(ContainerId Container, ItemId Item, int Count, ItemState? State = null) : IDomainEvent;

public sealed record ItemsRemoved(ContainerId Container, ItemId Item, int Count, ItemState? State = null) : IDomainEvent;

public sealed record ItemsMoved(ContainerId From, ContainerId To, ItemId Item, int Count, ItemState? State = null) : IDomainEvent;

public sealed record StackSplit(ContainerId Container, StackId Source, StackId Created, int Count, ItemState? State = null) : IDomainEvent;

/// <summary>An Item was fitted to or taken out of another, so the Stack holding that one now carries a different Item state.</summary>
public sealed record StackStateChanged(ContainerId Container, StackId Stack, ItemId Item, ItemState? State) : IDomainEvent;

public sealed record StacksMerged(ContainerId Container, StackId Target, StackId Removed, int ResultingCount) : IDomainEvent;
