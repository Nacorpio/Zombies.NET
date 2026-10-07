using System.Numerics;
using Zombies.Domain.Combat;
using Zombies.Domain.Inventory;
using Zombies.Domain.Items;

namespace Zombies.Domain.Death;

/// <summary>
/// The rules of death. A dead character's carried and worn Stacks move into a corpse Container with their Item state, the
/// Corpse is stored where they died, and a Memorial records how they died. Anyone can then loot the Corpse.
/// </summary>
public sealed class DeathService(IItemCatalog items, DeathStores stores)
{
    /// <summary>The Item state names a worn Item's Wear state is kept under on the Stack it becomes, in whole percent.</summary>
    public const string WetnessState = "wetness";

    public const string ConditionState = "condition";

    private long _nextContainerId;

    /// <summary>A Container id no stored Container uses, so a character's carried Container and a corpse never share one.</summary>
    public ContainerId NextContainerId()
    {
        if (_nextContainerId == 0)
        {
            _nextContainerId = stores.Containers.Ids().Select(id => id.Value).DefaultIfEmpty(0).Max() + 1;
        }

        return new ContainerId(_nextContainerId++);
    }

    /// <summary>
    /// Empties <paramref name="carried"/> and takes everything off <paramref name="worn"/> into a new corpse Container.
    /// The corpse is sized to hold exactly that, so nothing is lost.
    /// </summary>
    /// <exception cref="InvalidOperationException">An item is not in the Item catalog, so it could not be moved without losing it.</exception>
    public DeathReport Die(Container carried, Outfit worn, string player, DeathCause cause, int daysSurvived, int kills, Vector3 position)
    {
        ArgumentNullException.ThrowIfNull(carried);
        ArgumentNullException.ThrowIfNull(worn);
        ArgumentException.ThrowIfNullOrEmpty(player);

        var wornStacks = worn.ToSnapshot().Worn.Select(w => (Item: new ItemId(w.Item), State: WearState(w))).ToList();
        var mass = carried.TotalMass;
        var volume = carried.TotalVolume;
        foreach (var (item, _) in wornStacks)
        {
            mass += Definition(item).UnitMass;
            volume += Definition(item).UnitVolume;
        }

        var corpse = new Container(NextContainerId(), mass, volume, items);
        foreach (var (item, state) in wornStacks)
        {
            Require(corpse.TryAdd(item, 1, state), item.ToString());
            Require(worn.Unequip(item), item.ToString());
        }

        Require(carried.MoveFittingTo(corpse), "the carried Stacks");
        if (carried.Stacks.Count > 0)
        {
            throw new InvalidOperationException("The corpse Container could not hold everything the character carried.");
        }

        var record = new Corpse(corpse.Id, player, position);
        var memorial = new Memorial(player, daysSurvived, kills, cause);
        stores.Containers.Save(corpse);
        stores.Corpses.Save(record);
        stores.Memorials.Add(memorial);
        return new DeathReport(record, memorial);
    }

    /// <summary>
    /// Moves what <paramref name="looter"/> has room for out of the Corpse, and removes a Corpse nothing is left in.
    /// False when the Corpse's Container is gone.
    /// </summary>
    public bool Loot(Corpse corpse, Container looter, out bool emptied)
    {
        ArgumentNullException.ThrowIfNull(corpse);
        ArgumentNullException.ThrowIfNull(looter);
        emptied = false;
        if (!stores.Containers.TryGet(corpse.Container, out var container))
        {
            return false;
        }

        Require(container.MoveFittingTo(looter), "the corpse's Stacks");
        stores.Containers.Save(container);
        emptied = container.Stacks.Count == 0;
        if (emptied)
        {
            stores.Corpses.Remove(corpse.Container);
        }

        return true;
    }

    private ItemDefinition Definition(ItemId item) =>
        items.TryGet(item, out var definition) ? definition : throw new InvalidOperationException($"{item} is not in the Item catalog, so it cannot be moved to a corpse.");

    private static ItemState? WearState(WornSnapshot worn)
    {
        var wetness = (int)Math.Round(worn.Wetness * 100);
        var condition = (int)Math.Round(worn.Condition * 100);
        var state = wetness == 0 && condition == 100 ? null : ItemState.Create([new(WetnessState, wetness), new(ConditionState, condition)]);
        return (worn.Faults ?? []).Aggregate(state, (s, fault) => ItemFaults.With(s, fault));
    }

    private static void Require(InventoryResult result, string what)
    {
        if (!result.IsSuccess)
        {
            throw new InvalidOperationException($"Moving {what} to a corpse failed: {result.Error}.");
        }
    }
}
