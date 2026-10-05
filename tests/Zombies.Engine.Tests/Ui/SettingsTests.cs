using Zombies.Engine.Platform;
using Zombies.Engine.Ui;

namespace Zombies.Engine.Tests.Ui;

public sealed class SettingsTests
{
    [Fact]
    public void Defaults_AreSensible()
    {
        var settings = new GameSettings();

        Assert.Equal(1f, settings.UiScale);
        Assert.Equal(70f, settings.FieldOfViewDegrees);
        Assert.True(settings.HeadBob);
        Assert.Equal(UiPaletteKind.Standard, settings.Palette);
        Assert.Equal("en", settings.Language);
    }

    [Theory]
    [InlineData(0.1f, GameSettings.MinUiScale)]
    [InlineData(9f, GameSettings.MaxUiScale)]
    [InlineData(1.5f, 1.5f)]
    [InlineData(float.NaN, 1f)]
    public void Sanitized_KeepsUiScaleInRange(float given, float expected)
    {
        Assert.Equal(expected, (new GameSettings { UiScale = given }).Sanitized().UiScale);
    }

    [Theory]
    [InlineData(10f, GameSettings.MinFieldOfView)]
    [InlineData(300f, GameSettings.MaxFieldOfView)]
    [InlineData(90f, 90f)]
    [InlineData(float.PositiveInfinity, 70f)]
    public void Sanitized_KeepsFieldOfViewInRange(float given, float expected)
    {
        Assert.Equal(expected, (new GameSettings { FieldOfViewDegrees = given }).Sanitized().FieldOfViewDegrees);
    }

    [Theory]
    [InlineData(1f, 1, 1)]
    [InlineData(2f, 1, 2)]
    [InlineData(1.5f, 2, 3)]
    [InlineData(0.75f, 2, 2)]
    [InlineData(0.5f, 1, 1)]
    public void TextScale_FollowsUiScaleButNeverDropsBelowOne(float uiScale, int baseScale, int expected)
    {
        Assert.Equal(expected, (new GameSettings { UiScale = uiScale }).TextScale(baseScale));
    }

    [Fact]
    public void FieldOfViewRadians_ConvertsDegrees()
    {
        Assert.Equal(MathF.PI / 2, (new GameSettings { FieldOfViewDegrees = 90 }).FieldOfViewRadians, 5);
    }

    [Fact]
    public void Json_RoundTripsEverySetting()
    {
        var keys = KeyBindings.Defaults();
        Assert.True(keys.Rebind(GameAction.Inventory, Key.B, out _));
        var original = new GameSettings { UiScale = 1.5f, FieldOfViewDegrees = 95, HeadBob = false, Palette = UiPaletteKind.Colorblind, Language = "sv", Keys = keys };

        Assert.True(SettingsJson.TryParse(SettingsJson.Serialize(original), out var loaded, out var error), error);

        Assert.Equal(original.UiScale, loaded.UiScale);
        Assert.Equal(original.FieldOfViewDegrees, loaded.FieldOfViewDegrees);
        Assert.False(loaded.HeadBob);
        Assert.Equal(UiPaletteKind.Colorblind, loaded.Palette);
        Assert.Equal("sv", loaded.Language);
        Assert.Equal(Key.B, loaded.Keys.KeyFor(GameAction.Inventory));
    }

    [Fact]
    public void Json_AMissingSetting_KeepsItsDefault_SoOldFilesStillLoad()
    {
        Assert.True(SettingsJson.TryParse("""{ "fieldOfViewDegrees": 80 }""", out var loaded, out _));

        Assert.Equal(80f, loaded.FieldOfViewDegrees);
        Assert.Equal(1f, loaded.UiScale);
        Assert.Equal(Key.W, loaded.Keys.KeyFor(GameAction.MoveForward));
    }

