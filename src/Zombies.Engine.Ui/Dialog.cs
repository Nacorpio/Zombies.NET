using Zombies.Engine.Render;

namespace Zombies.Engine.Ui;

/// <summary>A button a dialog offers. <paramref name="IsCancel"/> marks the one chosen when the player backs out.</summary>
public sealed record DialogButton(string Id, string LabelKey, bool IsCancel = false);

/// <summary>A button as shown: its localized label and where it was laid out.</summary>
public sealed class DialogButtonView(string id, string label, bool isCancel)
{
    public string Id { get; } = id;

    public string Label { get; } = label;

    public bool IsCancel { get; } = isCancel;

    public UiRect Bounds { get; internal set; }
}

/// <summary>
/// A modal question with a title, a concise message, an optional caption line, and buttons. Text is cut to a few short lines
/// so a dialog stays readable. The mouse and the keyboard both choose a button, and the choice ends up in <see cref="Result"/>.
/// </summary>
public sealed class Dialog
{
    public const int MaxCharsPerLine = 40;
    public const int MaxMessageLines = 3;

    private const int PaddingPixels = 8;
    private const int GapPixels = 6;
    private const int ButtonPaddingPixels = 6;

    private readonly List<DialogButtonView> _buttons;

    private Dialog(string title, IReadOnlyList<string> messageLines, string? caption, List<DialogButtonView> buttons)
    {
        Title = title;
        MessageLines = messageLines;
        Caption = caption;
        _buttons = buttons;
    }

    public string Title { get; }

    public IReadOnlyList<string> MessageLines { get; }

    public string? Caption { get; }

    public IReadOnlyList<DialogButtonView> Buttons => _buttons;

    public int FocusedIndex { get; private set; }

    /// <summary>The id of the button that closed the dialog, or null while it is open.</summary>
    public string? Result { get; private set; }

    public bool IsOpen => Result is null;

    public UiRect Bounds { get; private set; }

    /// <param name="arguments">Values for <c>{0}</c> style placeholders in the title and the message, such as a mod's name.</param>
    public static Dialog Create(Localizer localizer, string titleKey, string messageKey, string? captionKey, IReadOnlyList<DialogButton> buttons, params object[] arguments)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(buttons);
        ArgumentNullException.ThrowIfNull(arguments);
        if (buttons.Count == 0)
        {
            throw new ArgumentException("A dialog needs at least one button.", nameof(buttons));
        }

        return new Dialog(
            TextWrap.Truncate(localizer.Format(titleKey, arguments), MaxCharsPerLine),
            TextWrap.Wrap(localizer.Format(messageKey, arguments), MaxCharsPerLine, MaxMessageLines),
            captionKey is null ? null : TextWrap.Truncate(localizer.Get(captionKey), MaxCharsPerLine),
            [.. buttons.Select(b => new DialogButtonView(b.Id, localizer.Get(b.LabelKey), b.IsCancel))]);
    }

    /// <summary>Lays the dialog out centered on the screen at a text scale.</summary>
    public void Arrange(UiRect screen, int textScale)
    {
        var line = DebugFont.LineHeight * textScale;
        var pad = PaddingPixels * textScale;
        var gap = GapPixels * textScale;
        var buttonHeight = line + (2 * ButtonPaddingPixels * textScale / 2);
        var buttonWidths = _buttons.Select(b => (b.Label.Length * DebugFont.Advance * textScale) + (2 * ButtonPaddingPixels * textScale)).ToList();
        var buttonRow = buttonWidths.Sum() + (gap * (_buttons.Count - 1));

        var widest = Math.Max(
            Math.Max(Title.Length, MessageLines.Count == 0 ? 0 : MessageLines.Max(l => l.Length)),
            Caption?.Length ?? 0) * DebugFont.Advance * textScale;
        var width = Math.Max(widest, buttonRow) + (2 * pad);
        var height = pad + line + gap + (MessageLines.Count * line) + (Caption is null ? 0 : gap + line) + gap + buttonHeight + pad;
        var bounds = new UiRect(screen.X + ((screen.Width - width) / 2), screen.Y + ((screen.Height - height) / 2), width, height);
        Bounds = bounds;

        var x = bounds.X + ((width - buttonRow) / 2);
        var y = bounds.Bottom - pad - buttonHeight;
        for (var i = 0; i < _buttons.Count; i++)
        {
            _buttons[i].Bounds = new UiRect(x, y, buttonWidths[i], buttonHeight);
            x += buttonWidths[i] + gap;
        }
    }

    /// <summary>Handles a mouse click. A modal dialog takes every click, so this always returns true; only a click on a button closes it.</summary>
    public bool Click(float x, float y)
    {
        if (!IsOpen)
        {
            return true;
        }

        var hit = _buttons.FindIndex(b => b.Bounds.Contains(x, y));
        if (hit >= 0)
        {
            FocusedIndex = hit;
            Result = _buttons[hit].Id;
        }

        return true;
    }

    public void MoveFocus(int delta)
    {
        if (IsOpen)
        {
            FocusedIndex = (((FocusedIndex + delta) % _buttons.Count) + _buttons.Count) % _buttons.Count;
        }
    }

    public void Activate()
    {
        if (IsOpen)
        {
            Result = _buttons[FocusedIndex].Id;
        }
    }

    /// <summary>Backs out: chooses the cancel button, or leaves the dialog open when it has none.</summary>
    public void Cancel()
    {
        if (IsOpen && _buttons.FirstOrDefault(b => b.IsCancel) is { } cancel)
        {
            Result = cancel.Id;
        }
    }
}
