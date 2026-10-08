using System.Globalization;
using System.Text.Json;

namespace Zombies.Benchmarks;

/// <summary>The most a benchmark may take, in microseconds, and the most it may allocate per operation, in bytes.</summary>
internal sealed record BenchmarkLimit(double MaxMeanMicroseconds, long? MaxAllocatedBytes = null);

/// <summary>What one benchmark measured.</summary>
internal sealed record BenchmarkResult(string Name, double MeanMicroseconds, long? AllocatedBytes);

/// <summary>
/// Compares benchmark results with the committed limits. A benchmark over its limit, a result with no limit, and a limit with no result
/// are all failures, so a renamed or deleted benchmark cannot quietly stop being checked.
/// </summary>
internal static class BudgetThresholds
{
    private static readonly JsonSerializerOptions LimitOptions = new(JsonSerializerDefaults.Web) { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    public static IReadOnlyDictionary<string, BenchmarkLimit> ParseLimits(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        var raw = JsonSerializer.Deserialize<Dictionary<string, BenchmarkLimit>>(json, LimitOptions)
            ?? throw new FormatException("The thresholds file must be a JSON object.");
        foreach (var (name, limit) in raw)
        {
            if (!double.IsFinite(limit.MaxMeanMicroseconds) || limit.MaxMeanMicroseconds <= 0)
            {
                throw new FormatException($"The limit for '{name}' needs a positive maxMeanMicroseconds.");
            }

            if (limit.MaxAllocatedBytes is < 0)
            {
                throw new FormatException($"The limit for '{name}' has a negative maxAllocatedBytes.");
            }
        }

        return raw;
    }

    /// <summary>Reads the means and allocations out of a BenchmarkDotNet <c>-report-full.json</c> file. BenchmarkDotNet reports nanoseconds.</summary>
    public static IReadOnlyList<BenchmarkResult> ParseReport(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        using var document = JsonDocument.Parse(json);
        var results = new List<BenchmarkResult>();
        foreach (var benchmark in document.RootElement.GetProperty("Benchmarks").EnumerateArray())
        {
            var name = benchmark.GetProperty("Method").GetString() ?? throw new FormatException("A benchmark has no method name.");
            var type = benchmark.TryGetProperty("Type", out var typeElement) ? typeElement.GetString() : null;
            var mean = benchmark.GetProperty("Statistics").GetProperty("Mean").GetDouble();
            long? allocated = benchmark.TryGetProperty("Memory", out var memory) && memory.TryGetProperty("BytesAllocatedPerOperation", out var bytes)
                ? bytes.GetInt64()
                : null;
            var parameters = benchmark.TryGetProperty("Parameters", out var parameterElement) ? parameterElement.GetString() : null;
            var fullName = type is null ? name : $"{type}.{name}";
            results.Add(new BenchmarkResult(string.IsNullOrEmpty(parameters) ? fullName : $"{fullName}({parameters})", mean / 1000.0, allocated));
        }

        return results;
    }

    /// <returns>One message per problem, or none when every benchmark is inside its limit.</returns>
    public static IReadOnlyList<string> Check(IReadOnlyDictionary<string, BenchmarkLimit> limits, IReadOnlyList<BenchmarkResult> results)
    {
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(results);
        var problems = new List<string>();
        foreach (var result in results)
        {
            if (!limits.TryGetValue(result.Name, out var limit))
            {
                problems.Add($"{result.Name} has no limit in the thresholds file.");
                continue;
            }

            if (result.MeanMicroseconds > limit.MaxMeanMicroseconds)
            {
                problems.Add(string.Create(CultureInfo.InvariantCulture, $"{result.Name} took {result.MeanMicroseconds:F1} us on average, over its limit of {limit.MaxMeanMicroseconds:F1} us."));
            }

            if (limit.MaxAllocatedBytes is { } maxBytes && result.AllocatedBytes is { } allocated && allocated > maxBytes)
            {
                problems.Add($"{result.Name} allocated {allocated} bytes per operation, over its limit of {maxBytes}.");
            }
        }

        foreach (var name in limits.Keys.Where(n => results.All(r => r.Name != n)))
        {
            problems.Add($"{name} has a limit but was not measured.");
        }

        return problems;
    }
}