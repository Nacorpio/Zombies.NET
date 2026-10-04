using Zombies.Engine.Render;

namespace Zombies.Engine.Tests;

public sealed class IconTests
{
    private static readonly string[] RequiredNames =
    [
        IconNames.Robot,
        IconNames.Use,
        IconNames.Equip,
        IconNames.Drop,
        IconNames.Split,
        IconNames.Inspect,
        IconNames.Craft,
        IconNames.Attach,
        IconNames.Warning,
        IconNames.Check,
        IconNames.Cross,
    ];

    [Fact]
    public void BuiltInSet_ContainsEveryRequiredIconAndAPlaceholder()
    {
        foreach (var name in RequiredNames)
        {
            Assert.True(Icons.Exists(name), $"missing icon '{name}'");
        }

        Assert.True(Icons.Exists(IconNames.Unknown));
        Assert.Equal(Icons.Names.Count, Icons.Count);
        Assert.Equal(Icons.Names.Distinct(StringComparer.Ordinal).Count(), Icons.Names.Count);
        Assert.All(Icons.Names, name => Assert.Matches("^[a-z]+$", name));
    }

    [Fact]
    public void Names_AppearInAtlasOrderWithTheRobotFirst()
    {
        Assert.Equal(IconNames.Robot, Icons.Names[0]);
        Assert.Equal(IconNames.Unknown, Icons.Names[^1]);
    }

    [Fact]
    public void Atlas_CanBeBuiltWhichValidatesEveryIconsArt()
    {
        var atlas = UiAtlas.CreatePixels();

        Assert.Equal(UiAtlas.Width * UiAtlas.Height, atlas.Length);
    }

    [Fact]
    public void EveryIcon_IsSixteenSquareAndOnlyUsesTheThreeTones()
    {
        var atlas = UiAtlas.CreatePixels();

        foreach (var name in Icons.Names)
        {
            var (x0, y0, x1, y1) = Pixels(Icons.Uv(name));
            Assert.Equal(Icons.Size, x1 - x0);
            Assert.Equal(Icons.Size, y1 - y0);

            var solid = 0;
            for (var y = y0; y < y1; y++)
            {
                for (var x = x0; x < x1; x++)
                {
                    var value = atlas[(y * UiAtlas.Width) + x];
                    Assert.Contains(value, new byte[] { 0, 150, 255 });
                    if (value != 0)
                    {
                        solid++;
                    }
                }
            }

            Assert.InRange(solid, 20, 200);
        }
    }

    [Fact]
    public void Icons_SitBelowTheFontAndNeverShareACell()
    {
        var cells = new HashSet<(int, int)>();

        foreach (var name in Icons.Names)
        {
            var (x0, y0, x1, y1) = Pixels(Icons.Uv(name));
            Assert.True(y0 >= UiAtlas.FontHeight, $"'{name}' overlaps the font");
            Assert.InRange(x1, 1, UiAtlas.Width);
            Assert.InRange(y1, 1, UiAtlas.Height);
            Assert.True(cells.Add((x0, y0)), $"'{name}' shares a cell with another icon");
        }
    }

    [Fact]
    public void IconUv_LandsOnWholeTexelsSoWholeNumberScalesStayCrisp()
    {
        foreach (var name in Icons.Names)
        {
            var uv = Icons.Uv(name);

            Assert.Equal(Math.Round(uv.U0 * UiAtlas.Width), uv.U0 * UiAtlas.Width, 3);
            Assert.Equal(Math.Round(uv.V0 * UiAtlas.Height), uv.V0 * UiAtlas.Height, 3);
            Assert.Equal(Math.Round(uv.U1 * UiAtlas.Width), uv.U1 * UiAtlas.Width, 3);
            Assert.Equal(Math.Round(uv.V1 * UiAtlas.Height), uv.V1 * UiAtlas.Height, 3);
        }
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
    public void RobotIcon_HasSoftToneEyesThatAreNotSolid()
    {
        var atlas = UiAtlas.CreatePixels();
        var (x0, y0, x1, y1) = Pixels(Icons.Uv(IconNames.Robot));

        var soft = 0;
        for (var y = y0; y < y1; y++)
        {
            for (var x = x0; x < x1; x++)
            {
                if (atlas[(y * UiAtlas.Width) + x] == 150)
                {
                    soft++;
                }
            }
        }

        Assert.Equal(8, soft);
    }

    [Fact]
    public void Placeholder_DrawsForAnUnknownNameInsteadOfFailing()
    {
        Assert.False(Icons.Exists("no_such_icon"));
        Assert.Equal(Icons.Uv(IconNames.Unknown), Icons.Uv("no_such_icon"));
        Assert.NotEqual(Icons.Uv(IconNames.Unknown), Icons.Uv(IconNames.Robot));
    }

    [Fact]
    public void SpriteBatch_DrawIconAddsOneTintedQuadOfTheRightSize()
    {
        var batch = new SpriteBatch();

        batch.DrawIcon(IconNames.Robot, 10, 20, scale: 2, new Rgba(1, 2, 3, 4));

        Assert.Equal(1, batch.QuadCount);
        var v = batch.Vertices;
        Assert.Equal((10f, 20f), (v[0].X, v[0].Y));
        Assert.Equal((42f, 52f), (v[2].X, v[2].Y));
        var uv = Icons.Uv(IconNames.Robot);
        Assert.Equal((uv.U0, uv.V0), (v[0].U, v[0].V));
        Assert.Equal((uv.U1, uv.V1), (v[2].U, v[2].V));
        Assert.All(v.ToArray(), vertex => Assert.Equal(new Rgba(1, 2, 3, 4).Packed, vertex.Color));
    }

    [Theory]
    [InlineData(1, 16)]
    [InlineData(2, 32)]
    [InlineData(3, 48)]
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
        var atlas = UiAtlas.CreatePixels();
        var (x0, y0, x1, y1) = Pixels(DebugFont.GlyphUv('A'));

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

    private static (int X0, int Y0, int X1, int Y1) Pixels((float U0, float V0, float U1, float V1) uv) => (
        (int)Math.Round(uv.U0 * UiAtlas.Width),
        (int)Math.Round(uv.V0 * UiAtlas.Height),
        (int)Math.Round(uv.U1 * UiAtlas.Width),
        (int)Math.Round(uv.V1 * UiAtlas.Height));
}
