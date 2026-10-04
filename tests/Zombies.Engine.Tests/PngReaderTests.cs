using Zombies.Engine.Render;

namespace Zombies.Engine.Tests;

public sealed class PngReaderTests
{
    private static (byte R, byte G, byte B, byte A) Pattern(int x, int y) =>
        ((byte)((x * 37) + 5), (byte)((y * 53) + 9), (byte)(x ^ y), (byte)(255 - (x * 11) - y));

    [Fact]
    public void WriterOutput_DecodesBackToTheSamePixels()
    {
        var png = TestPng.Rgba(13, 7, Pattern);

        var image = PngReader.Decode(png);

        Assert.Equal((13, 7), (image.Width, image.Height));
        for (var y = 0; y < 7; y++)
        {
            for (var x = 0; x < 13; x++)
            {
                var (r, g, b, a) = Pattern(x, y);
                Assert.Equal(new byte[] { r, g, b, a }, image.Pixels.AsSpan(((y * 13) + x) * 4, 4).ToArray());
            }
        }
    }

    [Fact]
    public void FileRoundTrip_ThroughDiskWorksToo()
    {
        var path = Path.Combine(Path.GetTempPath(), $"zombies-png-{Guid.NewGuid():N}.png");
        try
        {
            PngWriter.Write(path, 2, 1, [1, 2, 3, 4, 5, 6, 7, 8]);

            Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, PngReader.Read(path).Pixels);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void EveryRowFilter_IsReversed(int filter)
    {
        const int width = 6;
        const int height = 5;
        var rows = new List<byte[]>();
        for (var y = 0; y < height; y++)
        {
            var row = new byte[width * 4];
            for (var i = 0; i < row.Length; i++)
            {
                row[i] = (byte)((i * 29) + (y * 71) + (i * y));
            }

            rows.Add(row);
        }

        var scanlines = new List<byte>();
        for (var y = 0; y < height; y++)
        {
            scanlines.AddRange(TestPng.Filter(filter, rows[y], y == 0 ? new byte[width * 4] : rows[y - 1], stride: 4));
        }

        var image = PngReader.Decode(TestPng.Build(width, height, 6, 8, [.. scanlines]));

        Assert.Equal(rows.SelectMany(r => r), image.Pixels);
    }

    [Fact]
    public void Gray8_BecomesOpaqueGrayRgba()
    {
        var image = PngReader.Decode(TestPng.Build(2, 1, 0, 8, [0, 10, 200]));

        Assert.Equal(new byte[] { 10, 10, 10, 255, 200, 200, 200, 255 }, image.Pixels);
    }

    [Fact]
    public void GrayWithAlpha8_KeepsItsAlpha()
    {
        var image = PngReader.Decode(TestPng.Build(2, 1, 4, 8, [0, 50, 100, 60, 0]));

        Assert.Equal(new byte[] { 50, 50, 50, 100, 60, 60, 60, 0 }, image.Pixels);
    }

    [Fact]
    public void Rgb8_WithAColourKey_MakesThatColourTransparent()
    {
        var key = new byte[] { 0, 1, 0, 2, 0, 3 };
        var image = PngReader.Decode(TestPng.Build(2, 1, 2, 8, [0, 1, 2, 3, 9, 9, 9], transparency: key));

        Assert.Equal(new byte[] { 1, 2, 3, 0, 9, 9, 9, 255 }, image.Pixels);
    }

    [Fact]
    public void Rgb16_KeepsTheHighByte()
    {
        var image = PngReader.Decode(TestPng.Build(1, 1, 2, 16, [0, 0xAB, 0xCD, 0x12, 0x34, 0xFF, 0x00]));

        Assert.Equal(new byte[] { 0xAB, 0x12, 0xFF, 255 }, image.Pixels);
    }

    [Fact]
    public void Palette2Bit_UnpacksIndexesAndAppliesPaletteAlpha()
    {
        // Four pixels in one byte: indexes 0, 1, 2, 3.
        var palette = new byte[] { 10, 0, 0, 0, 20, 0, 0, 0, 30, 1, 2, 3 };
        var transparency = new byte[] { 255, 128 };
        var image = PngReader.Decode(TestPng.Build(4, 1, 3, 2, [0, 0b00_01_10_11], palette: palette, transparency: transparency));

        Assert.Equal(
            new byte[] { 10, 0, 0, 255, 0, 20, 0, 128, 0, 0, 30, 255, 1, 2, 3, 255 },
            image.Pixels);
    }

    [Fact]
    public void Gray1Bit_ExpandsToBlackAndWhite()
    {
        var image = PngReader.Decode(TestPng.Build(3, 1, 0, 1, [0, 0b101_00000]));

        Assert.Equal(new byte[] { 255, 255, 255, 255, 0, 0, 0, 255, 255, 255, 255, 255 }, image.Pixels);
    }

    [Fact]
    public void Interlaced_ImageComesOutInTheRightPlaces()
    {
        const int size = 9;
        (byte R, byte G, byte B, byte A) At(int x, int y) => ((byte)x, (byte)y, (byte)(x + y), 255);
        (int X, int Y, int Dx, int Dy)[] passes = [(0, 0, 8, 8), (4, 0, 8, 8), (0, 4, 4, 8), (2, 0, 4, 4), (0, 2, 2, 4), (1, 0, 2, 2), (0, 1, 1, 2)];

        var scanlines = new List<byte>();
        foreach (var (startX, startY, dx, dy) in passes)
        {
            for (var y = startY; y < size; y += dy)
            {
                scanlines.Add(0);
                for (var x = startX; x < size; x += dx)
                {
                    var (r, g, b, a) = At(x, y);
                    scanlines.AddRange([r, g, b, a]);
                }
            }
        }

        var image = PngReader.Decode(TestPng.Build(size, size, 6, 8, [.. scanlines], interlaced: true));

        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var (r, g, b, a) = At(x, y);
                Assert.Equal(new byte[] { r, g, b, a }, image.Pixels.AsSpan(((y * size) + x) * 4, 4).ToArray());
            }
        }
    }

    [Fact]
    public void NotAPng_IsRejectedWithAReason()
    {
        Assert.False(PngReader.TryDecode([], out _, out var empty));
        Assert.Contains("empty", empty);
        Assert.False(PngReader.TryDecode("GIF89a not a png"u8, out _, out var other));
        Assert.Contains("not a PNG", other);
    }

    [Fact]
    public void TruncatedFile_IsRejected()
    {
        var png = TestPng.Rgba(4, 4, Pattern);

        Assert.False(PngReader.TryDecode(png.AsSpan(0, png.Length / 2), out _, out var error));
        Assert.False(string.IsNullOrEmpty(error));
        Assert.Throws<InvalidDataException>(() => PngReader.Decode(png.AsSpan(0, 20)));
    }

    [Fact]
    public void CorruptImageData_IsRejectedNotThrownAsSomethingElse()
    {
        var png = TestPng.Rgba(8, 8, Pattern);
        var corrupt = (byte[])png.Clone();
        var idat = corrupt.AsSpan().IndexOf("IDAT"u8);
        for (var i = idat + 6; i < idat + 20; i++)
        {
            corrupt[i] ^= 0xFF;
        }

        Assert.False(PngReader.TryDecode(corrupt, out _, out var error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void ImageDataShorterThanTheImage_IsRejected()
    {
        Assert.False(PngReader.TryDecode(TestPng.Build(4, 4, 6, 8, new byte[17]), out _, out var error));
        Assert.Contains("shorter", error);
    }

    [Fact]
    public void AbsurdDimensions_AreRejectedBeforeAnythingIsAllocated()
    {
        var png = TestPng.Build(PngReader.MaxSide + 1, 1, 6, 8, [0]);

        Assert.False(PngReader.TryDecode(png, out _, out var error));
        Assert.Contains("each side must be", error);
    }

    [Fact]
    public void PaletteImageWithoutAPalette_IsRejected()
    {
        Assert.False(PngReader.TryDecode(TestPng.Build(1, 1, 3, 8, [0, 0]), out _, out var error));
        Assert.Contains("PLTE", error);
    }
}
