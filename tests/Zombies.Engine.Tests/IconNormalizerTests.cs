using Zombies.Engine.Render;

namespace Zombies.Engine.Tests;

public sealed class IconNormalizerTests
{
    private const int Size = IconSet.Size;

    private static readonly (byte R, byte G, byte B, byte A) Clear = (0, 0, 0, 0);
    private static readonly (byte R, byte G, byte B, byte A) White = (255, 255, 255, 255);

    private static int Visible(byte[] mask) => mask.Count(b => b != 0);

    private static bool IsVisible(byte[] mask, int x, int y) => mask[(y * Size) + x] != 0;

    [Fact]
    public void AnyInputSize_GivesExactlyOneIconOfPixels()
    {
        foreach (var (w, h) in new[] { (1, 1), (16, 16), (32, 32), (20, 10), (10, 20), (100, 37), (512, 512) })
        {
            var image = TestPng.Image(w, h, (x, y) => x == y || x + y == w ? White : Clear);

            Assert.Equal(Size * Size, IconNormalizer.Normalize(image).Length);
        }
    }

    [Fact]
    public void OversizedImage_IsShrunkAndKeepsItsShape()
    {
        // A 256 by 256 image with a solid square covering its middle half.
        var image = TestPng.Image(256, 256, (x, y) => x is >= 64 and < 192 && y is >= 64 and < 192 ? White : Clear);

        var mask = IconNormalizer.Normalize(image);

        Assert.Equal(16 * 16, Visible(mask));
        Assert.True(IsVisible(mask, 8, 8));
        Assert.True(IsVisible(mask, 23, 23));
        Assert.False(IsVisible(mask, 7, 8));
        Assert.False(IsVisible(mask, 24, 23));
    }

    [Fact]
    public void SoftEdgedArt_GetsHardEdgesAndNoHalo()
    {
        // A disc whose last 8 pixels fade out, drawn white at every alpha: the classic soft halo of generated art.
        var image = TestPng.Image(128, 128, (x, y) =>
        {
            var distance = Math.Sqrt(((x - 63.5) * (x - 63.5)) + ((y - 63.5) * (y - 63.5)));
            var alpha = Math.Clamp((56 - distance) / 8.0, 0, 1);
            return TestPng.C(255, 255, 255, (int)Math.Round(alpha * 255));
        });

        var mask = IconNormalizer.Normalize(image);

        // Only two kinds of pixel: empty, or full strength. Nothing in between to read as a halo.
        Assert.All(mask, b => Assert.True(b is 0 or 255, $"unexpected tone {b}"));

        // The half-alpha edge is at radius 52 of 128, which is 13 icon pixels at 32 wide.
        var expectedArea = Math.PI * 13 * 13;
        Assert.InRange(Visible(mask), expectedArea * 0.9, expectedArea * 1.1);
        Assert.False(IsVisible(mask, 0, 0));
        Assert.True(IsVisible(mask, 16, 16));
    }

    [Fact]
    public void FadedEdge_DoesNotDarkenTheArtItBelongsTo()
    {
        // Light art with a ramp from opaque to clear. Colour has to be read without the alpha mixed in.
        var image = TestPng.Image(64, 64, (x, _) => TestPng.C(240, 240, 240, Math.Clamp(255 - (Math.Max(0, x - 32) * 8), 0, 255)));

        var mask = IconNormalizer.Normalize(image);

        Assert.All(mask, b => Assert.True(b is 0 or 255, $"unexpected tone {b}"));
        Assert.True(IsVisible(mask, 0, 0));
        Assert.False(IsVisible(mask, 31, 0));
    }

    [Fact]
    public void SameSizeImage_KeepsEveryPixelWhereItWas()
    {
        var image = TestPng.Image(32, 32, (x, y) => (x, y) is (5, 9) or (30, 1) ? White : Clear);

        var mask = IconNormalizer.Normalize(image);

        Assert.Equal(2, Visible(mask));
        Assert.True(IsVisible(mask, 5, 9));
        Assert.True(IsVisible(mask, 30, 1));
    }

    [Fact]
    public void HalfSizeImage_IsEnlargedToWholeBlocksNotSmeared()
    {
        var image = TestPng.Image(16, 16, (x, y) => (x, y) == (3, 5) ? White : Clear);

        var mask = IconNormalizer.Normalize(image);

        Assert.Equal(4, Visible(mask));
        Assert.All(new[] { (6, 10), (7, 10), (6, 11), (7, 11) }, p => Assert.True(IsVisible(mask, p.Item1, p.Item2)));
    }

    [Fact]
    public void WideImage_IsFittedWholeAndCentredWithoutStretching()
    {
        var image = TestPng.Image(64, 32, (x, y) => x is >= 8 and < 56 && y is >= 8 and < 24 ? White : Clear);

        var mask = IconNormalizer.Normalize(image);

        // 64 wide fits to 32, so every source pixel is half an icon pixel and the 32 rows sit in rows 8 to 23.
        Assert.Equal(24 * 8, Visible(mask));
        Assert.True(IsVisible(mask, 4, 12));
        Assert.False(IsVisible(mask, 3, 12));
        Assert.False(IsVisible(mask, 16, 7));
        Assert.False(IsVisible(mask, 16, 24));
    }

    [Fact]
    public void FlatOpaqueBackground_IsRemoved()
    {
        // What image generators usually hand back: no transparency, a flat colour behind the art.
        var image = TestPng.Image(64, 64, (x, y) => x is >= 16 and < 48 && y is >= 16 and < 48 ? White : TestPng.C(20, 20, 20, 255));

        var mask = IconNormalizer.Normalize(image);

        Assert.Equal(16 * 16, Visible(mask));
        Assert.False(IsVisible(mask, 0, 0));
        Assert.True(IsVisible(mask, 16, 16));
    }

    [Fact]
    public void BrightAndDarkParts_KeepTheirShadingWithinTheToneRange()
    {
        var style = new IconStyle { MinTone = 100 };
        var image = TestPng.Image(32, 32, (x, y) => y > 3 ? Clear : x < 16 ? White : TestPng.C(90, 90, 90, 255));

        var mask = IconNormalizer.Normalize(image, style);

        Assert.Equal(255, mask[0]);
        Assert.Equal(100, mask[31]);
        Assert.All(mask.Where(b => b != 0), b => Assert.InRange(b, 100, 255));
    }

    [Fact]
    public void Palette_SnapsEveryVisiblePixelToATone()
    {
        byte[] palette = [120, 200, 255];
        var style = new IconStyle { Palette = palette };
        var image = TestPng.Image(32, 32, (x, y) => y > 7 ? Clear : TestPng.C(x * 8, x * 8, x * 8, 255));

        var mask = IconNormalizer.Normalize(image, style);

        Assert.All(mask.Where(b => b != 0), b => Assert.Contains(b, palette));
        Assert.Contains((byte)120, mask);
        Assert.Contains((byte)255, mask);
    }

    [Fact]
    public void FullyTransparentImage_GivesAnEmptyMask()
    {
        var mask = IconNormalizer.Normalize(TestPng.Image(40, 40, (_, _) => Clear));

        Assert.Equal(0, Visible(mask));
    }

    [Fact]
    public void FaintAlpha_BelowTheThreshold_IsDropped()
    {
        var image = TestPng.Image(32, 32, (_, _) => TestPng.C(255, 255, 255, 100));

        Assert.Equal(0, Visible(IconNormalizer.Normalize(image)));
        Assert.Equal(Size * Size, Visible(IconNormalizer.Normalize(image, new IconStyle { AlphaThreshold = 90 })));
    }
}
