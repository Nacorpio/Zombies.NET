using UnitsNet;
using Zombies.Domain.Crafting;
using Zombies.Domain.Inventory;
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
        Assert.Equal(8, result.Registry.Count);
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

    private static (ItemCatalog Items, LootTableCatalog Loot) LoadCatalogs()
    {
        var registry = LoadRepositoryMods().Registry;
        var items = new ItemCatalog(registry.OfKind("item").Select(d => ItemDefinitionJson.Parse(d.Json)));
        var loot = new LootTableCatalog(registry.OfKind("loot").Select(d => LootTableJson.Parse(d.Json)));
        return (items, loot);
    }

    [Fact]
    public void SampleDataMod_PatchesABaseLootTableAndAddsItsOwn()
    {
        var registry = LoadRepositoryMods().Registry;

        var kitchen = LootTableJson.Parse(registry.OfKind("loot").Single(d => d.Id.Value == "base:loot/kitchen").Json);

        Assert.Equal((2, 4), (kitchen.MinRolls, kitchen.MaxRolls));
        Assert.Equal(["sample_data"], registry.OfKind("loot").Single(d => d.Id.Value == "base:loot/kitchen").ModifiedBy);
        Assert.Equal("sample_data", registry.OfKind("loot").Single(d => d.Id.Value == "sample_data:loot/gym_locker").DefinedBy);
    }

    [Fact]
    public void EveryLootEntryInTheLoadedModsNamesALoadedItem()
    {
        var (items, loot) = LoadCatalogs();
        var registry = LoadRepositoryMods().Registry;

        foreach (var definition in registry.OfKind("loot"))
        {
            Assert.Empty(LootTableJson.Parse(definition.Json).UnknownItems(items));
        }

        Assert.True(loot.TryGet("base:loot/medical", out _));
    }

    [Fact]
    public void LootFromTheLoadedMods_FillsAContainerTheSameWayEveryTime()
    {
        var (items, loot) = LoadCatalogs();

        (IReadOnlyList<ItemStack> Stacks, LootFillResult Result) Fill(ulong seed)
        {
            var repository = new InMemoryContainerRepository();
            var inventory = new InventoryService(items, repository);
            var id = new ContainerId(1);
            inventory.AddContainer(new Container(id, Mass.FromKilograms(20), Volume.FromLiters(20), items));
            var result = new LootService(loot).Fill("base:loot/kitchen", seed, new ContainerItemSink(inventory, id));
            repository.TryGet(id, out var container);
            return (container.Stacks, result);
        }

        var first = Fill(1234);
        var second = Fill(1234);

        Assert.True(first.Result.IsSuccess);
        Assert.NotEmpty(first.Stacks);
        Assert.Equal(first.Stacks, second.Stacks);
        Assert.Equal(first.Result.Rolled, second.Result.Rolled);
    }

    [Fact]
    public void GeneratedSchemas_MatchTheFilesCommittedForEditors()
    {
        foreach (var (kind, generated) in DefinitionSchemas.Generate(BaseDefinitionKinds.All))
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
