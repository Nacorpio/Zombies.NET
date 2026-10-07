using System.Globalization;
using Zombies.Domain.Actions;
using Zombies.Domain.Inventory;
using Zombies.Domain.Items;

namespace Zombies.Engine.Ui;

/// <summary>How Item actions treat a container: whether it is the player's own, whether it can be changed, and where a Stack dropped from it goes.</summary>
public sealed record ContainerRole(bool IsOwn, bool IsReadOnly, ContainerId? DropInto = null);

/// <summary>An Item the player used from a container. The Inventory command has already taken it out; the host tells the Server, which applies what the Item does.</summary>
public sealed record ItemUsed(ContainerId Container, ItemId Item, ItemState? State);

/// <summary>An action the player chose that has no built-in Domain command, such as one a mod defined, for the host to handle.</summary>
public sealed record ItemActionChosen(string Action, ContainerId Container, StackId Stack);

public sealed partial class InventoryView
{
    private readonly Dictionary<ContainerId, ContainerRole> _roles = [];
    private ContextMenuHost? _menu;
    private (ContainerId Container, StackId Stack)? _menuTarget;

    /// <summary>The menu a right click opens. At most one is open at a time.</summary>
    public ContextMenuHost Menu => _menu ??= CreateMenu();

    /// <summary>What the last Inspect chose to show about a Stack that is not a weapon, or null. The screen shows it until <see cref="DismissInspection"/>.</summary>
    public Tooltip? Inspection { get; private set; }

    /// <summary>Raised after a Stack's Item was used and taken out of its container.</summary>
    public event Action<ItemUsed>? Used;

    /// <summary>Raised when an action with no built-in Domain command was chosen.</summary>
    public event Action<ItemActionChosen>? ActionChosen;

    /// <summary>Says how Item actions treat a container. A container with no role is not the player's own and can be changed.</summary>
    public void SetRole(ContainerId container, ContainerRole role)
    {
        ArgumentNullException.ThrowIfNull(role);
        _roles[container] = role;
    }

    public void DismissInspection() => Inspection = null;

    /// <summary>
    /// Opens the menu for a Stack at the pointer, listing exactly the Item actions the Domain says it offers, in the Domain's order and
    /// groups, with the ones that cannot run now disabled and their reason. False, and nothing open, when there is no such Stack, nothing is
    /// offered, or a Stack is being dragged. Any menu already open closes either way.
    /// </summary>
    public bool OpenMenu(ContainerId container, StackId stack, float pointerX, float pointerY, UiRect screen, int textScale)
    {
        Menu.Close();
        _menuTarget = null;
        if (actions is null || Dragging is not null || !TryContainer(container, out var found) || Stack(found, stack) is not { } item)
        {
            return false;
        }

        var available = actions.Available(item, ContextOf(container));
        if (available.Count == 0)
        {
            return false;
        }

        var entries = available.Select((a, i) => new ContextMenuEntry(
            a.Id,
            a.Label,
            a.Icon,
            DisabledReasonKey: a.IsEnabled ? null : $"menu.reason.{a.DisabledReason}",
            StartsGroup: i > 0 && a.Group != available[i - 1].Group)).ToList();
        Menu.Open(ContextMenu.Create(localizer, entries), pointerX, pointerY, screen, textScale);
        _menuTarget = (container, stack);
        return true;
    }

    /// <summary>
    /// Runs an Item action on a Stack through the matching Domain command. The action must still be offered and enabled, so a menu that went
    /// stale cannot run what the Domain would now refuse. False, with <see cref="Message"/> set, when it did not run.
    /// </summary>
    public bool Perform(string action, ContainerId container, StackId stack)
    {
        ArgumentNullException.ThrowIfNull(action);
        Message = null;
        Inspection = null;
        if (actions is null || !TryContainer(container, out var found) || Stack(found, stack) is not { } item)
        {
            Message = localizer.Get("error.unknown_stack");
            return false;
        }

        var offered = actions.Available(item, ContextOf(container)).FirstOrDefault(a => a.Id == action);
        if (offered is not { IsEnabled: true })
        {
            Message = localizer.Get("error.nothing_to_do");
            return false;
        }

        switch (action)
        {
            case ItemActionIds.Use:
                return Use(container, item);
            case ItemActionIds.Equip:
                return Equip(container, item);
            case ItemActionIds.Drop:
                return Drop(container, item);
            case ItemActionIds.Split:
                return Succeeded(inventory.SplitStack(container, item.Id, item.Count / 2));
            case ItemActionIds.Inspect:
                return Inspect(container, item);
            default:
                ActionChosen?.Invoke(new ItemActionChosen(action, container, item.Id));
                return true;
        }
    }

    private ContextMenuHost CreateMenu()
    {
        var host = new ContextMenuHost();
        host.Chosen += OnChosen;
        return host;
    }

    private void OnChosen(string action)
    {
        if (_menuTarget is not { } target)
        {
            return;
        }

        _menuTarget = null;
        Perform(action, target.Container, target.Stack);
    }

    private ItemActionContext ContextOf(ContainerId container)
    {
        var role = _roles.GetValueOrDefault(container) ?? new ContainerRole(IsOwn: false, IsReadOnly: false);
        return new ItemActionContext(role.IsOwn, role.IsReadOnly);
    }

    private bool Use(ContainerId container, ItemStack item)
    {
        if (!Succeeded(inventory.RemoveItems(container, item.Item, 1, item.State)))
        {
            return false;
        }

        Used?.Invoke(new ItemUsed(container, item.Item, item.State));
        return true;
    }

    private bool Equip(ContainerId container, ItemStack item)
    {
        if (outfit is null)
        {
            Message = localizer.Get("error.not_wearable");
            return false;
        }

        if (!Succeeded(outfit.Equip(item.Item)))
        {
            return false;
        }

        // The Outfit accepted it, so taking one out of the container should always work; if it somehow does not, put things back.
        if (!Succeeded(inventory.RemoveItems(container, item.Item, 1, item.State)))
        {
            outfit.Unequip(item.Item);
            return false;
        }

        return true;
    }

    private bool Drop(ContainerId container, ItemStack item)
    {
        if (_roles.GetValueOrDefault(container)?.DropInto is not { } target)
        {
            Message = localizer.Get("error.nothing_to_do");
            return false;
        }

        return Succeeded(inventory.MoveItems(container, item.Id, target, item.Count));
    }

    private bool Inspect(ContainerId container, ItemStack item)
    {
        if (OpenWeapon(container, item.Id))
        {
            return true;
        }

        CloseWeapon();
        var lines = new List<string> { localizer.Format("inspect.count", item.Count) };
        if (catalog.TryGet(item.Item, out var definition))
        {
            lines.Add(localizer.Format("inspect.mass", Number(definition.UnitMass.Kilograms * item.Count)));
            lines.Add(localizer.Format("inspect.volume", Number(definition.UnitVolume.Liters * item.Count)));
        }

        Inspection = Tooltip.Create(Label(item.Item), string.Join(", ", lines));
        return true;
    }

    /// <summary>Records why a command was refused, or returns true when it succeeded.</summary>
    private bool Succeeded(InventoryResult result)
    {
        if (result.IsSuccess)
        {
            return true;
        }

        Message = localizer.Get($"error.{Snake(result.Error!.Value.ToString())}");
        return false;
    }

    private static string Number(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}
