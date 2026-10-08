using System.Globalization;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Exporters.Json;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Loggers;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Toolchains.InProcess.NoEmit;
using BenchmarkDotNet.Validators;

namespace Zombies.Benchmarks;

/// <summary>
/// Runs the benchmarks and fails when one is over its committed limit.
/// Usage: Benchmarks [--thresholds FILE] [--results DIR] [--check]. With <c>--check</c> nothing is run; the reports already in the results
/// directory are compared with the limits, which is how a limit change can be tried out without waiting for a run.
/// </summary>
internal static class BenchmarkRun
{
    private const string ReportSuffix = "-report-full.json";

    public static int Run(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var thresholds = Path.Combine(AppContext.BaseDirectory, "thresholds.json");
        var results = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "benchmarks");
        var checkOnly = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--thresholds" when i + 1 < args.Length: thresholds = args[++i]; break;
                case "--results" when i + 1 < args.Length: results = args[++i]; break;
                case "--check": checkOnly = true; break;
                default:
                    Console.Error.WriteLine($"Benchmarks: unknown argument '{args[i]}'.");
                    return 2;
            }
        }

        IReadOnlyDictionary<string, BenchmarkLimit> limits;
        try
        {
            limits = BudgetThresholds.ParseLimits(File.ReadAllText(thresholds));
        }
        catch (Exception ex) when (ex is IOException or FormatException or System.Text.Json.JsonException)
        {
            Console.Error.WriteLine($"Benchmarks: the thresholds file '{thresholds}' could not be used: {ex.Message}");
            return 2;
        }

        if (!checkOnly)
        {
            Measure(results);
        }

        return Check(limits, results);
    }

    private static void Measure(string results)
    {
        // A report left by an earlier run must not be mistaken for this one's.
        if (Directory.Exists(results))
        {
            foreach (var old in Directory.GetFiles(results, "*" + ReportSuffix, SearchOption.AllDirectories))
            {
                File.Delete(old);
            }
        }

        // In process, so a run does not rebuild a project for every benchmark; the optimizations validator refuses a Debug build,
        // since numbers from one would mean nothing.
        var config = ManualConfig.CreateEmpty()
            .AddLogger(ConsoleLogger.Default)
            .AddColumnProvider(BenchmarkDotNet.Columns.DefaultColumnProviders.Instance)
            .AddJob(Job.ShortRun.WithToolchain(InProcessNoEmitToolchain.Instance))
            .AddExporter(JsonExporter.Full)
            .AddValidator(JitOptimizationsValidator.FailOnError)
            .WithArtifactsPath(results);
        _ = BenchmarkSwitcher.FromAssembly(typeof(BenchmarkRun).Assembly).RunAllJoined(config);
    }

    private static int Check(IReadOnlyDictionary<string, BenchmarkLimit> limits, string results)
    {
        // One run writes one joined report, so the newest is the run being checked.
        var report = Directory.Exists(results)
            ? Directory.GetFiles(results, "*" + ReportSuffix, SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
            : null;
        if (report is null)
        {
            Console.Error.WriteLine($"Benchmarks: no '*{ReportSuffix}' report was found under '{results}'.");
            return 2;
        }

        var measured = BudgetThresholds.ParseReport(File.ReadAllText(report));
        foreach (var result in measured.OrderBy(r => r.Name, StringComparer.Ordinal))
        {
            var limit = limits.TryGetValue(result.Name, out var found) ? found.MaxMeanMicroseconds.ToString("F1", CultureInfo.InvariantCulture) : "none";
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {result.Name}: mean {result.MeanMicroseconds:F1} us (limit {limit} us), allocated {result.AllocatedBytes?.ToString(CultureInfo.InvariantCulture) ?? "unknown"} bytes"));
        }

        var problems = BudgetThresholds.Check(limits, measured);
        foreach (var problem in problems)
        {
            Console.Error.WriteLine($"Benchmarks: {problem}");
        }

        Console.WriteLine(problems.Count == 0 ? "Benchmarks: every benchmark is inside its limit." : $"Benchmarks: {problems.Count} problem(s).");
        return problems.Count == 0 ? 0 : 1;
    }
}