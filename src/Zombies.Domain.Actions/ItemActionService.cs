using Zombies.Domain.Combat;
using Zombies.Domain.Inventory;
using Zombies.Domain.Items;

namespace Zombies.Domain.Actions;

/// <summary>Where a Stack is, from the player's point of view.</summary>
public sealed record ItemActionContext(bool IsOwnContainer, bool IsReadOnly);

/// <summary>An Item action offered for a Stack, with whether it can run now and why not when it cannot.</summary>
public sealed record AvailableAction(string Id, string Label, string Icon, string Group, int Order, bool IsEnabled, string? DisabledReason);

/// <summary>
/// Answers what a player can do with a Stack. Actions that make no sense for the Stack are left out;
/// actions that make sense but cannot run right now are returned disabled with a reason key.
/// </summary>
public sealed class ItemActionService(
    ItemActionCatalog catalog,
    IItemCatalog items,
    IWearableCatalog wearables,
    WeaponCatalog weapons)
{
    public IReadOnlyList<AvailableAction> Available(ItemStack stack, ItemActionContext context)
    {
        ArgumentNullException.ThrowIfNull(stack);
        ArgumentNullException.ThrowIfNull(context);

        var offered = new List<AvailableAction>();
        foreach (var definition in catalog.All)
        {
            if (!definition.AppliesWhen.All(c => Applies(c, stack)))
            {
                continue;
            }

            var failing = definition.EnabledWhen.Where(c => !Enabled(c, context)).ToList();
            offered.Add(failing.Count == 0
                ? new AvailableAction(definition.Id, definition.Label, definition.Icon, definition.Group, definition.Order, true, null)
                : new AvailableAction(definition.Id, definition.Label, definition.Icon, definition.Group, definition.Order, false, definition.DisabledReason ?? ItemActionDefinition.ReasonFor(failing[0])));
        }

        return [.. offered.OrderBy(a => a.Group, StringComparer.Ordinal).ThenBy(a => a.Order).ThenBy(a => a.Id, StringComparer.Ordinal)];
    }

    private bool Applies(ActionCondition condition, ItemStack stack) => condition switch
    {
        ActionCondition.StackCountAboveOne => stack.Count > 1,
        ActionCondition.Wearable => wearables.TryGet(stack.Item, out _),
        ActionCondition.Weapon => weapons.TryGetWeapon(stack.Item, out _),
        ActionCondition.Edible => Is(stack.Item, d => d.Edible),
        ActionCondition.Drinkable => Is(stack.Item, d => d.Drinkable),
        ActionCondition.Consumable => Is(stack.Item, d => d.Edible || d.Drinkable),
        _ => false,
    };

    private static bool Enabled(ActionCondition condition, ItemActionContext context) => condition switch
    {
        ActionCondition.OwnContainer => context.IsOwnContainer,
        ActionCondition.WritableContainer => !context.IsReadOnly,
        _ => false,
    };

    private bool Is(ItemId item, Func<ItemDefinition, bool> test) => items.TryGet(item, out var definition) && test(definition);
}
