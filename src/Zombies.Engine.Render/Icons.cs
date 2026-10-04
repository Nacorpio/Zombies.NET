namespace Zombies.Engine.Render;

/// <summary>Names of the built-in icons. Use these instead of typing the strings.</summary>
public static class IconNames
{
    public const string Robot = "robot";
    public const string Use = "use";
    public const string Equip = "equip";
    public const string Drop = "drop";
    public const string Split = "split";
    public const string Inspect = "inspect";
    public const string Craft = "craft";
    public const string Attach = "attach";
    public const string Warning = "warning";
    public const string Check = "check";
    public const string Cross = "cross";

    /// <summary>What is drawn when a name is not an icon, so a typo is visible instead of silent.</summary>
    public const string Unknown = "unknown";
}

/// <summary>
/// The built-in icon set: small named pixel images, 16 by 16, sitting in the same atlas as the debug font so a screen
/// that mixes text and icons still draws in one pass.
/// </summary>
public static class Icons
{
    public const int Size = 16;

    private static readonly Dictionary<string, int> Indexes = IconArt.All
        .Select((icon, index) => (icon.Name, index))
        .ToDictionary(x => x.Name, x => x.index, StringComparer.Ordinal);

    public static int Count => IconArt.All.Count;

    /// <summary>Every icon name, in atlas order.</summary>
    public static IReadOnlyList<string> Names { get; } = [.. IconArt.All.Select(i => i.Name)];

    public static bool Exists(string name) => Indexes.ContainsKey(name);

    /// <summary>Atlas rectangle of an icon, as UV. An unknown name gives the placeholder icon.</summary>
    public static (float U0, float V0, float U1, float V1) Uv(string name)
    {
        var index = Indexes.TryGetValue(name, out var found) ? found : Indexes[IconNames.Unknown];
        var (x, y) = Origin(index);
        return ((float)x / UiAtlas.Width, (float)y / UiAtlas.Height, (float)(x + Size) / UiAtlas.Width, (float)(y + Size) / UiAtlas.Height);
    }

    /// <summary>Top-left pixel of an icon in the atlas.</summary>
    internal static (int X, int Y) Origin(int index) =>
        ((index % UiAtlas.IconsPerRow) * Size, UiAtlas.FontHeight + ((index / UiAtlas.IconsPerRow) * Size));

    /// <summary>Fails with a precise message when icon art is not 16 rows of 16 allowed characters, instead of drawing it skewed.</summary>
    private static void Validate(string name, string[] rows)
    {
        if (rows.Length != Size)
        {
            throw new InvalidOperationException($"Icon '{name}' has {rows.Length} rows; it must have {Size}.");
        }

        for (var y = 0; y < rows.Length; y++)
        {
            if (rows[y].Length != Size)
            {
                throw new InvalidOperationException($"Icon '{name}' row {y} is {rows[y].Length} characters wide; it must be {Size}.");
            }

            foreach (var c in rows[y])
            {
                if (c is not (IconArt.Solid or IconArt.Soft or IconArt.Empty))
                {
                    throw new InvalidOperationException($"Icon '{name}' row {y} contains '{c}'; use only '{IconArt.Solid}', '{IconArt.Soft}', or a space.");
                }
            }
        }
    }

    /// <summary>Writes every icon into the atlas pixels: solid art as 255, soft art as a partial tone.</summary>
    internal static void DrawInto(byte[] atlas, int atlasWidth)
    {
        for (var index = 0; index < IconArt.All.Count; index++)
        {
            var (originX, originY) = Origin(index);
            var (name, rows) = IconArt.All[index];
            Validate(name, rows);
            for (var y = 0; y < Size; y++)
            {
                for (var x = 0; x < Size; x++)
                {
                    atlas[((originY + y) * atlasWidth) + originX + x] = rows[y][x] switch
                    {
                        IconArt.Solid => 255,
                        IconArt.Soft => 150,
                        _ => 0,
                    };
                }
            }
        }
    }
}

/// <summary>The one texture that holds the debug font and the icons, so the sprite pass needs a single binding.</summary>
public static class UiAtlas
{
    public const int Width = DebugFont.CellsPerRow * DebugFont.CellSize;

    public const int IconsPerRow = Width / Icons.Size;

    /// <summary>Height of the font part. The icons start right below it.</summary>
    public static int FontHeight => DebugFont.FontRows * DebugFont.CellSize;

    public static int Height => FontHeight + (((Icons.Count + IconsPerRow - 1) / IconsPerRow) * Icons.Size);

    /// <summary>The atlas as one byte per pixel of coverage.</summary>
    public static byte[] CreatePixels()
    {
        var atlas = new byte[Width * Height];
        DebugFont.DrawInto(atlas, Width);
        Icons.DrawInto(atlas, Width);
        return atlas;
    }
}
