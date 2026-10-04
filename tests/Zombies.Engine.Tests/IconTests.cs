using Zombies.Engine.Render;

namespace Zombies.Engine.Tests;

public sealed class IconTests
{
    private static IconSet SetOf(int count) =>
        IconSet.Create(Enumerable.Range(0, count).Select(i => ($"icon{i}", Mask(i))));

    private static byte[] Mask(int seed)
    {
        var mask = new byte[IconSet.Size * IconSet.Size];
        mask[seed % mask.Length] = 255;
        mask[(seed * 7) % mask.Length] = 200;
        return mask;
    }

    [Fact]
    public void IconSize_IsThirtyTwo_AndTheAtlasGridFollowsIt()
    {
        Assert.Equal(32, IconSet.Size);
        Assert.Equal(IconSet.Size, Icons.Size);
        Assert.Equal(UiAtlas.Width / IconSet.Size, UiAtlas.IconsPerRow);
    }

    [Fact]
    public void EveryIconSet_ContainsThePlaceholderFirst()
    {
        Assert.Equal([IconNames.Unknown], IconSet.Placeholder.Names);
        Assert.Equal(IconNames.Unknown, SetOf(3).Names[0]);
    }

    [Fact]
    public void PlaceholderMask_IsThirtyTwoSquareAndClearlyNotArt()
    {
        var mask = IconSet.Placeholder.Mask(IconNames.Unknown).ToArray();

        Assert.Equal(IconSet.Size * IconSet.Size, mask.Length);
        Assert.InRange(mask.Count(b => b != 0), 100, 700);
        Assert.All(mask, b => Assert.True(b is 0 or 255));
    }

    [Fact]
    public void IconNamedUnknown_ReplacesThePlaceholderInPlace()
    {
        var set = IconSet.Create([(IconNames.Unknown, Mask(5)), ("other", Mask(6))]);

        Assert.Equal([IconNames.Unknown, "other"], set.Names);
        Assert.True(set.Mask(IconNames.Unknown).SequenceEqual(Mask(5)));
    }

    [Fact]
    public void WronglySizedMask_IsRefused()
    {
        Assert.Throws<ArgumentException>(() => IconSet.Create([("small", new byte[16 * 16])]));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(3, 1)]
    [InlineData(4, 2)]
    [InlineData(11, 3)]
    public void Atlas_SizesItselfFromTheIconSet(int extraIcons, int expectedRows)
    {
        var set = SetOf(extraIcons);

        Assert.Equal(UiAtlas.FontHeight + (expectedRows * IconSet.Size), set.AtlasHeight);
        Assert.Equal(UiAtlas.Width * set.AtlasHeight, UiAtlas.CreatePixels(set).Length);
    }

    [Fact]
    public void AtlasPixels_HoldEachIconsMaskInItsOwnCell()
    {
        var set = SetOf(9);
        var atlas = UiAtlas.CreatePixels(set);

        foreach (var name in set.Names)
        {
            var (x0, y0, _, _) = Pixels(set, set.Uv(name));
            var mask = set.Mask(name);
            for (var y = 0; y < IconSet.Size; y++)
            {
                Assert.True(atlas.AsSpan(((y0 + y) * UiAtlas.Width) + x0, IconSet.Size).SequenceEqual(mask.Slice(y * IconSet.Size, IconSet.Size)), $"'{name}' row {y}");
            }
        }
    }

    [Fact]
    public void Icons_AreThirtyTwoSquare_SitBelowTheFont_AndNeverShareACell()
    {
        var set = SetOf(14);
        var cells = new HashSet<(int, int)>();

        foreach (var name in set.Names)
        {
            var (x0, y0, x1, y1) = Pixels(set, set.Uv(name));
            Assert.Equal(IconSet.Size, x1 - x0);
            Assert.Equal(IconSet.Size, y1 - y0);
            Assert.True(y0 >= UiAtlas.FontHeight, $"'{name}' overlaps the font");
            Assert.InRange(x1, 1, UiAtlas.Width);
            Assert.InRange(y1, 1, set.AtlasHeight);
            Assert.True(cells.Add((x0, y0)), $"'{name}' shares a cell with another icon");
        }
    }

    [Fact]
    public void IconUv_LandsOnWholeTexelsSoWholeNumberScalesStayCrisp()
    {
        var set = SetOf(14);

        foreach (var name in set.Names)
        {
            var uv = set.Uv(name);

            Assert.Equal(Math.Round(uv.U0 * UiAtlas.Width), uv.U0 * UiAtlas.Width, 3);
            Assert.Equal(Math.Round(uv.V0 * set.AtlasHeight), uv.V0 * set.AtlasHeight, 3);
            Assert.Equal(Math.Round(uv.U1 * UiAtlas.Width), uv.U1 * UiAtlas.Width, 3);
            Assert.Equal(Math.Round(uv.V1 * set.AtlasHeight), uv.V1 * set.AtlasHeight, 3);
        }
    }

