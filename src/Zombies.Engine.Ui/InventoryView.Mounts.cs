using System.Globalization;
using Zombies.Domain.Combat;
using Zombies.Domain.Inventory;
using Zombies.Domain.Items;

namespace Zombies.Engine.Ui;

/// <summary>A weapon opened in the inventory to show its Mounts.</summary>
public sealed record OpenedWeapon(ContainerId Container, StackId Stack, ItemId Item, string Label);

/// <summary>One Mount of the opened weapon as the screen shows it, with the Attachment fitted to it, if any.</summary>
public sealed record MountSlot(string Mount, string Label, ItemId? Fitted, string? FittedLabel);

/// <summary>How one stat of a weapon would change, with its localized name.</summary>
public sealed record StatChange(StatName Stat, string Label, double Before, double After)
{
    public double Delta => After - Before;
}

/// <summary>The opened weapon's Mounts: fitting Attachments by dropping them on a Mount, and taking them off by dragging them to a Container.</summary>
public sealed partial class InventoryView
{
    private static readonly StatName[] ShownStats = [WeaponService.Damage, WeaponService.RateOfFire, WeaponService.Reach, WeaponService.Handling, WeaponService.Noise];

    private static readonly DialogButton[] RefusalButtons = [new("ok", "dialog.mount_refused.ok", IsCancel: true)];

    public OpenedWeapon? Weapon { get; private set; }

    /// <summary>A captioned dialog that explains the last refused fit, or null. The screen shows it and then calls <see cref="DismissRefusal"/>.</summary>
    public Dialog? Refusal { get; private set; }

    /// <summary>Opens a weapon to show its Mounts. False, and nothing opened, when the Stack is not a weapon with Mounts.</summary>
    public bool OpenWeapon(ContainerId container, StackId stack)
    {
        if (fitting is null || !TryContainer(container, out var found) || Stack(found, stack) is not { } item || fitting.Weapons.Mounts(item.Item).Count == 0)
        {
            return false;
        }

        Weapon = new OpenedWeapon(container, stack, item.Item, Label(item.Item));
        return true;
    }

    public void CloseWeapon() => Weapon = null;

    public void DismissRefusal() => Refusal = null;

    /// <summary>Every Mount the opened weapon offers, in the weapon's order, or none when no weapon is open.</summary>
    public IReadOnlyList<MountSlot> Mounts
    {
        get
        {
            if (fitting is null || OpenedStack() is not { } weapon)
            {
                return [];
            }

            return [.. fitting.Weapons.Mounts(weapon.Item).Select(mount => fitting.Weapons.FittedTo(weapon.State, mount) is { } fitted
                ? new MountSlot(mount, MountLabel(mount), fitted, Label(fitted))
                : new MountSlot(mount, MountLabel(mount), null, null))];
        }
    }

    /// <summary>Where each of <see cref="Mounts"/> goes inside a panel: one row of equal slots, in the same order.</summary>
    public IReadOnlyList<UiRect> ArrangeMounts(UiRect panel, int textScale)
    {
        var count = Mounts.Count;
        if (count == 0)
        {
            return [];
        }

        var gap = SlotGapPixels * textScale;
        var width = (panel.Width - (gap * (count - 1))) / count;
        var height = Math.Min(panel.Height, 2 * SlotHeightPixels * textScale);
        return [.. Enumerable.Range(0, count).Select(i => new UiRect(panel.X + (i * (width + gap)), panel.Y, width, height))];
    }

    /// <summary>Picks up the Attachment on a Mount of the opened weapon. It comes off only when it is dropped into a Container.</summary>
    public bool BeginDragFromMount(string mount)
    {
        Message = null;
        if (Weapon is not { } opened || Mounts.FirstOrDefault(m => m.Mount == mount)?.Fitted is not { } fitted)
        {
            return false;
        }

        Dragging = new DraggedItem(opened.Container, opened.Stack, fitted, 1, null, mount);
        return true;
    }

    /// <summary>Drops what is being dragged on a Mount of the opened weapon, fitting it. A refusal says why and changes nothing.</summary>
    public bool DropOnMount(string mount)
    {
        if (Dragging is not { } dragged || fitting is null || Weapon is not { } opened)
        {
            return false;
        }

        if (dragged.FromMount is { } from)
        {
            // An Attachment belongs on one Mount only, so putting it back where it was is the only drop that can succeed.
            Dragging = null;
            return from == mount || Refuse(mount, dragged.Item, WeaponError.WrongMount);
        }

        var result = fitting.Fit(opened.Container, opened.Stack, mount, dragged.From, dragged.Stack);
        if (!result.IsSuccess)
        {
            return result.WeaponError is { } weaponError ? Refuse(mount, dragged.Item, weaponError) : Refuse(mount, dragged.Item, result.InventoryError!.Value);
        }

        Dragging = null;
        Message = null;
        return true;
    }

