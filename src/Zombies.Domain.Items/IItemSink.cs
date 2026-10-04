namespace Zombies.Domain.Items;

/// <summary>
/// Somewhere Items can be put, such as a Container. Contexts that produce Items, like loot, depend on this port
/// instead of on the context that stores them.
/// </summary>
public interface IItemSink
{
    /// <summary>Tries to put <paramref name="count"/> of an Item in. Returns how many were accepted, from 0 to <paramref name="count"/>.</summary>
    int Offer(ItemId item, int count);
}
