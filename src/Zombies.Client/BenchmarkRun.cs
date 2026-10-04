using System.Globalization;
using System.Numerics;
using Zombies.Engine.Render;

namespace Zombies.Client;

/// <summary>
/// A repeatable performance run: wait until the world around the start has loaded, then fly a fixed route for a fixed time, so chunks keep
/// streaming in and out, and record how long each frame took. Reports the average and the slow tail, because stutter hides inside an average.
/// </summary>
internal sealed class BenchmarkRun
{
    private const float FlightSeconds = 15f;
    private const float FlightSpeed = 40f;
    private const float TurnRate = 0.25f;

    private readonly List<double> _frameMilliseconds = [];
    private readonly List<int> _collectionsAtFrame = [];
    private readonly List<(double Update, double Render)> _phases = [];
    private float _flown;
    private int _settled;

    public bool Flying { get; private set; }

    public bool Finished { get; private set; }

    public void Observe(int frame, bool worldLoaded, float seconds, double updateMilliseconds, double renderMilliseconds)
    {
        _ = frame;
        if (!Flying)
        {
            _settled = worldLoaded ? _settled + 1 : 0;
            Flying = _settled >= 30;
            return;
        }

        _frameMilliseconds.Add(seconds * 1000.0);
        _collectionsAtFrame.Add(GC.CollectionCount(2));
        _phases.Add((updateMilliseconds, renderMilliseconds));
    }

    public void Advance(Camera camera, float seconds)
    {
        camera.Yaw += TurnRate * seconds;
        camera.Pitch = -0.30f;
        camera.Position += camera.Forward * new Vector3(1, 0, 1) * FlightSpeed * seconds;
        camera.Position = new Vector3(camera.Position.X, 84f, camera.Position.Z);
        _flown += seconds;
        Finished = _flown >= FlightSeconds;
    }

    public int Report(double budgetMilliseconds)
    {
        if (_frameMilliseconds.Count < 30)
        {
            Console.Error.WriteLine("BENCH FAILED: too few frames were measured.");
            return 1;
        }

        var sorted = _frameMilliseconds.Order().ToArray();
        double Percentile(double p) => sorted[Math.Min(sorted.Length - 1, (int)(sorted.Length * p))];
        var average = sorted.Average();

        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"BENCH: {sorted.Length} frames over {_flown:F1} s flying {FlightSpeed} blocks/s"));
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"  frame time: avg {average:F2} ms ({1000 / average:F0} fps), p50 {Percentile(0.50):F2}, p95 {Percentile(0.95):F2}, p99 {Percentile(0.99):F2}, max {sorted[^1]:F2} ms"));

        var spikes = sorted.Count(ms => ms > budgetMilliseconds * 2);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  budget {budgetMilliseconds:F1} ms: {spikes} frames took more than double"));

        // Say where the slowest frames happened and whether a full garbage collection ran in that frame, to tell stutter causes apart.
        var worst = Enumerable.Range(0, _frameMilliseconds.Count).OrderByDescending(i => _frameMilliseconds[i]).Take(5).OrderBy(i => i);
        foreach (var i in worst)
        {
            var elapsed = _frameMilliseconds.Take(i + 1).Sum() / 1000.0;
            var collected = i > 0 && _collectionsAtFrame[i] != _collectionsAtFrame[i - 1];
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  slow frame #{i} at {elapsed:F2} s: {_frameMilliseconds[i]:F2} ms (stream+upload {_phases[i].Update:F2}, render {_phases[i].Render:F2}){(collected ? " (a gen 2 GC ran)" : string.Empty)}"));
        }

        if (average > budgetMilliseconds)
        {
            Console.Error.WriteLine("BENCH OVER BUDGET");
            return 1;
        }

        Console.WriteLine("BENCH OK");
        return 0;
    }
}
