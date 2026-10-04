using System.Buffers.Binary;
using System.IO.Compression;

namespace Zombies.Engine.Render;

/// <summary>An image as 8-bit RGBA, rows top to bottom, straight (not premultiplied) alpha.</summary>
public sealed class RgbaImage
{
    public RgbaImage(int width, int height, byte[] pixels)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        ArgumentNullException.ThrowIfNull(pixels);
        if (pixels.Length != width * height * 4)
        {
            throw new ArgumentException($"Expected {width * height * 4} bytes of RGBA data, got {pixels.Length}.", nameof(pixels));
        }

        Width = width;
        Height = height;
        Pixels = pixels;
    }

    public int Width { get; }

    public int Height { get; }

    public byte[] Pixels { get; }
}

/// <summary>
/// Reads PNG files into 8-bit RGBA using only the standard library. Handles every standard colour type and bit depth,
/// transparency chunks, and interlacing. Anything else in the file, such as gamma or text, is ignored.
/// </summary>
public static class PngReader
{
    /// <summary>Largest width or height accepted, so a corrupt or hostile header cannot ask for gigabytes.</summary>
    public const int MaxSide = 8192;

    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    // Adam7 passes: start x, start y, step x, step y.
    private static readonly (int X, int Y, int Dx, int Dy)[] Passes =
        [(0, 0, 8, 8), (4, 0, 8, 8), (0, 4, 4, 8), (2, 0, 4, 4), (0, 2, 2, 4), (1, 0, 2, 2), (0, 1, 1, 2)];

    public static RgbaImage Read(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return Decode(File.ReadAllBytes(path));
    }

    /// <summary>Decodes a PNG, or explains why it cannot, without throwing.</summary>
    public static bool TryDecode(ReadOnlySpan<byte> png, out RgbaImage image, out string error)
    {
        try
        {
            image = Decode(png);
            error = string.Empty;
            return true;
        }
        catch (InvalidDataException ex)
        {
            image = null!;
            error = ex.Message;
            return false;
        }
    }

    /// <exception cref="InvalidDataException">The bytes are not a PNG this reader understands.</exception>
    public static RgbaImage Decode(ReadOnlySpan<byte> png)
    {
        if (png.Length < Signature.Length || !png[..Signature.Length].SequenceEqual(Signature))
        {
            throw new InvalidDataException(png.Length == 0 ? "The file is empty." : "The file is not a PNG.");
        }

        var header = default(Header);
        var haveHeader = false;
        byte[]? palette = null;
        byte[]? transparency = null;
        using var compressed = new MemoryStream();

        var offset = Signature.Length;
        while (true)
        {
            if (offset + 8 > png.Length)
            {
                throw new InvalidDataException("The file ends before its IEND chunk.");
            }

            var length = BinaryPrimitives.ReadUInt32BigEndian(png[offset..]);
            var type = png.Slice(offset + 4, 4);
            if (length > int.MaxValue || offset + 12 + (long)length > png.Length)
            {
                throw new InvalidDataException("A chunk runs past the end of the file.");
            }

            var data = png.Slice(offset + 8, (int)length);
            offset += 12 + (int)length;

            if (type.SequenceEqual("IHDR"u8))
            {
                header = ReadHeader(data);
                haveHeader = true;
            }
            else if (!haveHeader)
            {
                throw new InvalidDataException("The first chunk is not IHDR.");
            }
            else if (type.SequenceEqual("PLTE"u8))
            {
                palette = data.ToArray();
            }
            else if (type.SequenceEqual("tRNS"u8))
            {
                transparency = data.ToArray();
            }
            else if (type.SequenceEqual("IDAT"u8))
            {
                compressed.Write(data);
            }
            else if (type.SequenceEqual("IEND"u8))
            {
                break;
            }
            else if ((type[0] & 0x20) == 0)
            {
                throw new InvalidDataException($"Unknown critical chunk '{System.Text.Encoding.ASCII.GetString(type)}'.");
            }
        }

        if (compressed.Length == 0)
        {
            throw new InvalidDataException("The file has no image data.");
        }

        if (header.ColorType == 3 && palette is null)
        {
            throw new InvalidDataException("A palette image has no PLTE chunk.");
        }

        var raw = Inflate(compressed);
        var rgba = new byte[header.Width * header.Height * 4];
        if (header.Interlaced)
        {
            var position = 0;
            foreach (var (startX, startY, dx, dy) in Passes)
            {
                var passWidth = (header.Width - startX + dx - 1) / dx;
                var passHeight = (header.Height - startY + dy - 1) / dy;
                if (passWidth <= 0 || passHeight <= 0)
                {
                    continue;
                }

                position = Unfilter(raw, position, header, passWidth, passHeight, (x, y) => ((startY + (y * dy)) * header.Width) + startX + (x * dx), rgba, palette, transparency);
            }
        }
        else
        {
            Unfilter(raw, 0, header, header.Width, header.Height, (x, y) => (y * header.Width) + x, rgba, palette, transparency);
        }

        return new RgbaImage(header.Width, header.Height, rgba);
    }

