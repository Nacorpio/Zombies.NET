using Zombies.Domain.Items;
using Zombies.Domain.Mods;
using Zombies.Engine.Core.Modding;

namespace Zombies.Engine.Tests;

/// <summary>Loads the real mods in the repository through the same path the game and SimHarness use.</summary>
public sealed class ModLoadingTests
{
    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Zombies.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Could not find the repository root.");
    }

    private static ModLoadResult LoadRepositoryMods() => ModLoader.Load(DirectoryModSource.Read(Path.Combine(RepoRoot(), "mods")));

    [Fact]
    public void BaseAndSampleDataMod_LoadThroughOneCodePath()
    {
        var result = LoadRepositoryMods();

        Assert.True(result.IsSuccess, string.Join(Environment.NewLine, result.Errors));
        Assert.Equal(["base", "sample_data"], result.Mods.Select(m => m.Manifest.Id));
        Assert.Equal(4, result.Registry.Count);
    }

    [Fact]
    public void SampleDataMod_AddsAnItemAndPatchesABaseItem()
    {
        var registry = LoadRepositoryMods().Registry;

        var items = registry.OfKind("item").ToDictionary(d => d.Id.Value);
        var beans = ItemDefinitionJson.Parse(items["base:item/canned_beans"].Json);

        Assert.Equal(12, beans.MaxStack);
        Assert.Equal(0.4, beans.UnitMass.Kilograms, 9);
        Assert.Equal(["sample_data"], items["base:item/canned_beans"].ModifiedBy);
        Assert.Equal("sample_data", items["sample_data:item/protein_bar"].DefinedBy);
    }

    [Fact]
    public void EveryLoadedItemParsesIntoAnItemDefinition()
    {
        var registry = LoadRepositoryMods().Registry;

        var catalog = new ItemCatalog(registry.OfKind("item").Select(d => ItemDefinitionJson.Parse(d.Json)));

        Assert.True(catalog.TryGet(new ItemId("base:item/bandage"), out _));
    }

    [Fact]
    public void GeneratedSchemas_MatchTheFilesCommittedForEditors()
    {
        foreach (var (kind, generated) in DefinitionSchemas.Generate())
        {
            var path = Path.Combine(RepoRoot(), "schemas", $"{kind}.schema.json");
            Assert.True(File.Exists(path), $"Missing {path}. Run: dotnet run --project tools/SimHarness -- schemas schemas");

            var committed = File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal);
            Assert.Equal(generated, committed);
        }
    }

    [Fact]
    public void MissingModsFolder_IsReportedClearly()
    {
        Assert.Throws<DirectoryNotFoundException>(() => DirectoryModSource.Read(Path.Combine(RepoRoot(), "no_such_mods_folder")));
    }
}
