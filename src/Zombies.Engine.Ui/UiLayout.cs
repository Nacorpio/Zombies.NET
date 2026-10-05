using System.Text.Json;
using Zombies.Engine.Render;

namespace Zombies.Engine.Ui;

/// <summary>What a widget is. Panels and bars are boxes, labels draw text, and rows and columns stack their children.</summary>
public enum WidgetKind
{
    Panel,
    Label,
    Bar,
    Row,
    Column,
}

/// <summary>Where a widget sits inside its parent. The offset is measured inward from the anchored edge, so a widget stays on screen when the parent grows.</summary>
public enum Anchor
{
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight,
    Center,
    Fill,
}

/// <summary>One node of a layout. Bounds are filled in by <see cref="UiLayout.Arrange"/>.</summary>
public sealed class Widget
{
    private readonly List<Widget> _children;

    internal Widget(
        string? id,
        WidgetKind kind,
        Anchor anchor,
        float x,
        float y,
        float? width,
        float? height,
        PaletteRole role,
        string? textKey,
        string? tooltipKey,
        float gap,
        List<Widget> children)
    {
        Id = id;
        Kind = kind;
        Anchor = anchor;
        X = x;
        Y = y;
        Width = width;
        Height = height;
        Role = role;
        TextKey = textKey;
        TooltipKey = tooltipKey;
        Gap = gap;
        _children = children;
        foreach (var child in children)
        {
            child.Parent = this;
        }
    }

    /// <summary>The name screens use to set this widget's values, or null when the widget is only there to hold others.</summary>
    public string? Id { get; }

    public WidgetKind Kind { get; }

    public Anchor Anchor { get; }

    public PaletteRole Role { get; }

    /// <summary>The string table key a label draws, or null.</summary>
    public string? TextKey { get; }

    /// <summary>The string table key shown when the pointer rests on this widget, or null.</summary>
    public string? TooltipKey { get; }

    public IReadOnlyList<Widget> Children => _children;

    public Widget? Parent { get; private set; }

    /// <summary>Where the widget ended up, in screen pixels. Only meaningful after <see cref="UiLayout.Arrange"/>.</summary>
    public UiRect Bounds { get; internal set; }

    internal float X { get; }

    internal float Y { get; }

    internal float? Width { get; }

    internal float? Height { get; }

    internal float Gap { get; }
}

/// <summary>Everything arranging a layout needs besides the screen: the language to measure text in, the UI scale, and the text scale.</summary>
public sealed record UiContext(Localizer Localizer, float Scale = 1f, int TextScale = 1);

/// <summary>
/// A screen described as data, so a mod can add or rearrange one without code. The tree is parsed once and arranged every
/// time the screen size or scale changes; what it shows right now comes from a separate <see cref="UiValues"/>.
/// </summary>
public sealed class UiLayout
{
    /// <summary>How deep a tree may nest. A layout is data from a mod, so a runaway one is refused rather than allowed to exhaust the stack.</summary>
    public const int MaxDepth = 32;

    private UiLayout(string id, Widget root)
    {
        Id = id;
        Root = root;
    }

    public string Id { get; }

    public Widget Root { get; }

    public static bool TryParse(string json, out UiLayout layout, out string error)
    {
        layout = null!;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(json))
        {
            error = "The layout is empty.";
            return false;
        }

