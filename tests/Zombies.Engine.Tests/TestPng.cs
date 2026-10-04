using System.Buffers.Binary;
using System.IO.Compression;
using Zombies.Engine.Render;

namespace Zombies.Engine.Tests;

/// <summary>Builds PNG files byte by byte, so the reader is tested against formats the writer never produces.</summary>
internal static class TestPng
{
    public static (byte R, byte G, byte B, byte A) C(int r, int g, int b, int a) => ((byte)r, (byte)g, (byte)b, (byte)a);

    public static byte[] Rgba(int width, int height, Func<int, int, (byte R, byte G, byte B, byte A)> pixel)
    {
        var rgba = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var (r, g, b, a) = pixel(x, y);
                new byte[] { r, g, b, a }.CopyTo(rgba.AsSpan(((y * width) + x) * 4, 4));
            }
        }

        using var stream = new MemoryStream();
        PngWriter.Write(stream, width, height, rgba);
        return stream.ToArray();
    }

    public static RgbaImage Image(int width, int height, Func<int, int, (byte R, byte G, byte B, byte A)> pixel)
    {
        Assert.True(PngReader.TryDecode(Rgba(width, height, pixel), out var image, out var error), error);
        return image;
    }

    /// <param name="scanlines">Every row already prefixed with its filter byte, exactly as it goes into the zlib stream.</param>
    public static byte[] Build(int width, int height, int colorType, int bitDepth, byte[] scanlines, bool interlaced = false, byte[]? palette = null, byte[]? transparency = null)
    {
        using var stream = new MemoryStream();
        stream.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        var header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), (uint)height);
        header[8] = (byte)bitDepth;
        header[9] = (byte)colorType;
        header[12] = (byte)(interlaced ? 1 : 0);
        Chunk(stream, "IHDR", header);
        if (palette is not null)
        {
            Chunk(stream, "PLTE", palette);
        }

        if (transparency is not null)
        {
            Chunk(stream, "tRNS", transparency);
        }

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            zlib.Write(scanlines);
        }

        Chunk(stream, "IDAT", compressed.ToArray());
        Chunk(stream, "IEND", []);
        return stream.ToArray();
    }

    public static void Chunk(Stream stream, string type, byte[] data)
    {
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(number, (uint)data.Length);
        stream.Write(number);
        var name = System.Text.Encoding.ASCII.GetBytes(type);
        stream.Write(name);
        stream.Write(data);
        BinaryPrimitives.WriteUInt32BigEndian(number, Crc(name, data));
        stream.Write(number);
    }

    /// <summary>Applies PNG filter <paramref name="filter"/> to one row, the way an encoder would.</summary>
    public static byte[] Filter(int filter, byte[] row, byte[] previous, int stride)
    {
        var result = new byte[row.Length + 1];
        result[0] = (byte)filter;
        for (var i = 0; i < row.Length; i++)
        {
            int left = i >= stride ? row[i - stride] : 0;
            int up = previous[i];
            int upLeft = i >= stride ? previous[i - stride] : 0;
            var predicted = filter switch
            {
                1 => left,
                2 => up,
                3 => (left + up) / 2,
                4 => Paeth(left, up, upLeft),
                _ => 0,
            };
            result[i + 1] = (byte)(row[i] - predicted);
        }

        return result;
    }

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        var pa = Math.Abs(p - a);
        var pb = Math.Abs(p - b);
        var pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    private static uint Crc(byte[] type, byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in type.Concat(data))
        {
            crc ^= b;
            for (var k = 0; k < 8; k++)
            {
                crc = (crc & 1) != 0 ? 0xEDB88320 ^ (crc >> 1) : crc >> 1;
            }
        }

        return crc ^ 0xFFFFFFFF;
    }
}
