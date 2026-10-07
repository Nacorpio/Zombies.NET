using Zombies.Engine.Platform;
using Zombies.Engine.Render;

namespace Zombies.Engine.Ui;

/// <summary>
/// One entry a context menu offers. A <see cref="DisabledReasonKey"/> makes the entry disabled and is the one-line reason shown
/// on hover. <see cref="StartsGroup"/> puts a thin divider above the entry, unless it is the first.
/// <see cref="Shortcut"/> is a short key name such as "F", shown as a hint at the right and not translated.
/// </summary>
public sealed record ContextMenuEntry(string Id, string LabelKey, string Icon, string? Shortcut = null, string? DisabledReasonKey = null, bool StartsGroup = false);

/// <summary>An entry as shown: localized text, whether it can be chosen, and where it was laid out.</summary>
public sealed class ContextMenuItem(string id, string label, string icon, string? shortcut, string? disabledReason, bool startsGroup)
{
    public string Id { get; } = id;

    public string Label { get; } = label;

    public string Icon { get; } = icon;

    public string? Shortcut { get; } = shortcut;

    public bool IsEnabled => DisabledReason is null;

    /// <summary>Why the entry cannot be chosen, on one line, or null when it can.</summary>
    public string? DisabledReason { get; } = disabledReason;

    public bool StartsGroup { get; } = startsGroup;

    public UiRect Bounds { get; internal set; }

    /// <summary>The thin line above this entry, or null when it starts no group.</summary>
    public UiRect? Divider { get; internal set; }
}

/// <summary>
/// A short list of entries, each with an Icon, a label and an optional shortcut hint, laid out beside the pointer and
/// kept fully on screen. This is pure layout and navigation; <see cref="ContextMenuHost"/> adds the one-menu-at-a-time and
/// close-on-click-outside rules, and <see cref="UiRenderer"/> draws it.
/// </summary>
public sealed class ContextMenu
{
    public const int MaxLabelChars = 24;
    public const int MaxShortcutChars = 6;
    public const int MaxReasonChars = Tooltip.MaxCharsPerLine;

    /// <summary>Icons are drawn at their own size, one pixel to one screen pixel, so they stay crisp at every UI scale.</summary>
    public const int IconScale = 1;

    private const int PaddingPixels = 4;
    private const int RowPaddingPixels = 2;
    private const int GapPixels = 4;
    private const int ShortcutGapPixels = 8;
    private const int DividerMarginPixels = 2;

    private readonly List<ContextMenuItem> _items;

    private ContextMenu(List<ContextMenuItem> items) => _items = items;

    public IReadOnlyList<ContextMenuItem> Items => _items;

    /// <summary>The entry under the pointer or reached by the keyboard, or -1 for none.</summary>
    public int HighlightedIndex { get; private set; } = -1;

    public ContextMenuItem? Highlighted => HighlightedIndex < 0 ? null : _items[HighlightedIndex];

    public UiRect Bounds { get; private set; }

    /// <summary>The text scale the menu was laid out at. It is lower than asked for when the menu would not otherwise fit the screen.</summary>
    public int EffectiveTextScale { get; private set; } = 1;

    /// <summary>Where the menu was opened, kept so it can be laid out again when the window or the UI scale changes.</summary>
    public (float X, float Y) Origin { get; private set; }

    public static ContextMenu Create(Localizer localizer, IReadOnlyList<ContextMenuEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count == 0)
        {
            throw new ArgumentException("A context menu needs at least one entry.", nameof(entries));
        }

