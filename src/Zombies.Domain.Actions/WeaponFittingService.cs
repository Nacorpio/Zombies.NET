using Zombies.Domain.Combat;
using Zombies.Domain.Inventory;
using Zombies.Domain.Items;

namespace Zombies.Domain.Actions;

/// <summary>
/// Outcome of fitting or removing an Attachment: refused by the weapon's rules, refused by the Containers, or done with the
/// events of both.
/// </summary>
public sealed record FittingResult(WeaponError? WeaponError, InventoryError? InventoryError, IReadOnlyList<IDomainEvent> Events)
{
    public bool IsSuccess => WeaponError is null && InventoryError is null;

    public static FittingResult Refused(WeaponError error) => new(error, null, []);

    public static FittingResult Refused(InventoryError error) => new(null, error, []);
}

/// <summary>
/// Fits Attachments that sit in Containers to weapons that sit in Containers, and takes them back out. The weapon's rules
/// decide whether an Attachment fits a Mount; the Inventory decides whether the Containers have room. A refusal from
/// either leaves everything as it was.
/// </summary>
public sealed class WeaponFittingService(InventoryService inventory, IContainerRepository containers, WeaponService weapons)
{
    public WeaponService Weapons => weapons;

    /// <summary>What fitting an Attachment to a Mount would do to a weapon, without doing it: the new state, or why it is refused.</summary>
    public WeaponResult Preview(ItemId weapon, ItemState? state, ItemId attachment, string mount) =>
        weapons.Attach(weapon, state, attachment, mount);

    /// <summary>Fits one Attachment from a Stack to the <paramref name="mount"/> of the weapon in another Stack.</summary>
    public FittingResult Fit(ContainerId weaponContainer, StackId weapon, string mount, ContainerId from, StackId attachment)
    {
        if (Find(weaponContainer, weapon) is not { } held || Find(from, attachment) is not { } part)
        {
            return FittingResult.Refused(InventoryError.UnknownStack);
        }

        var attached = weapons.Attach(held.Item, held.State, part.Item, mount);
        if (!attached.IsSuccess)
        {
            return FittingResult.Refused(attached.Error!.Value);
        }

        var moved = inventory.FitInto(from, attachment, weaponContainer, weapon, attached.State!);
        return moved.IsSuccess
            ? new FittingResult(null, null, [.. moved.Events, .. attached.Events])
            : FittingResult.Refused(moved.Error!.Value);
    }

    /// <summary>Takes the Attachment on <paramref name="mount"/> off the weapon and puts it in the <paramref name="to"/> Container.</summary>
    public FittingResult Remove(ContainerId weaponContainer, StackId weapon, string mount, ContainerId to)
    {
        if (Find(weaponContainer, weapon) is not { } held)
        {
            return FittingResult.Refused(InventoryError.UnknownStack);
        }

        if (weapons.FittedTo(held.State, mount) is not { } attachment)
        {
            return FittingResult.Refused(WeaponError.NotFitted);
        }

        var detached = weapons.Detach(held.Item, held.State, attachment);
        if (!detached.IsSuccess)
        {
            return FittingResult.Refused(detached.Error!.Value);
        }

        var moved = inventory.TakeOutOf(weaponContainer, weapon, detached.State, attachment, to);
        return moved.IsSuccess
            ? new FittingResult(null, null, [.. moved.Events, .. detached.Events])
            : FittingResult.Refused(moved.Error!.Value);
    }

    private ItemStack? Find(ContainerId container, StackId stack) =>
        containers.TryGet(container, out var found) ? found.Stacks.FirstOrDefault(s => s.Id == stack) : null;
}
