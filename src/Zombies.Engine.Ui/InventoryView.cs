using Zombies.Domain.Combat;
using Zombies.Domain.Inventory;
using Zombies.Domain.Items;

namespace Zombies.Engine.Ui;

/// <summary>How the slots of a container are laid out. Both views show the same model in the same order.</summary>
public enum InventoryViewMode
{
    List,
    Grid,
}

/// <summary>One Stack as the screen shows it: a localized name, a count, and the wear a weapon carries.</summary>
public sealed record InventorySlot(ContainerId Container, StackId Stack, ItemId Item, string Label, int Count, int? Condition, int? Rounds);

/// <summary>A container the player can drag Stacks into, with the title shown above it.</summary>
public sealed record InventoryTarget(ContainerId Container, string Title);

/// <summary>A container as a panel: its title, its slots, and how full it is.</summary>
public sealed record InventoryPanel(ContainerId Container, string Title, IReadOnlyList<InventorySlot> Slots, double MassFraction, double VolumeFraction);

/// <summary>A Stack picked up and not yet dropped.</summary>
public sealed record DraggedItem(ContainerId From, StackId Stack, ItemId Item, int Count, ItemState? State);

/// <summary>
/// The inventory screen's model. It reads Containers through the repository and changes them only through the Inventory
/// commands, so a drag either succeeds or leaves everything as it was. The list and the grid are two ways of drawing the
/// same slots.
/// </summary>
public sealed class InventoryView(InventoryService inventory, IContainerRepository containers, IItemCatalog catalog, Localizer localizer)
{
    private const int SlotHeightPixels = 10;
    private const int SlotGapPixels = 2;

    private readonly List<InventoryTarget> _targets = [];

    public InventoryViewMode Mode { get; set; } = InventoryViewMode.List;

    /// <summary>How many slots a row of the grid holds. Ignored in list mode.</summary>
    public int Columns { get; set; } = 4;

    public IReadOnlyList<InventoryTarget> Targets => _targets;

    public DraggedItem? Dragging { get; private set; }

    /// <summary>Why the last drag was refused, in the chosen language, or null when nothing went wrong.</summary>
    public string? Message { get; private set; }

    public void AddTarget(ContainerId container, string titleKey)
    {
        ArgumentNullException.ThrowIfNull(titleKey);
        _targets.Add(new InventoryTarget(container, localizer.Get(titleKey)));
    }

    public InventoryPanel Panel(ContainerId container)
    {
        var found = Container(container);
        return new InventoryPanel(
            container,
            _targets.Find(t => t.Container == container)?.Title ?? container.ToString(),
            Slots(container),
            Fraction(found.TotalMass.Kilograms, found.MassLimit.Kilograms),
            Fraction(found.TotalVolume.CubicMeters, found.VolumeLimit.CubicMeters));
    }

    public IReadOnlyList<InventorySlot> Slots(ContainerId container) =>
        [.. Container(container).Stacks.Select(s => new InventorySlot(
            container,
            s.Id,
            s.Item,
            Label(s.Item),
            s.Count,
            s.State is null ? null : WeaponService.ConditionOf(s.State),
            s.State is null ? null : WeaponService.RoundsOf(s.State)))];

    /// <summary>Where each slot goes inside a panel. A list is one column; a grid wraps after <see cref="Columns"/>. Slots that do not fit are left out.</summary>
    public IReadOnlyList<UiRect> ArrangeSlots(ContainerId container, UiRect panel, int textScale)
    {
        var slots = Slots(container);
        var height = SlotHeightPixels * textScale;
        var gap = SlotGapPixels * textScale;
        var columns = Mode == InventoryViewMode.Grid ? Math.Max(1, Columns) : 1;
        var width = (panel.Width - (gap * (columns - 1))) / columns;
        var rows = (int)Math.Floor((panel.Height + gap) / (height + gap));
        var capacity = Math.Max(0, rows * columns);

        var rects = new List<UiRect>(Math.Min(slots.Count, capacity));
        for (var i = 0; i < slots.Count && i < capacity; i++)
        {
            var column = i % columns;
            var row = i / columns;
            rects.Add(new UiRect(panel.X + (column * (width + gap)), panel.Y + (row * (height + gap)), width, height));
        }

        return rects;
    }

