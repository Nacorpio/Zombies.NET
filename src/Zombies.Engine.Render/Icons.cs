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

    /// <summary>The icons the Base mod ships, each as a PNG in its icons folder.</summary>
    public static IReadOnlyList<string> BuiltIn { get; } =
        [Robot, Use, Equip, Drop, Split, Inspect, Craft, Attach, Warning, Check, Cross, Unknown];
}

/// <summary>
/// A set of named icons, each a <see cref="Size"/> square coverage mask, laid out in the shared atlas below the debug font
/// so a screen that mixes text and icons still draws in one pass. The placeholder is always present, always first.
/// </summary>
public sealed class IconSet
{
    public const int Size = 32;

    private readonly Dictionary<string, int> _indexes;
    private readonly List<byte[]> _masks;

    private IconSet(IReadOnlyList<(string Name, byte[] Mask)> icons)
    {
        _indexes = icons.Select((icon, index) => (icon.Name, index)).ToDictionary(x => x.Name, x => x.index, StringComparer.Ordinal);
        _masks = [.. icons.Select(i => i.Mask)];
        Names = [.. icons.Select(i => i.Name)];
    }

    /// <summary>A set that holds only the placeholder, which is what draws until real icons are loaded.</summary>
    public static IconSet Placeholder { get; } = Create([]);

    public int Count => Names.Count;

    /// <summary>Every icon name, in atlas order.</summary>
    public IReadOnlyList<string> Names { get; }

    /// <summary>Height of the shared atlas when it holds this set: the font, then as many rows of icons as it takes.</summary>
    public int AtlasHeight => UiAtlas.FontHeight + (((Count + UiAtlas.IconsPerRow - 1) / UiAtlas.IconsPerRow) * Size);

    /// <summary>
    /// Builds a set. The placeholder comes first; an icon named <see cref="IconNames.Unknown"/> replaces it in place.
    /// Every mask must be <see cref="Size"/> squared bytes.
    /// </summary>
    public static IconSet Create(IEnumerable<(string Name, byte[] Mask)> icons)
    {
        ArgumentNullException.ThrowIfNull(icons);
        var list = new List<(string Name, byte[] Mask)> { (IconNames.Unknown, PlaceholderMask()) };
        foreach (var (name, mask) in icons)
        {
            if (mask.Length != Size * Size)
            {
                throw new ArgumentException($"Icon '{name}' is {mask.Length} bytes; it must be {Size * Size}.", nameof(icons));
            }

            var existing = list.FindIndex(i => string.Equals(i.Name, name, StringComparison.Ordinal));
            if (existing >= 0)
            {
                list[existing] = (name, mask);
            }
            else
            {
                list.Add((name, mask));
            }
        }

        return new IconSet(list);
    }

    public bool Exists(string name) => _indexes.ContainsKey(name);

    /// <summary>The icon's pixels, row by row, 0 where empty. An unknown name gives the placeholder's.</summary>
    public ReadOnlySpan<byte> Mask(string name) => _masks[IndexOf(name)];

    /// <summary>Atlas rectangle of an icon, as UV. An unknown name gives the placeholder icon.</summary>
    public (float U0, float V0, float U1, float V1) Uv(string name)
    {
        var (x, y) = Origin(IndexOf(name));
        return ((float)x / UiAtlas.Width, (float)y / AtlasHeight, (float)(x + Size) / UiAtlas.Width, (float)(y + Size) / AtlasHeight);
    }

    /// <summary>Top-left pixel of an icon in the atlas.</summary>
    internal static (int X, int Y) Origin(int index) =>
        ((index % UiAtlas.IconsPerRow) * Size, UiAtlas.FontHeight + ((index / UiAtlas.IconsPerRow) * Size));

    /// <summary>Writes every icon into the atlas pixels.</summary>
    internal void DrawInto(byte[] atlas, int atlasWidth)
    {
        for (var index = 0; index < _masks.Count; index++)
        {
            var (originX, originY) = Origin(index);
            for (var y = 0; y < Size; y++)
            {
                _masks[index].AsSpan(y * Size, Size).CopyTo(atlas.AsSpan(((originY + y) * atlasWidth) + originX, Size));
            }
        }
    }

    private int IndexOf(string name) => _indexes.TryGetValue(name, out var found) ? found : 0;

    /// <summary>A hollow box crossed out, so a missing icon is hard to mistake for art.</summary>
    private static byte[] PlaceholderMask()
    {
        const int edge = 3;
        var mask = new byte[Size * Size];
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                var border = x < edge || y < edge || x >= Size - edge || y >= Size - edge;
                var diagonal = Math.Abs(x - y) < edge || Math.Abs(x - (Size - 1 - y)) < edge;
                if (border || diagonal)
                {
                    mask[(y * Size) + x] = 255;
                }
            }
        }

        return mask;
    }
}

/// <summary>
/// The icons the game draws with. The renderer reads them once, when it builds the atlas, so install the loaded set
/// before the renderer is created.
/// </summary>
public static class Icons
{
    private static volatile IconSet _current = IconSet.Placeholder;

    public const int Size = IconSet.Size;

    public static IconSet Current => _current;

    public static void Install(IconSet set)
    {
        ArgumentNullException.ThrowIfNull(set);
        _current = set;
    }

    public static int Count => _current.Count;

    public static IReadOnlyList<string> Names => _current.Names;

    public static bool Exists(string name) => _current.Exists(name);

    public static (float U0, float V0, float U1, float V1) Uv(string name) => _current.Uv(name);
}

/// <summary>The one texture that holds the debug font and the icons, so the sprite pass needs a single binding.</summary>
public static class UiAtlas
{
    public const int Width = DebugFont.CellsPerRow * DebugFont.CellSize;

    public const int IconsPerRow = Width / IconSet.Size;

    /// <summary>Height of the font part. The icons start right below it.</summary>
    public static int FontHeight => DebugFont.FontRows * DebugFont.CellSize;

    public static int Height => Icons.Current.AtlasHeight;

    /// <summary>The atlas as one byte per pixel of coverage.</summary>
    public static byte[] CreatePixels() => CreatePixels(Icons.Current);

    public static byte[] CreatePixels(IconSet icons)
    {
        ArgumentNullException.ThrowIfNull(icons);
        var atlas = new byte[Width * icons.AtlasHeight];
        DebugFont.DrawInto(atlas, Width);
        icons.DrawInto(atlas, Width);
        return atlas;
    }
}
