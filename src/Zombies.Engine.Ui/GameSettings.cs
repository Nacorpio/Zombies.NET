using Zombies.Engine.Core;

namespace Zombies.Engine.Ui;

/// <summary>Which set of colors the UI draws status in. The colorblind set tells states apart by lightness and hue pairs that stay distinct for the common kinds of color blindness.</summary>
public enum UiPaletteKind
{
    Standard,
    Colorblind,
}

/// <summary>What the player can change in the options. Out-of-range values are clamped by <see cref="Sanitized"/>.</summary>
public sealed record GameSettings
{
    public const float MinUiScale = 0.75f;
    public const float MaxUiScale = 2f;
    public const float MinFieldOfView = 50f;
    public const float MaxFieldOfView = 110f;

    private const float DefaultUiScale = 1f;
    private const float DefaultFieldOfView = 70f;

    /// <summary>How large the UI is drawn, 1 being the normal size.</summary>
    public float UiScale { get; init; } = DefaultUiScale;

    /// <summary>The vertical field of view of the first-person camera, in degrees.</summary>
    public float FieldOfViewDegrees { get; init; } = DefaultFieldOfView;

    /// <summary>Whether the first-person camera bobs while walking. Off for players who get motion sick.</summary>
    public bool HeadBob { get; init; } = true;

    public UiPaletteKind Palette { get; init; }

    /// <summary>How much blood, decals, particles and severed parts the client draws. Visuals only; hit results never depend on it.</summary>
    public GoreLevel Gore { get; init; } = GoreLevel.Low;

    public string Language { get; init; } = "en";

    public KeyBindings Keys { get; init; } = KeyBindings.Defaults();

    public float FieldOfViewRadians => FieldOfViewDegrees * MathF.PI / 180f;

    public GameSettings Sanitized() => this with
    {
        UiScale = float.IsFinite(UiScale) ? Math.Clamp(UiScale, MinUiScale, MaxUiScale) : DefaultUiScale,
        FieldOfViewDegrees = float.IsFinite(FieldOfViewDegrees) ? Math.Clamp(FieldOfViewDegrees, MinFieldOfView, MaxFieldOfView) : DefaultFieldOfView,
        Gore = Enum.IsDefined(Gore) ? Gore : GoreLevel.Low,
        Language = string.IsNullOrWhiteSpace(Language) ? "en" : Language,
    };

    /// <summary>The integer scale to draw bitmap text at, given the scale a screen is designed for. Never below 1, since a bitmap font cannot draw smaller.</summary>
    public int TextScale(int designScale) => Math.Max(1, (int)MathF.Round(designScale * UiScale, MidpointRounding.AwayFromZero));
}