        JsonDocument document;
        try
        {
            // The reader's own limit is set above ours so that a too-deep tree is reported by our check, which names the path.
            document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
                MaxDepth = (MaxDepth * 2) + 4,
            });
        }
        catch (JsonException ex)
        {
            error = ex.Message.Contains("depth", StringComparison.OrdinalIgnoreCase)
                ? $"The layout is nested too deep; a layout may not go deeper than {MaxDepth} levels."
                : $"Not valid JSON: {ex.Message}";
            return false;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "The layout must be a JSON object.";
                return false;
            }

            if (!root.TryGetProperty("id", out var idElement) || idElement.ValueKind != JsonValueKind.String || idElement.GetString() is not { Length: > 0 } id)
            {
                error = "The layout needs a non-empty 'id'.";
                return false;
            }

            if (!root.TryGetProperty("root", out var rootElement) || rootElement.ValueKind != JsonValueKind.Object)
            {
                error = "The layout needs a 'root' widget.";
                return false;
            }

            var ids = new HashSet<string>(StringComparer.Ordinal);
            if (!TryWidget(rootElement, "root", 0, ids, out var widget, out error))
            {
                return false;
            }

            layout = new UiLayout(id, widget);
            return true;
        }
    }

    /// <summary>Finds a widget by id anywhere in the tree, or null.</summary>
    public Widget? Find(string id) => Find(Root, id);

    /// <summary>Every string table key the layout needs, so a screen can check its language covers them.</summary>
    public IReadOnlyList<string> Keys()
    {
        var keys = new SortedSet<string>(StringComparer.Ordinal);
        CollectKeys(Root, keys);
        return [.. keys];
    }

    /// <summary>Places every widget against the screen. Call again whenever the screen size or the scale changes.</summary>
    public void Arrange(UiRect screen, UiContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArrangeWidget(Root, screen, context);
    }

    /// <summary>The deepest widget under a point, or null when the point is outside everything. Overlapping siblings resolve to the one drawn last.</summary>
    public Widget? HitTest(float x, float y) => HitTest(Root, x, y);

    /// <summary>The tooltip for whatever is under a point: the widget's own key, or the nearest ancestor that has one.</summary>
    public Tooltip? TooltipAt(float x, float y, Localizer localizer)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        for (var widget = HitTest(x, y); widget is not null; widget = widget.Parent)
        {
            if (widget.TooltipKey is { } key)
            {
                return Tooltip.Create(null, localizer.Get(key));
            }
        }

        return null;
    }

    private static Widget? Find(Widget widget, string id)
    {
        if (string.Equals(widget.Id, id, StringComparison.Ordinal))
        {
            return widget;
        }

        foreach (var child in widget.Children)
        {
            if (Find(child, id) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private static void CollectKeys(Widget widget, SortedSet<string> keys)
    {
        if (widget.TextKey is { } text)
        {
            keys.Add(text);
        }

        if (widget.TooltipKey is { } tooltip)
        {
            keys.Add(tooltip);
        }

        foreach (var child in widget.Children)
        {
            CollectKeys(child, keys);
        }
    }

    private static Widget? HitTest(Widget widget, float x, float y)
    {
        for (var i = widget.Children.Count - 1; i >= 0; i--)
        {
            if (HitTest(widget.Children[i], x, y) is { } hit)
            {
                return hit;
            }
        }

        return widget.Bounds.Contains(x, y) ? widget : null;
    }

    private static void ArrangeWidget(Widget widget, UiRect parent, UiContext context)
    {
        var (width, height) = Measure(widget, context);
        widget.Bounds = Place(widget, parent, width, height, context);
        ArrangeChildren(widget, context);
    }

    private static void ArrangeChildren(Widget widget, UiContext context)
    {
        if (widget.Kind is WidgetKind.Row or WidgetKind.Column)
        {
            // A row or a column places its children itself, so a child's own anchor is ignored here.
            var horizontal = widget.Kind == WidgetKind.Row;
            var cursor = horizontal ? widget.Bounds.X : widget.Bounds.Y;
            foreach (var child in widget.Children)
            {
                var (childWidth, childHeight) = Measure(child, context);
                child.Bounds = horizontal
                    ? new UiRect(cursor, widget.Bounds.Y, childWidth, childHeight)
                    : new UiRect(widget.Bounds.X, cursor, childWidth, childHeight);
                ArrangeChildren(child, context);
                cursor += (horizontal ? childWidth : childHeight) + (widget.Gap * context.Scale);
            }

            return;
        }

        foreach (var child in widget.Children)
        {
            ArrangeWidget(child, widget.Bounds, context);
        }
    }

    private static (float Width, float Height) Measure(Widget widget, UiContext context)
    {
        var fixedWidth = widget.Width is { } w ? w * context.Scale : (float?)null;
        var fixedHeight = widget.Height is { } h ? h * context.Scale : (float?)null;
        switch (widget.Kind)
        {
            case WidgetKind.Row:
            {
                var width = 0f;
                var height = 0f;
                for (var i = 0; i < widget.Children.Count; i++)
                {
                    var (childWidth, childHeight) = Measure(widget.Children[i], context);
                    width += childWidth + (i == 0 ? 0 : widget.Gap * context.Scale);
                    height = Math.Max(height, childHeight);
                }

                return (fixedWidth ?? width, fixedHeight ?? height);
            }

            case WidgetKind.Column:
            {
                var width = 0f;
                var height = 0f;
                for (var i = 0; i < widget.Children.Count; i++)
                {
                    var (childWidth, childHeight) = Measure(widget.Children[i], context);
                    height += childHeight + (i == 0 ? 0 : widget.Gap * context.Scale);
                    width = Math.Max(width, childWidth);
                }

                return (fixedWidth ?? width, fixedHeight ?? height);
            }

            case WidgetKind.Label:
            {
                var text = widget.TextKey is null ? string.Empty : context.Localizer.Get(widget.TextKey);
                return (fixedWidth ?? DebugFont.MeasureWidth(text, context.TextScale), fixedHeight ?? DebugFont.LineHeight * context.TextScale);
            }

            default:
                return (fixedWidth ?? 0f, fixedHeight ?? 0f);
        }
    }

    private static UiRect Place(Widget widget, UiRect parent, float width, float height, UiContext context)
    {
        var x = widget.X * context.Scale;
        var y = widget.Y * context.Scale;
        return widget.Anchor switch
        {
            Anchor.TopRight => new UiRect(parent.Right - x - width, parent.Y + y, width, height),
            Anchor.BottomLeft => new UiRect(parent.X + x, parent.Bottom - y - height, width, height),
            Anchor.BottomRight => new UiRect(parent.Right - x - width, parent.Bottom - y - height, width, height),
            Anchor.Center => new UiRect(parent.X + ((parent.Width - width) / 2), parent.Y + ((parent.Height - height) / 2), width, height),
            Anchor.Fill => new UiRect(parent.X + x, parent.Y + y, parent.Width - (2 * x), parent.Height - (2 * y)),
            _ => new UiRect(parent.X + x, parent.Y + y, width, height),
        };
    }

    private static bool TryWidget(JsonElement element, string path, int depth, HashSet<string> ids, out Widget widget, out string error)
    {
        widget = null!;
        error = string.Empty;
        if (depth > MaxDepth)
        {
            error = $"'{path}' is nested too deep; a layout may not go deeper than {MaxDepth} levels.";
            return false;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            error = $"'{path}' must be an object.";
            return false;
        }

        if (!element.TryGetProperty("type", out var typeElement) || typeElement.ValueKind != JsonValueKind.String)
        {
            error = $"'{path}' needs a 'type'.";
            return false;
        }

        var typeName = typeElement.GetString()!;
        if (!TryKind(typeName, out var kind))
        {
            error = $"'{path}.type' is '{typeName}', which is not a widget type.";
            return false;
        }

        string? id = null;
        if (element.TryGetProperty("id", out var idElement))
        {
            if (idElement.ValueKind != JsonValueKind.String || idElement.GetString() is not { Length: > 0 } idValue)
            {
                error = $"'{path}.id' must be a non-empty string.";
                return false;
            }

            if (!ids.Add(idValue))
            {
                error = $"'{path}.id' repeats the id '{idValue}'.";
                return false;
            }

            id = idValue;
        }

        var anchor = Anchor.TopLeft;
        if (element.TryGetProperty("anchor", out var anchorElement))
        {
            if (anchorElement.ValueKind != JsonValueKind.String || !TryAnchor(anchorElement.GetString()!, out anchor))
            {
                error = $"'{path}.anchor' is '{anchorElement}', which is not an anchor.";
                return false;
            }
        }

        var role = PaletteRole.Text;
        if (element.TryGetProperty("role", out var roleElement))
        {
            if (roleElement.ValueKind != JsonValueKind.String || !TryRole(roleElement.GetString()!, out role))
            {
                error = $"'{path}.role' is '{roleElement}', which is not a color role.";
                return false;
            }
        }

        if (!TryOffset(element, "x", path, out var x, out error)
            || !TryOffset(element, "y", path, out var y, out error)
            || !TrySize(element, "width", path, out var width, out error)
            || !TrySize(element, "height", path, out var height, out error)
            || !TryOffset(element, "gap", path, out var gap, out error))
        {
            return false;
        }

        if (!TryKey(element, "textKey", path, out var textKey, out error) || !TryKey(element, "tooltipKey", path, out var tooltipKey, out error))
        {
            return false;
        }

        var children = new List<Widget>();
        if (element.TryGetProperty("children", out var childrenElement))
        {
            if (childrenElement.ValueKind != JsonValueKind.Array)
            {
                error = $"'{path}.children' must be an array.";
                return false;
            }

            var index = 0;
            foreach (var childElement in childrenElement.EnumerateArray())
            {
                if (!TryWidget(childElement, $"{path}.children[{index}]", depth + 1, ids, out var child, out error))
                {
                    return false;
                }

                children.Add(child);
                index++;
            }
        }

        widget = new Widget(id, kind, anchor, x, y, width, height, role, textKey, tooltipKey, gap, children);
        return true;
    }

    private static bool TryKind(string name, out WidgetKind kind)
    {
        switch (name)
        {
            case "panel": kind = WidgetKind.Panel; return true;
            case "label": kind = WidgetKind.Label; return true;
            case "bar": kind = WidgetKind.Bar; return true;
            case "row": kind = WidgetKind.Row; return true;
            case "column": kind = WidgetKind.Column; return true;
            default: kind = default; return false;
        }
    }

    private static bool TryAnchor(string name, out Anchor anchor)
    {
        switch (name)
        {
            case "topLeft": anchor = Anchor.TopLeft; return true;
            case "topRight": anchor = Anchor.TopRight; return true;
            case "bottomLeft": anchor = Anchor.BottomLeft; return true;
            case "bottomRight": anchor = Anchor.BottomRight; return true;
            case "center": anchor = Anchor.Center; return true;
            case "fill": anchor = Anchor.Fill; return true;
            default: anchor = default; return false;
        }
    }

    private static bool TryRole(string name, out PaletteRole role)
    {
        foreach (var candidate in Enum.GetValues<PaletteRole>())
        {
            if (string.Equals(candidate.ToString(), name, StringComparison.OrdinalIgnoreCase))
            {
                role = candidate;
                return true;
            }
        }

        role = default;
        return false;
    }

    private static bool TryOffset(JsonElement element, string name, string path, out float value, out string error)
    {
        value = 0f;
        error = string.Empty;
        if (!element.TryGetProperty(name, out var property))
        {
            return true;
        }

        if (property.ValueKind != JsonValueKind.Number || !property.TryGetSingle(out value) || !float.IsFinite(value))
        {
            error = $"'{path}.{name}' must be a number.";
            return false;
        }

        return true;
    }

    private static bool TrySize(JsonElement element, string name, string path, out float? value, out string error)
    {
        value = null;
        error = string.Empty;
        if (!element.TryGetProperty(name, out var property))
        {
            return true;
        }

        if (property.ValueKind != JsonValueKind.Number || !property.TryGetSingle(out var number) || !float.IsFinite(number))
        {
            error = $"'{path}.{name}' must be a number.";
            return false;
        }

        if (number < 0)
        {
            error = $"'{path}.{name}' cannot be negative.";
            return false;
        }

        value = number;
        return true;
    }

    private static bool TryKey(JsonElement element, string name, string path, out string? value, out string error)
    {
        value = null;
        error = string.Empty;
        if (!element.TryGetProperty(name, out var property))
        {
            return true;
        }

        if (property.ValueKind != JsonValueKind.String || property.GetString() is not { Length: > 0 } text)
        {
            error = $"'{path}.{name}' must be a non-empty string.";
            return false;
        }

        value = text;
        return true;
    }
}
