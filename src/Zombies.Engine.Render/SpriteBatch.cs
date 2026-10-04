namespace Zombies.Engine.Render;

/// <summary>
/// Collects textured and flat quads for one frame as plain vertex data, with no graphics API involved,
/// so every backend draws the same batch. Coordinates are pixels from the top-left corner.
/// </summary>
public sealed class SpriteBatch
{
    /// <summary>Most quads one frame can hold. Extra quads are dropped and <see cref="Overflowed"/> is set.</summary>
    public const int MaxQuads = 16000;

    private readonly SpriteVertex[] _vertices = new SpriteVertex[MaxQuads * 4];
    private int _count;

    public int QuadCount => _count / 4;

    public bool Overflowed { get; private set; }

    public ReadOnlySpan<SpriteVertex> Vertices => _vertices.AsSpan(0, _count);

    public void Clear()
    {
        _count = 0;
        Overflowed = false;
    }

    /// <summary>Adds a quad that samples a rectangle of the font atlas.</summary>
    public void Quad(float x, float y, float width, float height, (float U0, float V0, float U1, float V1) uv, Rgba color)
    {
        if (_count + 4 > _vertices.Length)
        {
            Overflowed = true;
            return;
        }

        var packed = color.Packed;
        _vertices[_count++] = new SpriteVertex(x, y, uv.U0, uv.V0, packed);
        _vertices[_count++] = new SpriteVertex(x + width, y, uv.U1, uv.V0, packed);
        _vertices[_count++] = new SpriteVertex(x + width, y + height, uv.U1, uv.V1, packed);
        _vertices[_count++] = new SpriteVertex(x, y + height, uv.U0, uv.V1, packed);
    }

    public void FillRect(float x, float y, float width, float height, Rgba color) => Quad(x, y, width, height, DebugFont.SolidUv(), color);

    /// <summary>Draws text with the built-in debug font. Each pixel of the font becomes <paramref name="scale"/> screen pixels. A newline starts a new line.</summary>
    public void DrawText(string text, float x, float y, int scale, Rgba color)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentOutOfRangeException.ThrowIfLessThan(scale, 1);

        var cursorX = x;
        var cursorY = y;
        foreach (var c in text)
        {
            if (c == '\n')
            {
                cursorX = x;
                cursorY += DebugFont.LineHeight * scale;
                continue;
            }

            if (c != ' ')
            {
                Quad(cursorX, cursorY, DebugFont.Advance * scale, DebugFont.LineHeight * scale, DebugFont.GlyphUv(c), color);
            }

            cursorX += DebugFont.Advance * scale;
        }
    }
}
