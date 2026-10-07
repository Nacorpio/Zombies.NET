using System.Buffers.Binary;
using System.Text.Json.Nodes;

namespace Zombies.ContentJudge.SystemOne;

/// <summary>
/// An image embedded in a Clef Flash request: always the bytes themselves, never a URL. It goes on the wire as a
/// base64 data URL (<c>data:image/png;base64,…</c>), the first of the two forms the Clef input schema accepts.
/// </summary>
public sealed record JudgeImage(string ContentType, byte[] Bytes)
{
    public const string Png = "image/png";
    public const string Jpeg = "image/jpeg";
    public const string WebP = "image/webp";

    public static readonly IReadOnlyList<string> SupportedTypes = [Png, Jpeg, WebP];

    /// <summary>Makes an image from its bytes, detecting PNG, JPEG, or WebP from the file signature.</summary>
    /// <exception cref="FormatException">The bytes are not a PNG, JPEG, or WebP file.</exception>
    public static JudgeImage FromBytes(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        return new JudgeImage(DetectContentType(bytes) ?? throw new FormatException("The image is not a PNG, JPEG, or WebP file."), bytes);
    }

    public string ToDataUrl() => $"data:{ContentType};base64,{Convert.ToBase64String(Bytes)}";

    public JsonNode ToJson() => ToDataUrl();

    /// <summary>Reads either form the Clef input schema accepts: a base64 data URL, or <c>{ "content_type", "base64" }</c>. Remote URLs are refused.</summary>
    /// <exception cref="FormatException">The entry is a URL or not a base64 image.</exception>
    public static JudgeImage FromJson(JsonNode? node)
    {
        if (node is JsonObject json)
        {
            return new JudgeImage(Wire.RequiredString(json, "content_type"), Decode(Wire.RequiredString(json, "base64")));
        }

        if (node is not JsonValue value || !value.TryGetValue<string>(out var text))
        {
            throw new FormatException("An image must be a data URL string or an object with 'content_type' and 'base64'.");
        }

        if (!text.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            throw new FormatException("Image URLs are not accepted; embed the image as a base64 data URL.");
        }

        var comma = text.IndexOf(',', StringComparison.Ordinal);
        const string Marker = ";base64";
        if (comma < 0 || !text[..comma].EndsWith(Marker, StringComparison.OrdinalIgnoreCase))
        {
            throw new FormatException("An image data URL must be base64 encoded.");
        }

        return new JudgeImage(text[5..(comma - Marker.Length)], Decode(text[(comma + 1)..]));
    }

    private static byte[] Decode(string base64)
    {
        try
        {
            return Convert.FromBase64String(base64);
        }
        catch (FormatException ex)
        {
            throw new FormatException("An image is not valid base64.", ex);
        }
    }

    public static string? DetectContentType(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 8 && bytes[..8].SequenceEqual((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]))
        {
            return Png;
        }

        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
        {
            return Jpeg;
        }

        if (bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes[8..12].SequenceEqual("WEBP"u8))
        {
            return WebP;
        }

        return null;
    }

    /// <summary>Reads width and height from a PNG, JPEG, or WebP header without decoding the image.</summary>
    public static bool TryReadSize(ReadOnlySpan<byte> bytes, out int width, out int height)
    {
        width = height = 0;
        switch (DetectContentType(bytes))
        {
            case Png when bytes.Length >= 24:
                width = BinaryPrimitives.ReadInt32BigEndian(bytes[16..]);
                height = BinaryPrimitives.ReadInt32BigEndian(bytes[20..]);
                return width > 0 && height > 0;
            case Jpeg:
                return TryReadJpegSize(bytes, out width, out height);
            case WebP:
                return TryReadWebPSize(bytes, out width, out height);
            default:
                return false;
        }
    }

    private static bool TryReadJpegSize(ReadOnlySpan<byte> bytes, out int width, out int height)
    {
        width = height = 0;
        var i = 2;
        while (i + 4 <= bytes.Length)
        {
            if (bytes[i] != 0xFF)
            {
                return false;
            }

            var marker = bytes[i + 1];
            if (marker == 0xFF)
            {
                i++;
                continue;
            }

            if (marker is 0xD8 or 0x01 or (>= 0xD0 and <= 0xD7))
            {
                i += 2;
                continue;
            }

            var length = BinaryPrimitives.ReadUInt16BigEndian(bytes[(i + 2)..]);
            var isFrame = marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC;
            if (isFrame && i + 9 <= bytes.Length)
            {
                height = BinaryPrimitives.ReadUInt16BigEndian(bytes[(i + 5)..]);
                width = BinaryPrimitives.ReadUInt16BigEndian(bytes[(i + 7)..]);
                return width > 0 && height > 0;
            }

            i += 2 + length;
        }

        return false;
    }

    private static bool TryReadWebPSize(ReadOnlySpan<byte> bytes, out int width, out int height)
    {
        width = height = 0;
        if (bytes.Length < 30)
        {
            return false;
        }

        var chunk = bytes[12..16];
        if (chunk.SequenceEqual("VP8X"u8))
        {
            width = 1 + (bytes[24] | (bytes[25] << 8) | (bytes[26] << 16));
            height = 1 + (bytes[27] | (bytes[28] << 8) | (bytes[29] << 16));
        }
        else if (chunk.SequenceEqual("VP8L"u8))
        {
            var bits = BinaryPrimitives.ReadUInt32LittleEndian(bytes[21..]);
            width = (int)(bits & 0x3FFF) + 1;
            height = (int)((bits >> 14) & 0x3FFF) + 1;
        }
        else if (chunk.SequenceEqual("VP8 "u8))
        {
            width = BinaryPrimitives.ReadUInt16LittleEndian(bytes[26..]) & 0x3FFF;
            height = BinaryPrimitives.ReadUInt16LittleEndian(bytes[28..]) & 0x3FFF;
        }

        return width > 0 && height > 0;
    }
}
