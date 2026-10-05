using Zombies.Engine.Render;

namespace Zombies.Engine.Ui;

/// <summary>A short block of text shown beside the pointer. It is kept to a few short lines so it can be read at a glance.</summary>
public sealed class Tooltip
{
    public const int MaxCharsPerLine = 32;
    public const int MaxLines = 4;

    private const int PaddingPixels = 4;
    private const int PointerOffsetPixels = 12;

    private Tooltip(string? title, IReadOnlyList<string> lines)
    {
        Title = title;
        Lines = lines;
    }

    public string? Title { get; }

    public IReadOnlyList<string> Lines { get; }

    public static Tooltip Create(string? title, string body)
    {
        ArgumentNullException.ThrowIfNull(body);
        return new Tooltip(title is null ? null : TextWrap.Truncate(title, MaxCharsPerLine), TextWrap.Wrap(body, MaxCharsPerLine, MaxLines));
    }

    /// <summary>Where to draw the tooltip for a pointer: just down and right of it, flipped to the other side when that would leave the screen, and never outside it.</summary>
    public UiRect Place(float pointerX, float pointerY, UiRect screen, int textScale)
    {
        var chars = Math.Max(Title?.Length ?? 0, Lines.Count == 0 ? 0 : Lines.Max(l => l.Length));
        var rows = Lines.Count + (Title is null ? 0 : 1);
        var width = (chars * DebugFont.Advance * textScale) + (2 * PaddingPixels * textScale);
        var height = (rows * DebugFont.LineHeight * textScale) + (2 * PaddingPixels * textScale);
        var offset = PointerOffsetPixels * textScale;

        var x = pointerX + offset;
        var y = pointerY + offset;
        if (x + width > screen.Right)
        {
            x = pointerX - offset - width;
        }

        if (y + height > screen.Bottom)
        {
            y = pointerY - offset - height;
        }

        return new UiRect(Math.Clamp(x, screen.X, Math.Max(screen.X, screen.Right - width)), Math.Clamp(y, screen.Y, Math.Max(screen.Y, screen.Bottom - height)), width, height);
    }
}
