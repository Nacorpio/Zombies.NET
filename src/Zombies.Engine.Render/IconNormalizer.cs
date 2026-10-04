namespace Zombies.Engine.Render;

/// <summary>
/// How loaded images are turned into icons. Icons are coverage masks that a tint colour draws in, so the image's
/// colours only decide shading: the brightest part of an icon draws at full strength and the darkest at <see cref="MinTone"/>.
/// </summary>
public sealed record IconStyle
{
    public static IconStyle Default { get; } = new();

    /// <summary>Alpha at or above this is part of the icon; below it is empty. There is nothing in between, so edges stay hard.</summary>
    public byte AlphaThreshold { get; init; } = 128;

    /// <summary>The weakest tone a visible pixel can have, so dark detail still shows when tinted.</summary>
    public byte MinTone { get; init; } = 96;

    /// <summary>When set, every visible pixel snaps to the nearest of these tones, so icons from different sources share one look.</summary>
    public IReadOnlyList<byte>? Palette { get; init; }

    /// <summary>
    /// How far, as a distance in RGB, a pixel of a fully opaque image may be from the corner colour and still count as
    /// background. Generated images often have a flat background instead of transparency.
    /// </summary>
    public int BackgroundTolerance { get; init; } = 48;
}

/// <summary>
/// Turns any image into a crisp <see cref="IconSet.Size"/> square coverage mask: background removed, fitted and centred,
/// resampled by area so detail is kept, then alpha made hard so no soft halo is left around the art.
/// </summary>
public static class IconNormalizer
{
    /// <returns><see cref="IconSet.Size"/> squared bytes, row by row: 0 is empty, anything else is a visible tone.</returns>
    public static byte[] Normalize(RgbaImage image, IconStyle? style = null)
    {
        ArgumentNullException.ThrowIfNull(image);
        style ??= IconStyle.Default;

        var premultiplied = Premultiply(image, style);
        var (red, green, blue, alpha) = Resample(premultiplied, image.Width, image.Height);
        const int size = IconSet.Size;

        // Shade from brightness of the un-premultiplied colour, so a soft edge does not darken into a halo.
        var lumas = new float[size * size];
        var visible = new bool[size * size];
        float low = float.MaxValue, high = float.MinValue;
        for (var i = 0; i < lumas.Length; i++)
        {
            if (alpha[i] * 255f < style.AlphaThreshold || alpha[i] <= 0f)
            {
                continue;
            }

            visible[i] = true;
            lumas[i] = ((0.299f * red[i]) + (0.587f * green[i]) + (0.114f * blue[i])) / alpha[i];
            low = Math.Min(low, lumas[i]);
            high = Math.Max(high, lumas[i]);
        }

        var mask = new byte[size * size];
        for (var i = 0; i < mask.Length; i++)
        {
            if (!visible[i])
            {
                continue;
            }

            // A flat icon has no shading to keep; it draws at full strength.
            var level = high - low < 1f / 64f ? 1f : (lumas[i] - low) / (high - low);
            var tone = (byte)Math.Round(style.MinTone + (level * (255 - style.MinTone)));
            mask[i] = Math.Max((byte)1, style.Palette is { Count: > 0 } palette ? Nearest(palette, tone) : tone);
        }

        return mask;
    }

    /// <summary>RGBA as premultiplied floats in 0 to 1, with a flat background keyed out when the image has no transparency.</summary>
    private static float[] Premultiply(RgbaImage image, IconStyle style)
    {
        var source = image.Pixels;
        var background = FlatBackground(image, style);
        var result = new float[source.Length];
        for (var i = 0; i < source.Length; i += 4)
        {
            var a = source[i + 3] / 255f;
            if (background is { } bg && Distance(source.AsSpan(i, 3), bg) <= style.BackgroundTolerance)
            {
                a = 0f;
            }

            result[i] = source[i] / 255f * a;
            result[i + 1] = source[i + 1] / 255f * a;
            result[i + 2] = source[i + 2] / 255f * a;
            result[i + 3] = a;
        }

        return result;
    }

    /// <summary>The background colour of a fully opaque image whose four corners agree, or null when there is nothing to key out.</summary>
    private static (byte R, byte G, byte B)? FlatBackground(RgbaImage image, IconStyle style)
    {
        var pixels = image.Pixels;
        for (var i = 3; i < pixels.Length; i += 4)
        {
            if (pixels[i] != 255)
            {
                return null;
            }
        }

        int[] corners = [0, image.Width - 1, (image.Height - 1) * image.Width, (image.Height * image.Width) - 1];
        var first = (pixels[0], pixels[1], pixels[2]);
        foreach (var corner in corners)
        {
            if (Distance(pixels.AsSpan(corner * 4, 3), first) > style.BackgroundTolerance)
            {
                return null;
            }
        }

        return first;
    }

    private static int Distance(ReadOnlySpan<byte> rgb, (byte R, byte G, byte B) other)
    {
        var dr = rgb[0] - other.R;
        var dg = rgb[1] - other.G;
        var db = rgb[2] - other.B;
        return (int)Math.Sqrt((dr * dr) + (dg * dg) + (db * db));
    }

    /// <summary>
    /// Fits the image into a centred square and averages the source area under each icon pixel. A whole-number enlargement,
    /// such as 16 to 32, comes out exactly as nearest neighbour.
    /// </summary>
    private static (float[] R, float[] G, float[] B, float[] A) Resample(float[] source, int width, int height)
    {
        const int size = IconSet.Size;
        var side = Math.Max(width, height);
        var offsetX = (side - width) / 2.0;
        var offsetY = (side - height) / 2.0;
        var scale = side / (double)size;

        var r = new float[size * size];
        var g = new float[size * size];
        var b = new float[size * size];
        var a = new float[size * size];
        for (var y = 0; y < size; y++)
        {
            var y0 = (y * scale) - offsetY;
            var y1 = ((y + 1) * scale) - offsetY;
            for (var x = 0; x < size; x++)
            {
                var x0 = (x * scale) - offsetX;
                var x1 = ((x + 1) * scale) - offsetX;
                double sr = 0, sg = 0, sb = 0, sa = 0;
                for (var sy = Math.Max(0, (int)Math.Floor(y0)); sy < Math.Min(height, (int)Math.Ceiling(y1)); sy++)
                {
                    var wy = Math.Min(y1, sy + 1) - Math.Max(y0, sy);
                    for (var sx = Math.Max(0, (int)Math.Floor(x0)); sx < Math.Min(width, (int)Math.Ceiling(x1)); sx++)
                    {
                        var w = wy * (Math.Min(x1, sx + 1) - Math.Max(x0, sx));
                        var i = ((sy * width) + sx) * 4;
                        sr += source[i] * w;
                        sg += source[i + 1] * w;
                        sb += source[i + 2] * w;
                        sa += source[i + 3] * w;
                    }
                }

                // Area outside the source counts as empty, which is what padding a wide or tall image means.
                var area = scale * scale;
                var o = (y * size) + x;
                r[o] = (float)(sr / area);
                g[o] = (float)(sg / area);
                b[o] = (float)(sb / area);
                a[o] = (float)(sa / area);
            }
        }

        return (r, g, b, a);
    }

    private static byte Nearest(IReadOnlyList<byte> palette, byte tone)
    {
        var best = palette[0];
        foreach (var candidate in palette)
        {
            if (Math.Abs(candidate - tone) < Math.Abs(best - tone))
            {
                best = candidate;
            }
        }

        return best;
    }
}