    private readonly record struct Header(int Width, int Height, int BitDepth, int ColorType, bool Interlaced)
    {
        public int Channels => ColorType switch
        {
            0 or 3 => 1,
            2 => 3,
            4 => 2,
            _ => 4,
        };

        /// <summary>Bytes per complete pixel, at least one, which is what the filters step back by.</summary>
        public int FilterStride => Math.Max(1, Channels * BitDepth / 8);

        public int RowBytes(int width) => ((width * Channels * BitDepth) + 7) / 8;
    }

    private static Header ReadHeader(ReadOnlySpan<byte> data)
    {
        if (data.Length != 13)
        {
            throw new InvalidDataException("The IHDR chunk is the wrong size.");
        }

        var width = BinaryPrimitives.ReadUInt32BigEndian(data);
        var height = BinaryPrimitives.ReadUInt32BigEndian(data[4..]);
        if (width is 0 or > MaxSide || height is 0 or > MaxSide)
        {
            throw new InvalidDataException($"The image is {width} by {height}; each side must be 1 to {MaxSide}.");
        }

        int bitDepth = data[8];
        int colorType = data[9];
        var valid = colorType switch
        {
            0 => bitDepth is 1 or 2 or 4 or 8 or 16,
            3 => bitDepth is 1 or 2 or 4 or 8,
            2 or 4 or 6 => bitDepth is 8 or 16,
            _ => false,
        };
        if (!valid)
        {
            throw new InvalidDataException($"Colour type {colorType} with bit depth {bitDepth} is not a valid PNG format.");
        }

        if (data[10] != 0 || data[11] != 0 || data[12] > 1)
        {
            throw new InvalidDataException("The image uses an unknown compression, filter, or interlace method.");
        }

        return new Header((int)width, (int)height, bitDepth, colorType, data[12] == 1);
    }

    private static byte[] Inflate(MemoryStream compressed)
    {
        compressed.Position = 0;
        try
        {
            using var zlib = new ZLibStream(compressed, CompressionMode.Decompress);
            using var output = new MemoryStream();
            zlib.CopyTo(output);
            return output.ToArray();
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidDataException($"The image data is corrupt: {ex.Message}", ex);
        }
    }