        return new ContextMenu([.. entries.Select((e, i) => new ContextMenuItem(
            e.Id,
            TextWrap.Truncate(localizer.Get(e.LabelKey), MaxLabelChars),
            e.Icon,
            e.Shortcut is null ? null : TextWrap.Truncate(e.Shortcut, MaxShortcutChars),
            e.DisabledReasonKey is null ? null : TextWrap.Truncate(OneLine(localizer.Get(e.DisabledReasonKey)), MaxReasonChars),
            e.StartsGroup && i > 0))]);
    }

    /// <summary>
    /// Lays the menu out with its top left corner at the pointer. It opens to the left of the pointer when it would run off the right
    /// edge, and above it when it would run off the bottom, and is clamped to the screen last. When even that cannot fit, the text
    /// scale drops toward 1; a screen smaller than the menu at scale 1 shows it from the screen's top left corner.
    /// </summary>
    public void Arrange(float pointerX, float pointerY, UiRect screen, int textScale)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(textScale, 1);
        Origin = (pointerX, pointerY);
        var scale = textScale;
        var (width, height) = Measure(scale);
        while (scale > 1 && (width > screen.Width || height > screen.Height))
        {
            scale--;
            (width, height) = Measure(scale);
        }

        EffectiveTextScale = scale;
        var x = pointerX;
        if (x + width > screen.Right)
        {
            x = pointerX - width;
        }

        var y = pointerY;
        if (y + height > screen.Bottom)
        {
            y = pointerY - height;
        }

        x = Math.Clamp(x, screen.X, Math.Max(screen.X, screen.Right - width));
        y = Math.Clamp(y, screen.Y, Math.Max(screen.Y, screen.Bottom - height));
        Bounds = new UiRect(x, y, width, height);
        Place(scale);
    }

    /// <summary>The index of the entry at a point, or -1 over a divider, the padding, or outside the menu.</summary>
    public int IndexAt(float x, float y) => _items.FindIndex(i => i.Bounds.Contains(x, y));

    public bool Contains(float x, float y) => Bounds.Contains(x, y);

    /// <summary>Highlights the entry under the pointer. A pointer that is over no entry leaves the highlight where it was.</summary>
    public void Hover(float x, float y)
    {
        var index = IndexAt(x, y);
        if (index >= 0)
        {
            HighlightedIndex = index;
        }
    }

    /// <summary>
    /// Moves the highlight by one entry, wrapping at the ends. Disabled entries can be highlighted so the keyboard can reach their
    /// reason; they just cannot be chosen. With nothing highlighted, down goes to the first entry and up to the last.
    /// </summary>
    public void Move(int delta)
    {
        if (delta == 0)
        {
            return;
        }

        var count = _items.Count;
        HighlightedIndex = HighlightedIndex < 0
            ? (delta > 0 ? 0 : count - 1)
            : (((HighlightedIndex + Math.Sign(delta)) % count) + count) % count;
    }

    /// <summary>The id of the highlighted entry when it is enabled, otherwise null. Choosing a disabled entry does nothing.</summary>
    public string? Choose() => Highlighted is { IsEnabled: true } item ? item.Id : null;

    private static string OneLine(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static int RowHeight(int scale) => (Icons.Size * IconScale) + (2 * RowPaddingPixels * scale);

    private static int DividerHeight(int scale) => (2 * DividerMarginPixels * scale) + scale;

    private (float Width, float Height) Measure(int scale)
    {
        var label = _items.Max(i => i.Label.Length);
        var shortcut = _items.Max(i => i.Shortcut?.Length ?? 0);
        var width = (2 * PaddingPixels * scale)
            + (Icons.Size * IconScale)
            + (GapPixels * scale)
            + (label * DebugFont.Advance * scale)
            + (shortcut > 0 ? (ShortcutGapPixels * scale) + (shortcut * DebugFont.Advance * scale) : 0);
        var height = (2 * PaddingPixels * scale) + (_items.Count * RowHeight(scale)) + (_items.Count(i => i.StartsGroup) * DividerHeight(scale));
        return (width, height);
    }

    private void Place(int scale)
    {
        var pad = PaddingPixels * scale;
        var y = Bounds.Y + pad;
        foreach (var item in _items)
        {
            if (item.StartsGroup)
            {
                item.Divider = new UiRect(Bounds.X + pad, y + (DividerMarginPixels * scale), Bounds.Width - (2 * pad), scale);
                y += DividerHeight(scale);
            }
            else
            {
                item.Divider = null;
            }

            item.Bounds = new UiRect(Bounds.X, y, Bounds.Width, RowHeight(scale));
            y += RowHeight(scale);
        }
    }
}

