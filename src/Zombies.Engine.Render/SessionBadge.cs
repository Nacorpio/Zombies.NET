using System.Globalization;
using Zombies.Engine.Core.Debugging;

namespace Zombies.Engine.Render;

public abstract record BadgeCommand;

public sealed record BadgeRect(float X, float Y, float Width, float Height, Rgba Color) : BadgeCommand;

public sealed record BadgeIcon(string Name, float X, float Y, int Scale, Rgba Tint) : BadgeCommand;

public sealed record BadgeText(string Text, float X, float Y, int Scale, Rgba Color) : BadgeCommand;

/// <summary>Where the badge sits and what it is made of, as drawing commands in the order they must be drawn.</summary>
public sealed record BadgeLayout(float X, float Y, float Width, float Height, bool Expanded, IReadOnlyList<BadgeCommand> Commands)
{
    public IEnumerable<string> Texts => Commands.OfType<BadgeText>().Select(t => t.Text);

    public IEnumerable<string> IconNames => Commands.OfType<BadgeIcon>().Select(i => i.Name);

    public bool Contains(float x, float y) => x >= X && x <= X + Width && y >= Y && y <= Y + Height;
}

/// <summary>
/// Lays out the debug session badge. It is a small pill by default (robot icon, title, and a progress bar or a step count),
/// and expands to the reason, who started it, the time, the steps, the facts, and the outcome. Anything the session does not provide
/// leaves no empty heading, bar, or list behind. Pure arithmetic: nothing here touches a GPU.
/// </summary>
public static class SessionBadge
{
    /// <summary>Pixels kept clear on the left for the frame-time overlay, so the two never touch.</summary>
    public const int OverlayReserve = 360;

    public const int MaxVisibleSteps = 7;
    public const int StepWindow = 5;
    public const int MaxReasonLines = 4;
    public const int MaxFacts = 6;
    public const int CompactTitleChars = 22;
    public const int ExpandedChars = 38;

    private static readonly Rgba Panel = new(12, 16, 24, 215);
    private static readonly Rgba Accent = new(110, 200, 255);
    private static readonly Rgba Bright = new(236, 240, 245);
    private static readonly Rgba Dim = new(150, 160, 175);
    private static readonly Rgba Good = new(120, 230, 120);
    private static readonly Rgba Bad = new(240, 100, 100);
    private static readonly Rgba Amber = new(240, 200, 80);
    private static readonly Rgba Track = new(255, 255, 255, 45);

    /// <summary>Text size by window height: 1 for small windows, 2 for most, 3 for very large ones.</summary>
    public static int ScaleFor(int screenHeight) => screenHeight < 600 ? 1 : screenHeight < 1400 ? 2 : 3;

    public static string FormatTime(double seconds)
    {
        var total = (int)Math.Max(0, Math.Floor(seconds));
        return total >= 3600
            ? string.Create(CultureInfo.InvariantCulture, $"{total / 3600}:{total / 60 % 60:00}:{total % 60:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{total / 60:00}:{total % 60:00}");
    }

