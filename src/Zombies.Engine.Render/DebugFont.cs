namespace Zombies.Engine.Render;

/// <summary>
/// A tiny built-in 5 by 7 bitmap font for debug overlays, so the renderer can show text before any real font, texture, or
/// UI system exists. Letters draw in capitals. The atlas is one 8-bit channel, 16 cells wide, with one fully solid cell for
/// drawing plain rectangles through the same pipeline.
/// </summary>
public static class DebugFont
{
    public const int CellSize = 8;
    public const int GlyphWidth = 5;
    public const int GlyphHeight = 7;

    /// <summary>Pixels from one character to the next at scale 1: the glyph plus one column of spacing.</summary>
    public const int Advance = 6;

    /// <summary>Pixels from one line to the next at scale 1: the glyph plus one row of spacing.</summary>
    public const int LineHeight = 8;

    public const int CellsPerRow = 16;

    private const string Characters = " 0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ.:-/%(),+=_|";

    // Each glyph is seven rows of five columns; '#' is lit.
    private static readonly string[][] Glyphs =
    [
        ["     ", "     ", "     ", "     ", "     ", "     ", "     "], // space
        [" ### ", "#   #", "#  ##", "# # #", "##  #", "#   #", " ### "], // 0
        ["  #  ", " ##  ", "  #  ", "  #  ", "  #  ", "  #  ", " ### "], // 1
        [" ### ", "#   #", "    #", "   # ", "  #  ", " #   ", "#####"], // 2
        [" ### ", "#   #", "    #", "  ## ", "    #", "#   #", " ### "], // 3
        ["   # ", "  ## ", " # # ", "#  # ", "#####", "   # ", "   # "], // 4
        ["#####", "#    ", "#### ", "    #", "    #", "#   #", " ### "], // 5
        ["  ## ", " #   ", "#    ", "#### ", "#   #", "#   #", " ### "], // 6
        ["#####", "    #", "   # ", "  #  ", " #   ", " #   ", " #   "], // 7
        [" ### ", "#   #", "#   #", " ### ", "#   #", "#   #", " ### "], // 8
        [" ### ", "#   #", "#   #", " ####", "    #", "   # ", " ##  "], // 9
        [" ### ", "#   #", "#   #", "#####", "#   #", "#   #", "#   #"], // A
        ["#### ", "#   #", "#   #", "#### ", "#   #", "#   #", "#### "], // B
        [" ### ", "#   #", "#    ", "#    ", "#    ", "#   #", " ### "], // C
        ["#### ", "#   #", "#   #", "#   #", "#   #", "#   #", "#### "], // D
        ["#####", "#    ", "#    ", "#### ", "#    ", "#    ", "#####"], // E
        ["#####", "#    ", "#    ", "#### ", "#    ", "#    ", "#    "], // F
        [" ### ", "#   #", "#    ", "# ###", "#   #", "#   #", " ####"], // G
        ["#   #", "#   #", "#   #", "#####", "#   #", "#   #", "#   #"], // H
        [" ### ", "  #  ", "  #  ", "  #  ", "  #  ", "  #  ", " ### "], // I
        ["  ###", "   # ", "   # ", "   # ", "   # ", "#  # ", " ##  "], // J
        ["#   #", "#  # ", "# #  ", "##   ", "# #  ", "#  # ", "#   #"], // K
        ["#    ", "#    ", "#    ", "#    ", "#    ", "#    ", "#####"], // L
        ["#   #", "## ##", "# # #", "# # #", "#   #", "#   #", "#   #"], // M
        ["#   #", "##  #", "# # #", "#  ##", "#   #", "#   #", "#   #"], // N
        [" ### ", "#   #", "#   #", "#   #", "#   #", "#   #", " ### "], // O
        ["#### ", "#   #", "#   #", "#### ", "#    ", "#    ", "#    "], // P
        [" ### ", "#   #", "#   #", "#   #", "# # #", "#  # ", " ## #"], // Q
        ["#### ", "#   #", "#   #", "#### ", "# #  ", "#  # ", "#   #"], // R
        [" ####", "#    ", "#    ", " ### ", "    #", "    #", "#### "], // S
        ["#####", "  #  ", "  #  ", "  #  ", "  #  ", "  #  ", "  #  "], // T
        ["#   #", "#   #", "#   #", "#   #", "#   #", "#   #", " ### "], // U
        ["#   #", "#   #", "#   #", "#   #", "#   #", " # # ", "  #  "], // V
        ["#   #", "#   #", "#   #", "# # #", "# # #", "## ##", "#   #"], // W
        ["#   #", "#   #", " # # ", "  #  ", " # # ", "#   #", "#   #"], // X
        ["#   #", "#   #", " # # ", "  #  ", "  #  ", "  #  ", "  #  "], // Y
        ["#####", "    #", "   # ", "  #  ", " #   ", "#    ", "#####"], // Z
        ["     ", "     ", "     ", "     ", "     ", " ##  ", " ##  "], // .
        ["     ", " ##  ", " ##  ", "     ", " ##  ", " ##  ", "     "], // :
        ["     ", "     ", "     ", "#####", "     ", "     ", "     "], // -
        ["    #", "    #", "   # ", "  #  ", " #   ", "#    ", "#    "], // /
        ["##  #", "##  #", "   # ", "  #  ", " #   ", "#  ##", "#  ##"], // %
        ["   # ", "  #  ", " #   ", " #   ", " #   ", "  #  ", "   # "], // (
        [" #   ", "  #  ", "   # ", "   # ", "   # ", "  #  ", " #   "], // )
        ["     ", "     ", "     ", "     ", " ##  ", "  #  ", " #   "], // ,
        ["     ", "  #  ", "  #  ", "#####", "  #  ", "  #  ", "     "], // +
        ["     ", "     ", "#####", "     ", "#####", "     ", "     "], // =
        ["     ", "     ", "     ", "     ", "     ", "     ", "#####"], // _
        ["  #  ", "  #  ", "  #  ", "  #  ", "  #  ", "  #  ", "  #  "], // |
    ];

