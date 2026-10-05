using UnitsNet;
using Zombies.Domain.Crafting;
using Zombies.Domain.Inventory;
using Zombies.Domain.Items;
using Zombies.Domain.World;

namespace Zombies.Engine.Core;

/// <summary>
/// Outcome of opening a Container in the world. <see cref="Generated"/> is true when this open is the one that rolled its loot.
/// </summary>
public sealed record ContainerOpenResult(Container? Container, AreaLootError? Error, string? Table, bool Generated)
{
    public bool IsSuccess => Error is null && Container is not null;
}

/// <summary>
/// Turns a world Container into an Inventory <see cref="Container"/> the first time a player opens it. Its loot comes from the
/// Area type of the room it stands in, chosen with the Container's seed, so the same world always puts the same loot in the same
/// Container. Later opens return the Container as it was left, taken from or not.
/// </summary>
public sealed class WorldContainerOpener(AreaLootService loot, IItemCatalog items, IContainerRepository containers)
{
    /// <summary>The most mass a world Container holds.</summary>
    public static readonly Mass MassLimit = Mass.FromKilograms(50);

    /// <summary>The most volume a world Container holds.</summary>
    public static readonly Volume VolumeLimit = Volume.FromLiters(60);

    /// <summary>A stable, positive id for the Container at a world position, so a save can find it again.</summary>
    public static ContainerId IdOf(WorldContainer container)
    {
        ArgumentNullException.ThrowIfNull(container);
        var hash = WorldHash.Mix(0x436F6E7461696E65UL, container.X, container.Y, container.Z);
        return new ContainerId((long)(hash & 0x7FFFFFFFFFFFFFFFUL));
    }

    public ContainerOpenResult Open(WorldContainer container)
    {
        ArgumentNullException.ThrowIfNull(container);
        var id = IdOf(container);
        if (containers.TryGet(id, out var existing))
        {
            return new ContainerOpenResult(existing, null, null, false);
        }

        // Fill a private copy first, so a repository that hands out fresh copies on every read still ends up with the loot.
        var scratch = new InventoryService(items, new InMemoryContainerRepository());
        var created = new Container(id, MassLimit, VolumeLimit, items);
        scratch.AddContainer(created);
        var filled = loot.Fill(container.AreaType, container.ContainerKind, container.Danger, container.Seed, new ContainerItemSink(scratch, id));
        if (!filled.IsSuccess)
        {
            return new ContainerOpenResult(null, filled.Error, null, false);
        }

        if (!containers.TryAdd(created) && containers.TryGet(id, out var raced))
        {
            return new ContainerOpenResult(raced, null, null, false);
        }

        return new ContainerOpenResult(created, null, filled.Table, true);
    }
}