    /// <summary>
    /// Builds the badge, or returns null when there is nothing to show (no session and no warning).
    /// A warning alone shows a small pill with the warning icon and the message.
    /// </summary>
    public static BadgeLayout? Layout(DebugSession? session, string? warning, double elapsedSeconds, bool expanded, int screenWidth, int screenHeight)
    {
        if (session is null && string.IsNullOrEmpty(warning))
        {
            return null;
        }

        var s = ScaleFor(screenHeight);
        var m = new Metrics(s);
        var available = Math.Max(0, screenWidth - OverlayReserve - (2 * m.Margin));

        var title = session?.Title ?? warning!;
        var headerIcon = session is null ? IconNames.Warning : IconNames.Robot;
        var headerIconTint = session is null ? Amber : Accent;
        var progress = session?.ProgressAt(elapsedSeconds);
        var summary = session?.Summary;

        // What sits at the right end of the header: a progress bar when there is progress, otherwise a step count, otherwise nothing.
        var count = progress is null && summary is not null ? string.Create(CultureInfo.InvariantCulture, $"{summary.Current}/{summary.Total}") : null;
        var rightWidth = progress is not null ? m.BarWidth : count is not null ? count.Length * m.CharWidth : 0;
        var expandedAllowed = expanded && session is not null;

        var chars = expandedAllowed ? Math.Min(ExpandedChars, Math.Max(10, (available - (2 * m.Pad)) / m.CharWidth)) : CompactTitleChars;
        var fixedWidth = (2 * m.Pad) + m.IconPixels + m.Pad + (rightWidth > 0 ? m.Pad + rightWidth : 0);
        var titleChars = Math.Min(title.Length, chars);

        float width;
        if (expandedAllowed)
        {
            width = Math.Min(available, (2 * m.Pad) + (chars * m.CharWidth));
            width = Math.Max(width, Math.Min(available, fixedWidth + (Math.Min(title.Length, CompactTitleChars) * m.CharWidth)));
            titleChars = Math.Max(0, Math.Min(title.Length, (int)((width - fixedWidth) / m.CharWidth)));
        }
        else
        {
            width = fixedWidth + (titleChars * m.CharWidth);
            if (width > available)
            {
                titleChars = Math.Max(0, (int)((available - fixedWidth) / m.CharWidth));
                width = fixedWidth + (titleChars * m.CharWidth);
            }

            if (width > available)
            {
                // Not even room for the right element: keep only the icon.
                titleChars = 0;
                rightWidth = 0;
                count = null;
                progress = null;
                width = (2 * m.Pad) + m.IconPixels;
            }
        }

        var x = screenWidth - width - m.Margin;
        var y = (float)m.Margin;
        var commands = new List<BadgeCommand>();
        var rowTop = y + m.Pad;
        var cursor = rowTop + m.RowHeight;

        // The background is added last into position zero once the height is known.
        commands.Add(new BadgeIcon(headerIcon, x + m.Pad, rowTop + ((m.RowHeight - m.IconPixels) / 2f), m.IconScale, headerIconTint));
        if (titleChars > 0)
        {
            commands.Add(new BadgeText(TextWrap.Truncate(title, titleChars).ToUpperInvariant(), x + m.Pad + m.IconPixels + m.Pad, rowTop + ((m.RowHeight - m.LineHeight) / 2f), s, Bright));
        }

        var rightX = x + width - m.Pad - rightWidth;
        if (progress is { } fraction && rightWidth > 0)
        {
            var barY = rowTop + ((m.RowHeight - m.BarHeight) / 2f);
            commands.Add(new BadgeRect(rightX, barY, m.BarWidth, m.BarHeight, Track));
            if (fraction > 0)
            {
                commands.Add(new BadgeRect(rightX, barY, m.BarWidth * (float)fraction, m.BarHeight, Accent));
            }
        }
        else if (count is not null && rightWidth > 0)
        {
            commands.Add(new BadgeText(count, rightX, rowTop + ((m.RowHeight - m.LineHeight) / 2f), s, Dim));
        }

        if (expandedAllowed)
        {
            cursor = AddDetails(commands, session!, warning, elapsedSeconds, x, width, cursor, m);
        }
        else if (session is not null && !string.IsNullOrEmpty(warning))
        {
            // A compact badge with a problem still says so, with a small warning mark beside the title.
            commands.Add(new BadgeIcon(IconNames.Warning, x + width - m.Pad - m.IconPixels, y - (m.IconPixels / 2f), m.IconScale, Amber));
        }

        var height = cursor + m.Pad - y;
        commands.Insert(0, new BadgeRect(x, y, width, height, Panel));
        commands.Insert(1, new BadgeRect(x, y, Math.Max(1, m.Scale), height, headerIconTint));
        return new BadgeLayout(x, y, width, height, expandedAllowed, commands);
    }

    /// <summary>Adds the expanded sections below the header and returns where the content ends.</summary>
    private static float AddDetails(List<BadgeCommand> commands, DebugSession session, string? warning, double elapsedSeconds, float x, float width, float cursor, Metrics m)
    {
        var left = x + m.Pad;
        var textChars = Math.Max(4, (int)((width - (2 * m.Pad)) / m.CharWidth));
        var s = m.Scale;

        void Divider()
        {
            cursor += m.Pad / 2f;
            commands.Add(new BadgeRect(x + m.Pad, cursor, width - (2 * m.Pad), Math.Max(1, s), Track));
            cursor += m.Pad;
        }

        void Line(string text, Rgba color)
        {
            commands.Add(new BadgeText(TextWrap.Truncate(text, textChars).ToUpperInvariant(), left, cursor, s, color));
            cursor += m.LineHeight;
        }

        if (!string.IsNullOrWhiteSpace(session.Reason))
        {
            Divider();
            foreach (var line in TextWrap.Wrap(session.Reason, textChars, MaxReasonLines))
            {
                Line(line, Bright);
            }
        }

        Divider();
        if (!string.IsNullOrWhiteSpace(session.StartedBy))
        {
            Line("BY " + session.StartedBy, Dim);
        }

        var elapsed = FormatTime(elapsedSeconds);
        Line(session.DurationSeconds is { } duration ? $"TIME {elapsed} OF {FormatTime(duration)}" : $"TIME {elapsed}", Dim);

        if (session.Steps.Count > 0)
        {
            Divider();
            cursor = AddSteps(commands, session, left, textChars, cursor, m);
        }

        if (session.Facts.Count > 0)
        {
            Divider();
            foreach (var fact in session.Facts.Take(MaxFacts))
            {
                var label = fact.Label.ToUpperInvariant() + ": ";
                var value = TextWrap.Truncate(fact.Value.ToUpperInvariant(), Math.Max(0, textChars - label.Length));
                commands.Add(new BadgeText(TextWrap.Truncate(label, textChars), left, cursor, s, Dim));
                commands.Add(new BadgeText(value, left + (label.Length * m.CharWidth), cursor, s, Bright));
                cursor += m.LineHeight;
            }

            if (session.Facts.Count > MaxFacts)
            {
                Line($"+{session.Facts.Count - MaxFacts} MORE", Dim);
            }
        }

        if (!string.IsNullOrWhiteSpace(session.Outcome))
        {
            Divider();
            foreach (var line in TextWrap.Wrap(session.Outcome, textChars, 2))
            {
                Line(line, Accent);
            }
        }

        if (!string.IsNullOrEmpty(warning))
        {
            Divider();
            commands.Add(new BadgeIcon(IconNames.Warning, left, cursor, m.IconScale, Amber));
            var warningChars = Math.Max(1, (int)((width - (3 * m.Pad) - m.IconPixels) / m.CharWidth));
            commands.Add(new BadgeText(TextWrap.Truncate(warning, warningChars), left + m.IconPixels + m.Pad, cursor + ((m.IconPixels - m.LineHeight) / 2f), s, Amber));
            cursor += Math.Max(m.IconPixels, m.LineHeight);
        }

        return cursor;
    }