    /// <summary>Width and height of the whole shared atlas, which holds the icons below the font. See <see cref="UiAtlas"/>.</summary>
    public static int AtlasWidth => UiAtlas.Width;

    public static int AtlasHeight => UiAtlas.Height;

    /// <summary>Rows of 8-pixel cells the font itself occupies at the top of the atlas.</summary>
    internal static int FontRows => ((Glyphs.Length + 1) + CellsPerRow - 1) / CellsPerRow;

    /// <summary>The whole atlas as one byte per pixel: 255 where a pixel is lit, 0 elsewhere.</summary>
    public static byte[] CreateAtlas() => UiAtlas.CreatePixels();

    /// <summary>Draws the glyph cells and the solid cell into an atlas that is <paramref name="atlasWidth"/> wide.</summary>
    internal static void DrawInto(byte[] atlas, int atlasWidth)
    {
        for (var glyph = 0; glyph < Glyphs.Length; glyph++)
        {
            var originX = (glyph % CellsPerRow) * CellSize;
            var originY = (glyph / CellsPerRow) * CellSize;
            for (var row = 0; row < GlyphHeight; row++)
            {
                for (var column = 0; column < GlyphWidth; column++)
                {
                    if (Glyphs[glyph][row][column] == '#')
                    {
                        atlas[((originY + row) * atlasWidth) + originX + column] = 255;
                    }
                }
            }
        }

        var white = Glyphs.Length;
        var whiteX = (white % CellsPerRow) * CellSize;
        var whiteY = (white / CellsPerRow) * CellSize;
        for (var y = 0; y < CellSize; y++)
        {
            for (var x = 0; x < CellSize; x++)
            {
                atlas[((whiteY + y) * atlasWidth) + whiteX + x] = 255;
            }
        }
    }

    /// <summary>UV rectangle of a character's drawn area (glyph plus its spacing column and row). Unknown characters come back as a space.</summary>
    public static (float U0, float V0, float U1, float V1) GlyphUv(char c)
    {
        var index = Characters.IndexOf(char.ToUpperInvariant(c), StringComparison.Ordinal);
        return CellUv(index < 0 ? 0 : index, Advance, LineHeight);
    }

    /// <summary>UV rectangle fully inside the solid cell. Sampling it gives 255 everywhere, so a quad there is a flat rectangle.</summary>
    public static (float U0, float V0, float U1, float V1) SolidUv()
    {
        var (u0, v0, u1, v1) = CellUv(Glyphs.Length, CellSize - 2, CellSize - 2);
        var half = 1f / AtlasWidth;
        return (u0 + half, v0 + (1f / AtlasHeight), u1 + half, v1 + (1f / AtlasHeight));
    }

    public static bool HasGlyph(char c) => Characters.Contains(char.ToUpperInvariant(c), StringComparison.Ordinal);

    public static int MeasureWidth(string text, int scale) => text.Length * Advance * scale;

    private static (float U0, float V0, float U1, float V1) CellUv(int cell, int width, int height)
    {
        var x = (cell % CellsPerRow) * CellSize;
        var y = (cell / CellsPerRow) * CellSize;
        return ((float)x / AtlasWidth, (float)y / AtlasHeight, (float)(x + width) / AtlasWidth, (float)(y + height) / AtlasHeight);
    }
}
