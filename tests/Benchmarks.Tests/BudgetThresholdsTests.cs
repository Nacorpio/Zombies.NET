using Zombies.Benchmarks;

namespace Zombies.Benchmarks.Tests;

public sealed class BudgetThresholdsTests
{
    private static Dictionary<string, BenchmarkLimit> Limits(params (string Name, double Micros, long? Bytes)[] limits) =>
        limits.ToDictionary(l => l.Name, l => new BenchmarkLimit(l.Micros, l.Bytes));

    // The shape BenchmarkDotNet writes: nanoseconds, the class and method separately, and the parameters as one string.
    private const string Report = """
        { "Title": "Zombies.Benchmarks-20261008", "Benchmarks": [
          { "Type": "ChunkBenchmarks", "Method": "LightAndMeshChunk", "Parameters": "",
            "Statistics": { "Mean": 1800000.0 }, "Memory": { "BytesAllocatedPerOperation": 4096 } },
          { "Type": "EcsBenchmarks", "Method": "QueryOneComponent", "Parameters": "Entities=200",
            "Statistics": { "Mean": 2500.5 }, "Memory": { "BytesAllocatedPerOperation": 0 } },
          { "Type": "EcsBenchmarks", "Method": "QueryOneComponent", "Parameters": "Entities=2000",
            "Statistics": { "Mean": 25000.0 } }
        ] }
        """;

    [Fact]
    public void ParseReport_ConvertsNanosecondsToMicroseconds_AndNamesEachBenchmarkWithItsParameters()
    {
        var results = BudgetThresholds.ParseReport(Report);

        Assert.Equal(
            ["ChunkBenchmarks.LightAndMeshChunk", "EcsBenchmarks.QueryOneComponent(Entities=200)", "EcsBenchmarks.QueryOneComponent(Entities=2000)"],
            results.Select(r => r.Name));
        Assert.Equal(1800.0, results[0].MeanMicroseconds, 6);
        Assert.Equal(2.5005, results[1].MeanMicroseconds, 6);
        Assert.Equal([4096L, 0L, null], results.Select(r => r.AllocatedBytes));
    }

    [Fact]
    public void Check_PassesWhenEveryBenchmarkIsInsideItsLimit()
    {
        var results = BudgetThresholds.ParseReport(Report);
        var limits = Limits(
            ("ChunkBenchmarks.LightAndMeshChunk", 2000, 8192),
            ("EcsBenchmarks.QueryOneComponent(Entities=200)", 10, 0),
            ("EcsBenchmarks.QueryOneComponent(Entities=2000)", 100, null));

        Assert.Empty(BudgetThresholds.Check(limits, results));
    }

    [Fact]
    public void Check_FailsWhenABenchmarkIsSlowerThanItsLimit()
    {
        var limits = Limits(
            ("ChunkBenchmarks.LightAndMeshChunk", 1500, null),
            ("EcsBenchmarks.QueryOneComponent(Entities=200)", 10, null),
            ("EcsBenchmarks.QueryOneComponent(Entities=2000)", 100, null));

        var problem = Assert.Single(BudgetThresholds.Check(limits, BudgetThresholds.ParseReport(Report)));

        Assert.Contains("ChunkBenchmarks.LightAndMeshChunk", problem, StringComparison.Ordinal);
        Assert.Contains("1800.0", problem, StringComparison.Ordinal);
        Assert.Contains("1500.0", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Check_FailsWhenABenchmarkAllocatesMoreThanItsLimit()
    {
        var limits = Limits(
            ("ChunkBenchmarks.LightAndMeshChunk", 2000, 1024),
            ("EcsBenchmarks.QueryOneComponent(Entities=200)", 10, 0),
            ("EcsBenchmarks.QueryOneComponent(Entities=2000)", 100, null));

        var problem = Assert.Single(BudgetThresholds.Check(limits, BudgetThresholds.ParseReport(Report)));

        Assert.Contains("allocated 4096", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Check_AtExactlyTheLimit_Passes()
    {
        var results = new[] { new BenchmarkResult("A.B", 100, 64) };

        Assert.Empty(BudgetThresholds.Check(Limits(("A.B", 100, 64)), results));
    }

    [Fact]
    public void Check_FailsForABenchmarkWithNoLimit_SoANewOneCannotSkipTheGate()
    {
        var results = new[] { new BenchmarkResult("A.New", 1, 0) };

        var problem = Assert.Single(BudgetThresholds.Check(Limits(), results));

        Assert.Contains("A.New has no limit", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Check_FailsForALimitThatWasNotMeasured_SoARenamedBenchmarkCannotSkipTheGate()
    {
        var problem = Assert.Single(BudgetThresholds.Check(Limits(("A.Gone", 100, null)), []));

        Assert.Contains("A.Gone has a limit but was not measured", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Check_ReportsEveryProblemAtOnce()
    {
        var results = new[] { new BenchmarkResult("A.Slow", 500, 10), new BenchmarkResult("A.Unlisted", 1, 0) };

        var problems = BudgetThresholds.Check(Limits(("A.Slow", 100, 5), ("A.Missing", 100, null)), results);

        Assert.Equal(4, problems.Count);
    }

    [Fact]
    public void ParseLimits_ReadsCommentsAndOptionalAllocationLimits()
    {
        var limits = BudgetThresholds.ParseLimits("""
            {
              // chunk meshing, against its 2 ms budget
              "A.B": { "maxMeanMicroseconds": 2000, "maxAllocatedBytes": 0 },
              "A.C": { "maxMeanMicroseconds": 5.5 }
            }
            """);

        Assert.Equal(new BenchmarkLimit(2000, 0), limits["A.B"]);
        Assert.Equal(new BenchmarkLimit(5.5), limits["A.C"]);
    }

    [Theory]
    [InlineData("""{ "A.B": { "maxMeanMicroseconds": 0 } }""")]
    [InlineData("""{ "A.B": { "maxMeanMicroseconds": -5 } }""")]
    [InlineData("""{ "A.B": { "maxMeanMicroseconds": 10, "maxAllocatedBytes": -1 } }""")]
    public void ParseLimits_RejectsLimitsThatCouldNeverBeMet(string json)
    {
        Assert.Throws<FormatException>(() => BudgetThresholds.ParseLimits(json));
    }
}