    private static float AddSteps(List<BadgeCommand> commands, DebugSession session, float left, int textChars, float cursor, Metrics m)
    {
        var steps = session.Steps;
        var start = 0;
        var end = steps.Count;
        if (steps.Count > MaxVisibleSteps)
        {
            start = Math.Clamp(session.CurrentStepIndex - (StepWindow / 2), 0, steps.Count - StepWindow);
            end = start + StepWindow;
        }

        float StepRow(string text, Rgba color, string? icon, string? mark, Rgba markColor)
        {
            var markX = left;
            var textX = left + m.IconPixels + (m.Pad / 2f);
            var rowHeight = Math.Max(m.IconPixels, m.LineHeight);
            var textY = cursor + ((rowHeight - m.LineHeight) / 2f);
            if (icon is not null)
            {
                commands.Add(new BadgeIcon(icon, markX, cursor + ((rowHeight - m.IconPixels) / 2f), m.IconScale, markColor));
            }
            else if (mark is not null)
            {
                // A solid square for the running step, a small dash for a pending one. The font has no arrow to spare.
                var size = mark == ">" ? 3 * m.Scale : 2 * m.Scale;
                var wide = mark == ">" ? size : 4 * m.Scale;
                commands.Add(new BadgeRect(markX + ((m.IconPixels - wide) / 2f), cursor + ((rowHeight - size) / 2f), wide, size, markColor));
            }

            var chars = Math.Max(1, textChars - (int)Math.Ceiling((m.IconPixels + (m.Pad / 2f)) / m.CharWidth));
            commands.Add(new BadgeText(TextWrap.Truncate(text, chars).ToUpperInvariant(), textX, textY, m.Scale, color));
            return rowHeight;
        }

        if (start > 0)
        {
            cursor += StepRow($"+{start} EARLIER", Dim, null, null, Dim);
        }

        for (var i = start; i < end; i++)
        {
            var step = steps[i];
            cursor += step.State switch
            {
                StepState.Done => StepRow(step.Title, Dim, IconNames.Check, null, Good),
                StepState.Failed => StepRow(step.Title, Bad, IconNames.Cross, null, Bad),
                StepState.Running => StepRow(step.Title, Bright, null, ">", Amber),
                _ => StepRow(step.Title, Dim, null, "-", Dim),
            };
        }

        if (end < steps.Count)
        {
            cursor += StepRow($"+{steps.Count - end} MORE", Dim, null, null, Dim);
        }

        return cursor;
    }

    /// <summary>Draws a layout into a sprite batch.</summary>
    public static void Draw(SpriteBatch batch, BadgeLayout layout)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(layout);
        foreach (var command in layout.Commands)
        {
            switch (command)
            {
                case BadgeRect rect:
                    batch.FillRect(rect.X, rect.Y, rect.Width, rect.Height, rect.Color);
                    break;
                case BadgeIcon icon:
                    batch.DrawIcon(icon.Name, icon.X, icon.Y, icon.Scale, icon.Tint);
                    break;
                case BadgeText text:
                    batch.DrawText(text.Text, text.X, text.Y, text.Scale, text.Color);
                    break;
                default:
                    break;
            }
        }
    }

    private readonly record struct Metrics(int Scale)
    {
        public int CharWidth => DebugFont.Advance * Scale;

        public int LineHeight => (DebugFont.LineHeight + 2) * Scale;

        public int Pad => 4 * Scale;

        public int Margin => 6 * Scale;

        public int IconScale => Math.Max(1, (Scale + 1) / 2);

        public int IconPixels => Icons.Size * IconScale;

        public int RowHeight => Math.Max(IconPixels, LineHeight);

        public int BarWidth => 8 * CharWidth;

        public int BarHeight => Math.Max(2, 2 * Scale);
    }
}
