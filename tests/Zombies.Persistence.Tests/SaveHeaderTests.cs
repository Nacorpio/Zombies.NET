using Zombies.Persistence.Sqlite;

namespace Zombies.Persistence.Tests;

public sealed class SaveHeaderTests
{
    private static readonly SavedMod Base = new("base", "0.1.0", "aaaa");
    private static readonly SavedMod Sample = new("sample_data", "1.0.0", "bbbb");

    private static SaveHeader Header() => new(7, 2, [Base, Sample]);

    [Fact]
    public void Load_OnAFreshSave_ReturnsNull()
    {
        using var save = new TempSave();
        using var database = save.Open();

        Assert.Null(new SqliteSaveHeaderRepository(database).Load());
    }

    [Fact]
    public void Write_ThenLoad_RoundTripsSeedGeneratorAndModsInOrder()
    {
        using var save = new TempSave();
        using var database = save.Open();
        var repository = new SqliteSaveHeaderRepository(database);

        repository.Write(new SaveHeader(ulong.MaxValue, 5, [Sample, Base]));
        var loaded = repository.Load()!;

        Assert.Equal(ulong.MaxValue, loaded.WorldSeed);
        Assert.Equal(5, loaded.GeneratorVersion);
        Assert.Equal([Sample, Base], loaded.Mods);
    }

    [Fact]
    public void Write_Twice_ReplacesTheEarlierHeader()
    {
        using var save = new TempSave();
        using var database = save.Open();
        var repository = new SqliteSaveHeaderRepository(database);
        repository.Write(Header());

        repository.Write(new SaveHeader(9, 3, [Base]));

        var loaded = repository.Load()!;
        Assert.Equal(9UL, loaded.WorldSeed);
        Assert.Equal([Base], loaded.Mods);
    }

    [Fact]
    public void CheckAgainst_TheSameModsAndGenerator_FindsNoProblem()
    {
        Assert.Empty(Header().CheckAgainst(2, [Sample, Base]));
    }

    [Fact]
    public void CheckAgainst_AChangedModVersion_NamesTheMod()
    {
        var problem = Assert.Single(Header().CheckAgainst(2, [Base with { Version = "0.2.0" }, Sample]));

        Assert.Contains("'base'", problem, StringComparison.Ordinal);
        Assert.Contains("0.1.0", problem, StringComparison.Ordinal);
        Assert.Contains("0.2.0", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void CheckAgainst_ChangedModContent_IsDetectedEvenAtTheSameVersion()
    {
        var problem = Assert.Single(Header().CheckAgainst(2, [Base, Sample with { ContentHash = "cccc" }]));

        Assert.Contains("'sample_data'", problem, StringComparison.Ordinal);
        Assert.Contains("different content", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void CheckAgainst_AMissingMod_IsReported()
    {
        var problem = Assert.Single(Header().CheckAgainst(2, [Base]));

        Assert.Contains("'sample_data'", problem, StringComparison.Ordinal);
        Assert.Contains("not loaded", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void CheckAgainst_AnExtraMod_IsReported()
    {
        var problem = Assert.Single(Header().CheckAgainst(2, [Base, Sample, new SavedMod("zed", "1.0.0", "dddd")]));

        Assert.Contains("'zed'", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void CheckAgainst_ADifferentGeneratorVersion_IsReported()
    {
        var problem = Assert.Single(Header().CheckAgainst(3, [Base, Sample]));

        Assert.Contains("world generator", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void CheckAgainst_SeveralProblems_ReportsEveryOne()
    {
        Assert.Equal(3, Header().CheckAgainst(3, [Base with { Version = "9" }]).Count);
    }
}
