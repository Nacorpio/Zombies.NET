using Zombies.Engine.Core;

namespace Zombies.Engine.Ui;

/// <summary>A value between a minimum and a maximum that snaps to a step. It maps to and from a 0 to 1 fraction, which is all a slider widget needs to draw the knob and to read a mouse drag.</summary>
public sealed class Slider
{
    public Slider(float minimum, float maximum, float step, float value)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maximum, minimum);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(step, 0f);
        Minimum = minimum;
        Maximum = maximum;
        Step = step;
        Value = Snap(value);
    }

    public event Action? Changed;

    public float Minimum { get; }

    public float Maximum { get; }

    public float Step { get; }

    public float Value { get; private set; }

    public float Fraction => (Value - Minimum) / (Maximum - Minimum);

    public void SetFraction(float fraction) => Set(Minimum + (Math.Clamp(float.IsFinite(fraction) ? fraction : 0f, 0f, 1f) * (Maximum - Minimum)));

    public void Set(float value) => Update(Snap(value));

    public void Nudge(int steps) => Update(Snap(Value + (steps * Step)));

    private void Update(float value)
    {
        if (value == Value)
        {
            return;
        }

        Value = value;
        Changed?.Invoke();
    }

    private float Snap(float value)
    {
        var clamped = Math.Clamp(float.IsFinite(value) ? value : Minimum, Minimum, Maximum);
        return Math.Clamp(Minimum + (MathF.Round((clamped - Minimum) / Step, MidpointRounding.AwayFromZero) * Step), Minimum, Maximum);
    }
}

/// <summary>The options screen's model: two sliders and two toggles that edit a <see cref="GameSettings"/>, and an event for the game to apply it.</summary>
public sealed class SettingsEditor
{
    private GameSettings _settings;

    public SettingsEditor(GameSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings.Sanitized();
        UiScale = new Slider(GameSettings.MinUiScale, GameSettings.MaxUiScale, 0.25f, _settings.UiScale);
        FieldOfView = new Slider(GameSettings.MinFieldOfView, GameSettings.MaxFieldOfView, 5f, _settings.FieldOfViewDegrees);
        UiScale.Changed += () => Apply(_settings with { UiScale = UiScale.Value });
        FieldOfView.Changed += () => Apply(_settings with { FieldOfViewDegrees = FieldOfView.Value });
    }

    public event Action? Changed;

    public Slider UiScale { get; }

    public Slider FieldOfView { get; }

    public GameSettings Current => _settings;

    public void ToggleHeadBob() => Apply(_settings with { HeadBob = !_settings.HeadBob });

    public void NextPalette()
    {
        var kinds = Enum.GetValues<UiPaletteKind>();
        Apply(_settings with { Palette = kinds[(Array.IndexOf(kinds, _settings.Palette) + 1) % kinds.Length] });
    }

    public void NextGore()
    {
        var levels = Enum.GetValues<GoreLevel>();
        Apply(_settings with { Gore = levels[(Array.IndexOf(levels, _settings.Gore) + 1) % levels.Length] });
    }

    public void SetLanguage(string language) => Apply(_settings with { Language = language });

    private void Apply(GameSettings settings)
    {
        _settings = settings.Sanitized();
        Changed?.Invoke();
    }
}
