using System.Globalization;

namespace Zombies.Engine.Render;

/// <summary>Rolling frame-time statistics over the most recent frames.</summary>
public sealed class FrameStats(int window = 120)
{
    private readonly double[] _times = new double[window > 0 ? window : throw new ArgumentOutOfRangeException(nameof(window))];
    private int _next;
    private int _filled;

    public int Samples => _filled;

    /// <summary>Records how long the last frame took, in seconds.</summary>
    public void Add(double seconds)
    {
        _times[_next] = seconds;
        _next = (_next + 1) % _times.Length;
        _filled = Math.Min(_filled + 1, _times.Length);
    }

    public double AverageMilliseconds => _filled == 0 ? 0 : _times.Take(_filled).Average() * 1000;

    public double MinMilliseconds => _filled == 0 ? 0 : _times.Take(_filled).Min() * 1000;

    public double MaxMilliseconds => _filled == 0 ? 0 : _times.Take(_filled).Max() * 1000;

    public double Fps => AverageMilliseconds <= 0 ? 0 : 1000 / AverageMilliseconds;

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{AverageMilliseconds:F2} ms ({Fps:F0} fps)");
}

/// <summary>Draws frame time and device info in a corner of the screen.</summary>
public static class DebugOverlay
{
    private static readonly Rgba Panel = new(0, 0, 0, 170);
    private static readonly Rgba Text = new(235, 235, 235);
    private static readonly Rgba Good = new(120, 230, 120);
    private static readonly Rgba Warn = new(240, 200, 80);
    private static readonly Rgba Bad = new(240, 100, 100);

    /// <summary>Frame time colour: green at 60 fps or better, amber down to 30, red below.</summary>
    public static Rgba ColorFor(double averageMilliseconds) => averageMilliseconds <= 16.7 ? Good : averageMilliseconds <= 33.4 ? Warn : Bad;

    public static string[] Lines(FrameStats stats, string deviceName, int width, int height) =>
    [
        string.Create(CultureInfo.InvariantCulture, $"FRAME {stats.AverageMilliseconds:F2} MS"),
        string.Create(CultureInfo.InvariantCulture, $"{stats.Fps:F0} FPS  MIN {stats.MinMilliseconds:F1}  MAX {stats.MaxMilliseconds:F1}"),
        deviceName.ToUpperInvariant(),
        string.Create(CultureInfo.InvariantCulture, $"{width}X{height}"),
    ];

    public static void Draw(SpriteBatch batch, FrameStats stats, string deviceName, int width, int height, int scale = 2)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(stats);
        ArgumentNullException.ThrowIfNull(deviceName);

        var lines = Lines(stats, deviceName, width, height);
        var padding = 4 * scale;
        var textWidth = lines.Max(l => DebugFont.MeasureWidth(l, scale));
        batch.FillRect(padding, padding, textWidth + (2 * padding), (lines.Length * DebugFont.LineHeight * scale) + (2 * padding), Panel);

        for (var i = 0; i < lines.Length; i++)
        {
            batch.DrawText(lines[i], 2 * padding, padding + padding + (i * DebugFont.LineHeight * scale), scale, i == 0 ? ColorFor(stats.AverageMilliseconds) : Text);
        }
    }
}