    [Fact]
    public void Json_OutOfRangeValues_AreClampedRatherThanRefused()
    {
        Assert.True(SettingsJson.TryParse("""{ "uiScale": 99, "fieldOfViewDegrees": 1 }""", out var loaded, out _));

        Assert.Equal(GameSettings.MaxUiScale, loaded.UiScale);
        Assert.Equal(GameSettings.MinFieldOfView, loaded.FieldOfViewDegrees);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{ "palette": "neon" }""")]
    [InlineData("""{ "uiScale": "big" }""")]
    [InlineData("""{ "headBob": 3 }""")]
    [InlineData("""{ "keys": { "Teleport": "T" } }""")]
    [InlineData("""{ "keys": { "Jump": "NotAKey" } }""")]
    public void Json_Garbage_IsRefusedWithAReason(string json)
    {
        Assert.False(SettingsJson.TryParse(json, out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void Json_TwoActionsOnOneKey_IsRefused()
    {
        Assert.False(SettingsJson.TryParse("""{ "keys": { "Jump": "W" } }""", out _, out var error));
        Assert.Contains("W", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Slider_MapsBetweenItsRangeAndAFraction_SoItCanBeDraggedWithTheMouse()
    {
        var slider = new Slider(50f, 110f, 5f, 70f);

        Assert.Equal(1f / 3f, slider.Fraction, 5);
        slider.SetFraction(1f);
        Assert.Equal(110f, slider.Value);
        slider.SetFraction(0f);
        Assert.Equal(50f, slider.Value);
    }

    [Fact]
    public void Slider_SnapsToItsStepAndStaysInRange()
    {
        var slider = new Slider(50f, 110f, 5f, 70f);

        slider.SetFraction(0.52f);
        Assert.Equal(80f, slider.Value);

        slider.Nudge(+1);
        Assert.Equal(85f, slider.Value);
        slider.Nudge(-100);
        Assert.Equal(50f, slider.Value);
    }

    [Fact]
    public void SettingsEditor_SlidersAndTogglesEditTheSettingsAndRaiseChanged()
    {
        var editor = new SettingsEditor(new GameSettings());
        var changes = 0;
        editor.Changed += () => changes++;

        editor.FieldOfView.SetFraction(1f);
        editor.UiScale.SetFraction(1f);
        editor.ToggleHeadBob();
        editor.NextPalette();

        var settings = editor.Current;
        Assert.Equal(GameSettings.MaxFieldOfView, settings.FieldOfViewDegrees);
        Assert.Equal(GameSettings.MaxUiScale, settings.UiScale);
        Assert.False(settings.HeadBob);
        Assert.Equal(UiPaletteKind.Colorblind, settings.Palette);
        Assert.Equal(4, changes);
    }

    [Fact]
    public void SettingsEditor_ASliderThatDidNotMove_DoesNotRaiseChanged()
    {
        var editor = new SettingsEditor(new GameSettings());
        var changes = 0;
        editor.Changed += () => changes++;

        editor.FieldOfView.SetFraction(editor.FieldOfView.Fraction);

        Assert.Equal(0, changes);
    }
}

public sealed class KeyBindingTests
{
    [Fact]
    public void Defaults_MatchTheKeysTheGameShipsWith()
    {
        var keys = KeyBindings.Defaults();

        Assert.Equal(Key.W, keys.KeyFor(GameAction.MoveForward));
        Assert.Equal(Key.S, keys.KeyFor(GameAction.MoveBack));
        Assert.Equal(Key.A, keys.KeyFor(GameAction.StrafeLeft));
        Assert.Equal(Key.D, keys.KeyFor(GameAction.StrafeRight));
        Assert.Equal(Key.Space, keys.KeyFor(GameAction.Jump));
        Assert.Equal(Key.LeftShift, keys.KeyFor(GameAction.Sprint));
        Assert.Equal(Key.LeftCtrl, keys.KeyFor(GameAction.Crouch));
        Assert.Equal(Key.Q, keys.KeyFor(GameAction.LeanLeft));
        Assert.Equal(Key.E, keys.KeyFor(GameAction.LeanRight));
        Assert.Equal(Key.Tab, keys.KeyFor(GameAction.ToggleMouse));
    }

    [Fact]
    public void Defaults_GiveEveryActionItsOwnKey()
    {
        var keys = KeyBindings.Defaults();

        var all = Enum.GetValues<GameAction>().Select(keys.KeyFor).ToList();

        Assert.Equal(all.Count, all.Distinct().Count());
        Assert.DoesNotContain(Key.Unknown, all);
    }

    [Fact]
    public void Rebind_AFreeKey_MovesTheAction()
    {
        var keys = KeyBindings.Defaults();

        Assert.True(keys.Rebind(GameAction.Jump, Key.J, out var conflict));

        Assert.Null(conflict);
        Assert.Equal(Key.J, keys.KeyFor(GameAction.Jump));
        Assert.Equal(GameAction.Jump, keys.ActionFor(Key.J));
        Assert.Null(keys.ActionFor(Key.Space));
    }

    [Fact]
    public void Rebind_AKeyAnotherActionUses_IsRefusedAndNamesThatAction()
    {
        var keys = KeyBindings.Defaults();

        Assert.False(keys.Rebind(GameAction.Jump, Key.W, out var conflict));

        Assert.Equal(GameAction.MoveForward, conflict);
        Assert.Equal(Key.Space, keys.KeyFor(GameAction.Jump));
        Assert.Equal(Key.W, keys.KeyFor(GameAction.MoveForward));
    }

    [Fact]
    public void RebindSwapping_GivesTheOtherActionTheKeyThisOneLeaves()
    {
        var keys = KeyBindings.Defaults();

        keys.RebindSwapping(GameAction.Jump, Key.W);

        Assert.Equal(Key.W, keys.KeyFor(GameAction.Jump));
        Assert.Equal(Key.Space, keys.KeyFor(GameAction.MoveForward));
    }

    [Fact]
    public void Rebind_ToItsOwnKey_SucceedsAndChangesNothing()
    {
        var keys = KeyBindings.Defaults();

        Assert.True(keys.Rebind(GameAction.Jump, Key.Space, out var conflict));

        Assert.Null(conflict);
        Assert.Equal(Key.Space, keys.KeyFor(GameAction.Jump));
    }

    [Theory]
    [InlineData(Key.Unknown)]
    [InlineData(Key.Escape)]
    public void Rebind_UnknownAndEscape_AreNeverAccepted(Key key)
    {
        var keys = KeyBindings.Defaults();

        Assert.False(keys.Rebind(GameAction.Jump, key, out _));

        Assert.Equal(Key.Space, keys.KeyFor(GameAction.Jump));
    }

    [Fact]
    public void Reset_RestoresTheDefaults()
    {
        var keys = KeyBindings.Defaults();
        keys.Rebind(GameAction.Jump, Key.J, out _);

        keys.Reset();

        Assert.Equal(Key.Space, keys.KeyFor(GameAction.Jump));
    }

    [Fact]
    public void Clone_IsIndependentOfTheOriginal()
    {
        var original = KeyBindings.Defaults();
        var copy = original.Clone();

        copy.Rebind(GameAction.Jump, Key.J, out _);

        Assert.Equal(Key.Space, original.KeyFor(GameAction.Jump));
    }

    [Fact]
    public void IsDown_ReadsTheInputStateThroughTheBinding()
    {
        var keys = KeyBindings.Defaults();
        keys.Rebind(GameAction.Jump, Key.J, out _);
        var input = new InputState();

        input.SetKey((int)Key.J, true);

        Assert.True(keys.IsDown(input, GameAction.Jump));
        Assert.False(keys.IsDown(input, GameAction.Sprint));
    }
}
