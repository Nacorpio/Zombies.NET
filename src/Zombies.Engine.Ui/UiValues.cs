namespace Zombies.Engine.Ui;

/// <summary>What a screen shows right now, by widget id, laid over the fixed layout: text, a bar's fill, a color role, and whether a widget is drawn.</summary>
public sealed class UiValues
{
    private readonly Dictionary<string, string> _text = new(StringComparer.Ordinal);
    private readonly Dictionary<string, float> _fraction = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PaletteRole> _role = new(StringComparer.Ordinal);
    private readonly HashSet<string> _hidden = new(StringComparer.Ordinal);

    public void SetText(string id, string text) => _text[id] = text;

    public void SetFraction(string id, float fraction) => _fraction[id] = float.IsFinite(fraction) ? Math.Clamp(fraction, 0f, 1f) : 0f;

    public void SetRole(string id, PaletteRole role) => _role[id] = role;

    public void SetVisible(string id, bool visible)
    {
        if (visible)
        {
            _hidden.Remove(id);
        }
        else
        {
            _hidden.Add(id);
        }
    }

    public string? TextOf(string id) => _text.GetValueOrDefault(id);

    public float FractionOf(string id) => _fraction.GetValueOrDefault(id);

    public PaletteRole? RoleOf(string id) => _role.TryGetValue(id, out var role) ? role : null;

    public bool IsVisible(string id) => !_hidden.Contains(id);

    public void Clear()
    {
        _text.Clear();
        _fraction.Clear();
        _role.Clear();
        _hidden.Clear();
    }
}
