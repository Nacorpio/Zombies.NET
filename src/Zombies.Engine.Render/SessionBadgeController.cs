using Zombies.Engine.Core.Debugging;

namespace Zombies.Engine.Render;

/// <summary>
/// Decides when the badge is compact and when it is open. It opens by itself for a few seconds when a session appears and whenever a step
/// changes or the outcome arrives, so the human notices what happened, then folds back to stay out of the way. Hovering the badge opens it,
/// and the toggle key pins it open or shut.
/// </summary>
public sealed class SessionBadgeController
{
    /// <summary>How long the badge stays open after something worth noticing.</summary>
    public const double AutoOpenSeconds = 4;

    private string? _signature;
    private double _openUntil = double.NegativeInfinity;
    private bool _pinned;
    private BadgeLayout? _last;

    public bool Pinned => _pinned;

    public BadgeLayout? Last => _last;

    /// <summary>Advances one frame and returns what to draw, or null when the badge has nothing to say.</summary>
    public BadgeLayout? Update(SessionState state, double nowSeconds, DateTimeOffset utcNow, float mouseX, float mouseY, bool togglePressed, int screenWidth, int screenHeight)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (togglePressed)
        {
            _pinned = !_pinned;
        }

        var signature = state.Session?.Signature;
        if (signature is not null && signature != _signature)
        {
            _openUntil = nowSeconds + AutoOpenSeconds;
        }

        _signature = signature;

        var hovered = _last is not null && _last.Contains(mouseX, mouseY);
        var expanded = _pinned || hovered || nowSeconds < _openUntil;
        var elapsed = state.ElapsedSeconds(nowSeconds, utcNow);
        return _last = SessionBadge.Layout(state.Session, state.Warning, elapsed, expanded, screenWidth, screenHeight);
    }
}