    [Fact]
    public void NameResolution_UnknownNamesDrawThePlaceholder()
    {
        var set = SetOf(3);

        Assert.True(set.Exists("icon1"));
        Assert.False(set.Exists("no_such_icon"));
        Assert.Equal(set.Uv(IconNames.Unknown), set.Uv("no_such_icon"));
        Assert.NotEqual(set.Uv(IconNames.Unknown), set.Uv("icon1"));
        Assert.True(set.Mask("no_such_icon").SequenceEqual(set.Mask(IconNames.Unknown)));
    }

    [Fact]
    public void FontAndIconsShareOneAtlas_SoAScreenWithBothDrawsInOnePass()
    {
        Assert.Equal(UiAtlas.Width, DebugFont.AtlasWidth);
        Assert.Equal(UiAtlas.Height, DebugFont.AtlasHeight);
        Assert.True(UiAtlas.Height > UiAtlas.FontHeight);
        Assert.True(DebugFont.GlyphUv('Z').V1 <= (float)UiAtlas.FontHeight / UiAtlas.Height + 1e-6f);
        Assert.True(DebugFont.SolidUv().V1 <= (float)UiAtlas.FontHeight / UiAtlas.Height + 1e-6f);
    }

    [Fact]
    public void SpriteBatch_DrawIconAddsOneTintedQuadOfTheRightSize()
    {
        var batch = new SpriteBatch();

        batch.DrawIcon(IconNames.Robot, 10, 20, scale: 2, new Rgba(1, 2, 3, 4));

        Assert.Equal(1, batch.QuadCount);
        var v = batch.Vertices;
        Assert.Equal((10f, 20f), (v[0].X, v[0].Y));
        Assert.Equal((10f + (2 * IconSet.Size), 20f + (2 * IconSet.Size)), (v[2].X, v[2].Y));
        var uv = Icons.Uv(IconNames.Robot);
        Assert.Equal((uv.U0, uv.V0), (v[0].U, v[0].V));
        Assert.Equal((uv.U1, uv.V1), (v[2].U, v[2].V));
        Assert.All(v.ToArray(), vertex => Assert.Equal(new Rgba(1, 2, 3, 4).Packed, vertex.Color));
    }

    [Theory]
    [InlineData(1, 32)]
    [InlineData(2, 64)]
    [InlineData(3, 96)]
    public void SpriteBatch_DrawIconScalesInWholeIconPixels(int scale, int expectedSize)
    {
        var batch = new SpriteBatch();

        batch.DrawIcon(IconNames.Check, 0, 0, scale, Rgba.White);

        Assert.Equal(expectedSize, batch.Vertices[2].X);
        Assert.Equal(expectedSize, batch.Vertices[2].Y);
    }

    [Fact]
    public void SpriteBatch_DrawIconUsesThePlaceholderForUnknownNamesAndRejectsBadArguments()
    {
        var batch = new SpriteBatch();

        batch.DrawIcon("typo", 0, 0, 1, Rgba.White);

        var uv = Icons.Uv(IconNames.Unknown);
        Assert.Equal((uv.U0, uv.V0), (batch.Vertices[0].U, batch.Vertices[0].V));
        Assert.Throws<ArgumentOutOfRangeException>(() => batch.DrawIcon(IconNames.Robot, 0, 0, 0, Rgba.White));
        Assert.Throws<ArgumentNullException>(() => batch.DrawIcon(null!, 0, 0, 1, Rgba.White));
    }

    [Fact]
    public void ExistingFontStillDrawsAfterTheAtlasGrew()
    {
        var atlas = UiAtlas.CreatePixels(SetOf(20));
        var (x0, y0, x1, y1) = Pixels(SetOf(20), DebugFont.GlyphUv('A'), fontCell: true);

        var lit = 0;
        for (var y = y0; y < y1; y++)
        {
            for (var x = x0; x < x1; x++)
            {
                lit += atlas[(y * UiAtlas.Width) + x] == 255 ? 1 : 0;
            }
        }

        Assert.True(lit > 10);
    }

    private static (int X0, int Y0, int X1, int Y1) Pixels(IconSet set, (float U0, float V0, float U1, float V1) uv, bool fontCell = false)
    {
        // Font UVs are relative to the installed set's atlas height, icon UVs to the set they came from.
        var height = fontCell ? UiAtlas.Height : set.AtlasHeight;
        return (
            (int)Math.Round(uv.U0 * UiAtlas.Width),
            (int)Math.Round(uv.V0 * height),
            (int)Math.Round(uv.U1 * UiAtlas.Width),
            (int)Math.Round(uv.V1 * height));
    }
}
