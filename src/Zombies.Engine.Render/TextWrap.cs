namespace Zombies.Engine.Render;

/// <summary>Fits text into a fixed number of characters, which is all a monospaced bitmap font needs.</summary>
public static class TextWrap
{
    private const string Ellipsis = "...";

    /// <summary>Cuts text to <paramref name="maxChars"/>, ending in three dots when something was cut.</summary>
    public static string Truncate(string text, int maxChars)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentOutOfRangeException.ThrowIfNegative(maxChars);
        if (text.Length <= maxChars)
        {
            return text;
        }

        return maxChars <= Ellipsis.Length ? text[..maxChars] : string.Concat(text.AsSpan(0, maxChars - Ellipsis.Length), Ellipsis);
    }

    /// <summary>
    /// Breaks text into lines of at most <paramref name="maxChars"/> at word boundaries, splitting a word only when it alone is too long.
    /// If more than <paramref name="maxLines"/> lines are needed, the last kept line ends in three dots.
    /// </summary>
    public static IReadOnlyList<string> Wrap(string text, int maxChars, int maxLines)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxChars, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLines, 1);

        var lines = new List<string>();
        var current = string.Empty;
        foreach (var word in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var remaining = word;
            while (remaining.Length > maxChars)
            {
                if (current.Length > 0)
                {
                    lines.Add(current);
                    current = string.Empty;
                }

                lines.Add(remaining[..maxChars]);
                remaining = remaining[maxChars..];
            }

            if (current.Length == 0)
            {
                current = remaining;
            }
            else if (current.Length + 1 + remaining.Length <= maxChars)
            {
                current = string.Concat(current, " ", remaining);
            }
            else
            {
                lines.Add(current);
                current = remaining;
            }
        }

        if (current.Length > 0)
        {
            lines.Add(current);
        }

        if (lines.Count <= maxLines)
        {
            return lines;
        }

        var kept = lines.Take(maxLines).ToList();
        var last = kept[^1];
        kept[^1] = last.Length + Ellipsis.Length <= maxChars ? last + Ellipsis : Truncate(last, maxChars - Ellipsis.Length) + Ellipsis;
        return kept;
    }
}