    /// <summary>
    /// How the opened weapon's stats would change if what is being dragged were dropped on <paramref name="mount"/>, or, when
    /// nothing is dragged, what the Attachment fitted there adds. Empty when nothing would change or the fit would be refused.
    /// </summary>
    public IReadOnlyList<StatChange> Changes(string mount)
    {
        if (fitting is null || OpenedStack() is not { } weapon)
        {
            return [];
        }

        if (Dragging is { FromMount: null } dragged)
        {
            var preview = fitting.Preview(weapon.Item, weapon.State, dragged.Item, mount);
            return preview.IsSuccess ? Compare(weapon.Item, weapon.State, preview.State) : [];
        }

        return fitting.Weapons.FittedTo(weapon.State, mount) is { } fitted
            ? Compare(weapon.Item, weapon.State?.WithoutAttached(fitted), weapon.State)
            : [];
    }

    /// <summary>
    /// The tooltip for a Mount: its name, then the stat changes the dragged Attachment would make or why the Mount refuses
    /// it, then the Attachment as the caption.
    /// </summary>
    public Tooltip MountTooltip(string mount)
    {
        ArgumentNullException.ThrowIfNull(mount);
        var weapon = OpenedStack();
        if (fitting is null || weapon is null)
        {
            return Tooltip.Create(MountLabel(mount), localizer.Get("mount.free"));
        }

        if (Dragging is { FromMount: null } dragged)
        {
            var preview = fitting.Preview(weapon.Item, weapon.State, dragged.Item, mount);
            var body = preview.IsSuccess ? Describe(Changes(mount)) : Reason(dragged.Item, preview.Error!.Value);
            return Tooltip.Create(MountLabel(mount), body, Label(dragged.Item));
        }

        return fitting.Weapons.FittedTo(weapon.State, mount) is { } fitted
            ? Tooltip.Create(MountLabel(mount), Describe(Changes(mount)), Label(fitted))
            : Tooltip.Create(MountLabel(mount), localizer.Get("mount.free"));
    }

    private bool TakeOff(DraggedItem dragged, string mount, ContainerId to)
    {
        var result = fitting?.Remove(dragged.From, dragged.Stack, mount, to);
        if (result is null || !result.IsSuccess)
        {
            Message = result?.WeaponError is { } weaponError
                ? Reason(dragged.Item, weaponError)
                : localizer.Get($"error.{Snake((result?.InventoryError ?? InventoryError.UnknownStack).ToString())}");
            return false;
        }

        Dragging = null;
        Message = null;
        return true;
    }

    private bool Refuse(string mount, ItemId attachment, WeaponError error) => Refuse(mount, attachment, Reason(attachment, error));

    private bool Refuse(string mount, ItemId attachment, InventoryError error) => Refuse(mount, attachment, localizer.Get($"error.{Snake(error.ToString())}"));

    private bool Refuse(string mount, ItemId attachment, string reason)
    {
        Message = reason;
        Refusal = Dialog.CreateText(
            localizer,
            localizer.Get("dialog.mount_refused.title"),
            reason,
            localizer.Format("dialog.mount_refused.caption", Label(attachment), MountLabel(mount)),
            RefusalButtons);
        return false;
    }

    /// <summary>Why a Mount refuses an Attachment, in a few words. A wrong Mount names the one the Attachment belongs on.</summary>
    private string Reason(ItemId attachment, WeaponError error)
    {
        var key = $"mount.refused.{Snake(error.ToString())}";
        return error == WeaponError.WrongMount && fitting?.Weapons.MountFor(attachment) is { } right
            ? localizer.Format(key, MountLabel(right))
            : localizer.Get(key);
    }

    private List<StatChange> Compare(ItemId weapon, ItemState? before, ItemState? after)
    {
        if (fitting is null || !fitting.Weapons.TryGetEffectiveStats(weapon, before, out var was) || !fitting.Weapons.TryGetEffectiveStats(weapon, after, out var now))
        {
            return [];
        }

        var changes = new List<StatChange>();
        foreach (var stat in ShownStats)
        {
            var (from, to) = (Value(was, stat), Value(now, stat));
            if (Math.Abs(to - from) > 1e-9)
            {
                changes.Add(new StatChange(stat, localizer.Get($"stat.{stat.Value}"), from, to));
            }
        }

        return changes;
    }

    private string Describe(IReadOnlyList<StatChange> changes) =>
        changes.Count == 0
            ? localizer.Get("mount.no_change")
            : string.Join(", ", changes.Select(c => localizer.Format("mount.stat_change", c.Label, c.Delta.ToString("+0.#;-0.#", CultureInfo.InvariantCulture))));

    private static double Value(WeaponStats stats, StatName stat) =>
        stat == WeaponService.Damage ? stats.Damage
        : stat == WeaponService.RateOfFire ? stats.RateOfFire
        : stat == WeaponService.Reach ? stats.Reach
        : stat == WeaponService.Handling ? stats.Handling
        : stats.Noise;

    private string MountLabel(string mount)
    {
        var key = $"mount.{mount}";
        var text = localizer.Get(key);
        return string.Equals(text, key, StringComparison.Ordinal) ? char.ToUpperInvariant(mount[0]) + mount[1..].Replace('_', ' ') : text;
    }

    private ItemStack? OpenedStack() =>
        Weapon is { } opened && TryContainer(opened.Container, out var found) ? Stack(found, opened.Stack) : null;
}