/// <summary>
/// Owns the menu that is open. Opening a menu closes the one before it, so there is at most one. A click outside closes it, and
/// so does Escape; Up, Down, and Enter move and choose. While a menu is open, a click is consumed so it does not also act on
/// whatever lies underneath.
/// </summary>
public sealed class ContextMenuHost
{
    private bool _keyboardDriven;
    private (float X, float Y) _pointer;

    public ContextMenu? Current { get; private set; }

    public bool IsOpen => Current is not null;

    /// <summary>Raised with the id of an enabled entry the player chose. The menu is already closed.</summary>
    public event Action<string>? Chosen;

    /// <summary>Raised when a menu closes for any reason, including when another replaces it.</summary>
    public event Action<ContextMenu>? Closed;

    /// <summary>Opens a menu at the pointer, closing any menu already open.</summary>
    public void Open(ContextMenu menu, float pointerX, float pointerY, UiRect screen, int textScale)
    {
        ArgumentNullException.ThrowIfNull(menu);
        Close();
        menu.Arrange(pointerX, pointerY, screen, textScale);
        Current = menu;
        _keyboardDriven = false;
        _pointer = (pointerX, pointerY);
    }

    public void Close()
    {
        if (Current is { } menu)
        {
            Current = null;
            Closed?.Invoke(menu);
        }
    }

    /// <summary>Lays the open menu out again, at the same pointer, after the window or the UI scale changed.</summary>
    public void Rearrange(UiRect screen, int textScale) => Current?.Arrange(Current.Origin.X, Current.Origin.Y, screen, textScale);

    public void PointerMoved(float x, float y)
    {
        if (Current is { } menu)
        {
            _pointer = (x, y);
            _keyboardDriven = false;
            menu.Hover(x, y);
        }
    }

    /// <summary>
    /// Handles a primary button click. Returns whether a menu was open and took the click. A click on an enabled entry chooses it; a click
    /// on a disabled entry, a divider, or the padding does nothing; a click anywhere else closes the menu.
    /// </summary>
    public bool Click(float x, float y)
    {
        if (Current is not { } menu)
        {
            return false;
        }

        if (!menu.Contains(x, y))
        {
            Close();
            return true;
        }

        var index = menu.IndexAt(x, y);
        if (index >= 0 && menu.Items[index].IsEnabled)
        {
            menu.Hover(x, y);
            Choose(menu);
        }

        return true;
    }

    /// <summary>Handles a key. Returns whether the menu used it; other keys are left to the game.</summary>
    public bool KeyPressed(Key key)
    {
        if (Current is not { } menu)
        {
            return false;
        }

        switch (key)
        {
            case Key.Escape:
                Close();
                return true;
            case Key.Down:
                _keyboardDriven = true;
                menu.Move(1);
                return true;
            case Key.Up:
                _keyboardDriven = true;
                menu.Move(-1);
                return true;
            case Key.Enter:
                Choose(menu);
                return true;
            default:
                return false;
        }
    }

    /// <summary>The tooltip to show: the reason the highlighted entry is disabled, or null when there is nothing to explain.</summary>
    public Tooltip? ReasonTooltip => Current?.Highlighted is { DisabledReason: { } reason } ? Tooltip.Create(null, reason) : null;

    /// <summary>Where to place <see cref="ReasonTooltip"/>: the pointer, or the middle of the entry when the keyboard chose it.</summary>
    public (float X, float Y) TooltipAnchor
    {
        get
        {
            if (_keyboardDriven && Current?.Highlighted is { } item)
            {
                return (item.Bounds.X + (item.Bounds.Width / 2), item.Bounds.Y + (item.Bounds.Height / 2));
            }

            return _pointer;
        }
    }

    private void Choose(ContextMenu menu)
    {
        if (menu.Choose() is not { } id)
        {
            return;
        }

        Close();
        Chosen?.Invoke(id);
    }
}
