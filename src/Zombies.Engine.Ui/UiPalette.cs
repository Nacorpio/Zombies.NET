using Zombies.Engine.Render;

namespace Zombies.Engine.Ui;

/// <summary>The roles a color can play. Layouts and screens ask for a role, never a color, so the palette can change without touching them.</summary>
public enum PaletteRole
{
    /// <summary>Draws nothing. A panel with this role only groups its children.</summary>
    None,
    Text,
    Panel,
    Muted,
    Good,
    Warning,
    Danger,
    Info,
}

/// <summary>The colors the UI draws with.</summary>
public sealed class UiPalette
{
    private readonly Dictionary<PaletteRole, Rgba> _colors;

    private UiPalette(Dictionary<PaletteRole, Rgba> colors) => _colors = colors;

    private static readonly UiPalette StandardPalette = new(new Dictionary<PaletteRole, Rgba>
    {
        [PaletteRole.None] = new(0, 0, 0, 0),
        [PaletteRole.Text] = new(235, 235, 235),
        [PaletteRole.Panel] = new(20, 22, 28, 200),
        [PaletteRole.Muted] = new(140, 145, 155),
        [PaletteRole.Good] = new(80, 200, 90),
        [PaletteRole.Warning] = new(240, 190, 60),
        [PaletteRole.Danger] = new(225, 60, 50),
        [PaletteRole.Info] = new(90, 160, 230),
    });

    // The Okabe-Ito set: blue, yellow, and vermillion differ in lightness and in the blue-yellow axis, which stays visible with red-green color blindness.
    private static readonly UiPalette ColorblindPalette = new(new Dictionary<PaletteRole, Rgba>
    {
        [PaletteRole.None] = new(0, 0, 0, 0),
        [PaletteRole.Text] = new(235, 235, 235),
        [PaletteRole.Panel] = new(20, 22, 28, 200),
        [PaletteRole.Muted] = new(140, 145, 155),
        [PaletteRole.Good] = new(86, 180, 233),
        [PaletteRole.Warning] = new(240, 228, 66),
        [PaletteRole.Danger] = new(213, 94, 0),
        [PaletteRole.Info] = new(0, 114, 178),
    });

    public static UiPalette For(UiPaletteKind kind) => kind == UiPaletteKind.Colorblind ? ColorblindPalette : StandardPalette;

    public Rgba Color(PaletteRole role) => _colors[role];
}