    /// <summary>Picks a Stack up. Nothing moves until it is dropped.</summary>
    public bool BeginDrag(ContainerId container, StackId stack)
    {
        Message = null;
        if (!TryContainer(container, out var found) || Stack(found, stack) is not { } item)
        {
            return false;
        }

        Dragging = new DraggedItem(container, stack, item.Item, item.Count, item.State);
        return true;
    }

    /// <summary>Drops what is being dragged. <paramref name="onto"/> names a Stack to merge into, or null to just move it into the container.</summary>
    public bool DropOn(ContainerId container, StackId? onto = null)
    {
        if (Dragging is not { } dragged)
        {
            return false;
        }

        if (dragged.From == container)
        {
            Dragging = null;
            Message = null;
            return true;
        }

        // Everything that can be refused is checked before anything moves, so a refused drop changes nothing.
        if (onto is { } target && !CanMergeInto(container, target, dragged, out var refusal))
        {
            Message = localizer.Get($"error.{refusal}");
            return false;
        }

        // Moving already tops up a partial Stack of the same Item, so dropping onto one merges without a second command.
        var move = inventory.MoveItems(dragged.From, dragged.Stack, container, dragged.Count);
        if (!move.IsSuccess)
        {
            Message = localizer.Get($"error.{Snake(move.Error!.Value.ToString())}");
            return false;
        }

        Dragging = null;
        Message = null;
        return true;
    }

    public void CancelDrag() => Dragging = null;

    /// <summary>Whether a Stack can be merged into another: same Item, same state, and room for both.</summary>
    private bool CanMergeInto(ContainerId container, StackId target, DraggedItem dragged, out string refusal)
    {
        refusal = "unknown";
        if (!TryContainer(container, out var destination) || Stack(destination, target) is not { } existing)
        {
            refusal = "unknown_stack";
            return false;
        }

        if (existing.Item != dragged.Item)
        {
            refusal = "item_mismatch";
            return false;
        }

        if (!Equals(existing.State, dragged.State))
        {
            refusal = "state_mismatch";
            return false;
        }

        if (!catalog.TryGet(existing.Item, out var definition) || existing.Count + dragged.Count > definition.MaxStack)
        {
            refusal = "stack_full";
            return false;
        }

        return true;
    }

    /// <summary>The Stack the moved items ended up in, which is the newest one holding them.</summary>
    private StackId? NewestStack(ContainerId container, DraggedItem dragged)
    {
        if (!TryContainer(container, out var destination))
        {
            return null;
        }

        StackId? newest = null;
        foreach (var stack in destination.Stacks.Where(s => s.Item == dragged.Item && Equals(s.State, dragged.State)))
        {
            if (newest is null || stack.Id.Value > newest.Value.Value)
            {
                newest = stack.Id;
            }
        }

        return newest;
    }

    private static string Snake(string name)
    {
        var builder = new System.Text.StringBuilder(name.Length + 4);
        foreach (var c in name)
        {
            if (char.IsUpper(c) && builder.Length > 0)
            {
                builder.Append('_');
            }

            builder.Append(char.ToLowerInvariant(c));
        }

        return builder.ToString();
    }

    private string Label(ItemId item)
    {
        var key = $"item.{item.Value.Replace(':', '.').Replace('/', '.')}";
        var text = localizer.Get(key);
        if (!string.Equals(text, key, StringComparison.Ordinal))
        {
            return text;
        }

        // No translation: show the last part of the Content ID as words, which reads better than the raw key.
        var name = item.Value[(item.Value.LastIndexOf('/') + 1)..].Replace('_', ' ');
        return name.Length == 0 ? item.Value : char.ToUpperInvariant(name[0]) + name[1..];
    }

    private Container Container(ContainerId id) => TryContainer(id, out var found) ? found : throw new ArgumentException($"Unknown container '{id}'.", nameof(id));

    private static ItemStack? Stack(Container container, StackId id) => container.Stacks.FirstOrDefault(s => s.Id == id);

    private bool TryContainer(ContainerId id, out Container container) => containers.TryGet(id, out container!);

    private static double Fraction(double used, double limit) => limit <= 0 ? 0 : Math.Clamp(used / limit, 0, 1);
}
