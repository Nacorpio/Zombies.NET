using Zombies.Domain.Items;

namespace Zombies.Domain.Inventory;

/// <summary>Read-only view of a Stack of identical Items inside a Container.</summary>
public sealed record ItemStack(StackId Id, ItemId Item, int Count);