    /// <summary>Reverses the row filters of one (sub)image and writes its pixels as RGBA. Returns where the next pass starts.</summary>
    private static int Unfilter(
        byte[] raw,
        int position,
        Header header,
        int width,
        int height,
        Func<int, int, int> target,
        byte[] rgba,
        byte[]? palette,
        byte[]? transparency)
    {
        var rowBytes = header.RowBytes(width);
        var stride = header.FilterStride;
        if (position + ((long)(rowBytes + 1) * height) > raw.Length)
        {
            throw new InvalidDataException("The image data is shorter than the image.");
        }

        var previous = new byte[rowBytes];
        var current = new byte[rowBytes];
        for (var y = 0; y < height; y++)
        {
            var filter = raw[position];
            Array.Copy(raw, position + 1, current, 0, rowBytes);
            position += rowBytes + 1;

            for (var i = 0; i < rowBytes; i++)
            {
                var left = i >= stride ? current[i - stride] : 0;
                var up = previous[i];
                var upLeft = i >= stride ? previous[i - stride] : 0;
                current[i] += filter switch
                {
                    0 => 0,
                    1 => (byte)left,
                    2 => up,
                    3 => (byte)((left + up) / 2),
                    4 => Paeth(left, up, upLeft),
                    _ => throw new InvalidDataException($"Row {y} uses unknown filter {filter}."),
                };
            }

            for (var x = 0; x < width; x++)
            {
                WritePixel(current, x, header, rgba.AsSpan(target(x, y) * 4, 4), palette, transparency);
            }

            (previous, current) = (current, previous);
        }

        return position;
    }

    private static byte Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        var pa = Math.Abs(p - a);
        var pb = Math.Abs(p - b);
        var pc = Math.Abs(p - c);
        return (byte)(pa <= pb && pa <= pc ? a : pb <= pc ? b : c);
    }

    private static void WritePixel(byte[] row, int x, Header header, Span<byte> pixel, byte[]? palette, byte[]? transparency)
    {
        switch (header.ColorType)
        {
            case 0:
            {
                var gray = Sample(row, x, 0, header);
                pixel[0] = pixel[1] = pixel[2] = To8(gray, header.BitDepth);
                pixel[3] = transparency is { Length: >= 2 } && gray == BinaryPrimitives.ReadUInt16BigEndian(transparency) ? (byte)0 : (byte)255;
                break;
            }

            case 2:
            {
                var r = Sample(row, x, 0, header);
                var g = Sample(row, x, 1, header);
                var b = Sample(row, x, 2, header);
                pixel[0] = To8(r, header.BitDepth);
                pixel[1] = To8(g, header.BitDepth);
                pixel[2] = To8(b, header.BitDepth);
                var keyed = transparency is { Length: >= 6 }
                    && r == BinaryPrimitives.ReadUInt16BigEndian(transparency)
                    && g == BinaryPrimitives.ReadUInt16BigEndian(transparency.AsSpan(2))
                    && b == BinaryPrimitives.ReadUInt16BigEndian(transparency.AsSpan(4));
                pixel[3] = keyed ? (byte)0 : (byte)255;
                break;
            }

            case 3:
            {
                var index = Sample(row, x, 0, header);
                if ((index * 3) + 2 >= palette!.Length)
                {
                    throw new InvalidDataException($"Palette index {index} is outside the palette.");
                }

                pixel[0] = palette[index * 3];
                pixel[1] = palette[(index * 3) + 1];
                pixel[2] = palette[(index * 3) + 2];
                pixel[3] = transparency is not null && index < transparency.Length ? transparency[index] : (byte)255;
                break;
            }

            case 4:
                pixel[0] = pixel[1] = pixel[2] = To8(Sample(row, x, 0, header), header.BitDepth);
                pixel[3] = To8(Sample(row, x, 1, header), header.BitDepth);
                break;

            default:
                for (var c = 0; c < 4; c++)
                {
                    pixel[c] = To8(Sample(row, x, c, header), header.BitDepth);
                }

                break;
        }
    }

    /// <summary>One channel of one pixel at the image's own bit depth.</summary>
    private static int Sample(byte[] row, int x, int channel, Header header)
    {
        var index = (x * header.Channels) + channel;
        return header.BitDepth switch
        {
            16 => BinaryPrimitives.ReadUInt16BigEndian(row.AsSpan(index * 2)),
            8 => row[index],
            _ => (row[index * header.BitDepth / 8] >> (8 - header.BitDepth - (index * header.BitDepth % 8))) & ((1 << header.BitDepth) - 1),
        };
    }

    private static byte To8(int value, int bitDepth) => bitDepth switch
    {
        16 => (byte)(value >> 8),
        8 => (byte)value,
        _ => (byte)(value * 255 / ((1 << bitDepth) - 1)),
    };
}
