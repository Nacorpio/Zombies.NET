using Zombies.Engine.Render;

namespace Zombies.Engine.Ui;

/// <summary>
/// Draws an arranged <see cref="UiLayout"/> through the sprite batch. It reads the layout for where things go and a
/// <see cref="UiValues"/> for what they show, so the same screen can be drawn every frame without rebuilding it.
/// </summary>
public static class UiRenderer
{
    private const int BarPaddingPixels = 1;

    public static void Draw(SpriteBatch sprites, UiLayout layout, UiValues values, UiContext context, UiPalette palette)
    {
        ArgumentNullException.ThrowIfNull(sprites);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(palette);
        DrawWidget(sprites, layout.Root, values, context, palette);
    }

    /// <summary>Draws a tooltip at the pointer, if there is one there.</summary>
    public static void DrawTooltip(SpriteBatch sprites, UiLayout layout, Localizer localizer, UiPalette palette, float x, float y, UiRect screen, int textScale)
    {
        ArgumentNullException.ThrowIfNull(sprites);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(palette);
        if (layout.TooltipAt(x, y, localizer) is not { } tooltip)
        {
            return;
        }

        DrawTooltip(sprites, tooltip, palette, x, y, screen, textScale);
    }

    /// <summary>Draws a tooltip beside the pointer: title, body lines, then the caption if it has one.</summary>
    public static void DrawTooltip(SpriteBatch sprites, Tooltip tooltip, UiPalette palette, float x, float y, UiRect screen, int textScale)
    {
        ArgumentNullException.ThrowIfNull(sprites);
        ArgumentNullException.ThrowIfNull(tooltip);
        ArgumentNullException.ThrowIfNull(palette);
        var bounds = tooltip.Place(x, y, screen, textScale);
        sprites.FillRect(bounds.X, bounds.Y, bounds.Width, bounds.Height, palette.Color(PaletteRole.Panel));
        var line = DebugFont.LineHeight * textScale;
        var textX = bounds.X + (4 * textScale);
        var textY = bounds.Y + (4 * textScale);
        if (tooltip.Title is { } title)
        {
            sprites.DrawText(title, textX, textY, textScale, palette.Color(PaletteRole.Text));
            textY += line;
        }

        foreach (var text in tooltip.Lines)
        {
            sprites.DrawText(text, textX, textY, textScale, palette.Color(PaletteRole.Muted));
            textY += line;
        }

        if (tooltip.Caption is { } caption)
        {
            sprites.DrawText(caption, textX, textY, textScale, palette.Color(PaletteRole.Text));
        }
    }

    /// <summary>Draws a dialog centered on the screen, with its focused button marked.</summary>
    public static void DrawDialog(SpriteBatch sprites, Dialog dialog, UiPalette palette, int textScale)
    {
        ArgumentNullException.ThrowIfNull(sprites);
        ArgumentNullException.ThrowIfNull(dialog);
        ArgumentNullException.ThrowIfNull(palette);
        var bounds = dialog.Bounds;
        var line = DebugFont.LineHeight * textScale;
        var pad = 8 * textScale;

        sprites.FillRect(bounds.X, bounds.Y, bounds.Width, bounds.Height, palette.Color(PaletteRole.Panel));
        var x = bounds.X + pad;
        var y = bounds.Y + pad;
        sprites.DrawText(dialog.Title, x, y, textScale, palette.Color(PaletteRole.Text));
        y += line + (6 * textScale);
        foreach (var text in dialog.MessageLines)
        {
            sprites.DrawText(text, x, y, textScale, palette.Color(PaletteRole.Muted));
            y += line;
        }

        if (dialog.Caption is { } caption)
        {
            y += 6 * textScale;
            sprites.DrawText(caption, x, y, textScale, palette.Color(PaletteRole.Info));
        }

        for (var i = 0; i < dialog.Buttons.Count; i++)
        {
            var button = dialog.Buttons[i];
            var focused = i == dialog.FocusedIndex;
            sprites.FillRect(button.Bounds.X, button.Bounds.Y, button.Bounds.Width, button.Bounds.Height, palette.Color(focused ? PaletteRole.Info : PaletteRole.Muted));
            sprites.DrawText(button.Label, button.Bounds.X + (6 * textScale), button.Bounds.Y + (3 * textScale), textScale, palette.Color(PaletteRole.Text));
        }
    }

    private static void DrawWidget(SpriteBatch sprites, Widget widget, UiValues values, UiContext context, UiPalette palette)
    {
        if (widget.Id is { } id && !values.IsVisible(id))
        {
            return;
        }

        var role = widget.Id is { } roleId ? values.RoleOf(roleId) ?? widget.Role : widget.Role;
        var bounds = widget.Bounds;
        switch (widget.Kind)
        {
            case WidgetKind.Panel:
                if (role != PaletteRole.None)
                {
                    sprites.FillRect(bounds.X, bounds.Y, bounds.Width, bounds.Height, palette.Color(PaletteRole.Panel));
                }

                break;

            case WidgetKind.Bar:
                DrawBar(sprites, widget, values, palette, role);
                break;

            case WidgetKind.Label:
                DrawLabel(sprites, widget, values, context, palette, role);
                break;        }

        foreach (var child in widget.Children)
        {
            DrawWidget(sprites, child, values, context, palette);
        }
    }

    private static void DrawBar(SpriteBatch sprites, Widget widget, UiValues values, UiPalette palette, PaletteRole role)
    {
        var bounds = widget.Bounds;
        var pad = BarPaddingPixels * (bounds.Height > 8 ? 2 : 1);
        sprites.FillRect(bounds.X, bounds.Y, bounds.Width, bounds.Height, palette.Color(PaletteRole.Muted));
        var fraction = widget.Id is { } id ? values.FractionOf(id) : 0f;
        var filled = Math.Max(0, (bounds.Width - (2 * pad)) * fraction);
        if (filled > 0)
        {
            sprites.FillRect(bounds.X + pad, bounds.Y + pad, filled, Math.Max(1, bounds.Height - (2 * pad)), palette.Color(role));
        }
    }

    private static void DrawLabel(SpriteBatch sprites, Widget widget, UiValues values, UiContext context, UiPalette palette, PaletteRole role)
    {
        var text = widget.Id is { } id ? values.TextOf(id) : null;
        text ??= widget.TextKey is { } key ? context.Localizer.Get(key) : null;
        if (text is { Length: > 0 })
        {
            sprites.DrawText(text, widget.Bounds.X, widget.Bounds.Y, context.TextScale, palette.Color(role));
        }
    }
}
