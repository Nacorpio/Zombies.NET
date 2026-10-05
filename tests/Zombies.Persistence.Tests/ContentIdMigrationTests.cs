using UnitsNet;
using Zombies.Domain.Inventory;
using Zombies.Domain.Items;
using Zombies.Domain.Mods;
using Zombies.Persistence.Sqlite;

namespace Zombies.Persistence.Tests;

public sealed class ContentIdMigrationTests
{
    private static readonly ContainerId Crate = new(7);
    private static readonly ItemId OldRifle = new("gunsmith:item/old_rifle");
    private static readonly ItemId OldScope = new("gunsmith:item/old_scope");
    private static readonly ItemId Rifle = new("gunsmith:item/rifle");
    private static readonly ItemId Scope = new("gunsmith:item/scope");
    private static readonly ItemId Beans = new("base:item/canned_beans");

    private static ItemCatalog Catalog(params ItemId[] ids) =>
        new(ids.Select(id => new ItemDefinition(id, Mass.FromKilograms(1), Volume.FromLiters(1), maxStack: 4)));

    private static (string Path, string Json) Item(string id) =>
        ($"data/{id.Replace(':', '_').Replace('/', '_')}.json", $$"""{ "id": "{{id}}", "mass": "1 kg", "volume": "1 l", "maxStack": 4 }""");

    private static (string Path, string Json) Migration(string name, string from, string? to) =>
        ($"data/migration_{name}.json", $$"""{ "id": "gunsmith:migration/{{name}}", "from": "{{from}}", {{(to is null ? "\"removed\": true" : $"\"to\": \"{to}\"")}} }""");

    private static ModPackage Gunsmith(string version, params (string Path, string Json)[] files) =>
        new("gunsmith", $$"""{ "id": "gunsmith", "version": "{{version}}" }""", [.. files.Select(f => new ModFile(f.Path, f.Json))]);

    private static ModLoadResult Load(ModPackage gunsmith)
    {
        var result = ModLoader.Load([new ModPackage("base", """{ "id": "base", "version": "1.0.0" }""", [new ModFile("data/beans.json", Item(Beans.Value).Json)]), gunsmith]);
        Assert.True(result.IsSuccess, string.Join("; ", result.Errors));
        return result;
    }

    private static List<SavedMod> SavedWith(ModLoadResult result) =>
        [.. result.Mods.Select(m => new SavedMod(m.Manifest.Id, m.Manifest.Version.ToString(), m.ContentHash))];

    /// <summary>Saves a Crate holding a rifle with a scope attached and some beans, under the ids of the old mod version.</summary>
    private static (TempSave Save, ModLoadResult Old) SaveWithOldVersion()
    {
        var old = Load(Gunsmith("1.0.0", Item(OldRifle.Value), Item(OldScope.Value)));
        var save = new TempSave();
        using var database = save.Open();
        var catalog = Catalog(OldRifle, OldScope, Beans);
        var memory = new InMemoryContainerRepository();
        var service = new InventoryService(catalog, memory);
        Assert.True(service.AddContainer(new Container(Crate, Mass.FromKilograms(50), Volume.FromLiters(50), catalog)).IsSuccess);
        Assert.True(service.AddItems(Crate, OldRifle, 1, ItemState.Create([new("condition", 40)], [new(OldScope, 2)])).IsSuccess);
        Assert.True(service.AddItems(Crate, Beans, 3).IsSuccess);
        memory.TryGet(Crate, out var container);
        new SqliteContainerRepository(database, catalog).Save(container);
        return (save, old);
    }

    private static Container Reload(TempSave save, ContentIdMigrator migrator, params ItemId[] known)
    {
        using var database = save.Open();
        Assert.True(new SqliteContainerRepository(database, Catalog(known), migrator).TryGet(Crate, out var loaded));
        return loaded;
    }

    [Fact]
    public void Save_MadeWithAnOlderModVersion_LoadsTheSameStackUnderTheRenamedItem()
    {
        var (save, old) = SaveWithOldVersion();
        using (save)
        {
            var renamed = Load(Gunsmith("2.0.0", Item(Rifle.Value), Item(Scope.Value), Migration("rifle", OldRifle.Value, Rifle.Value), Migration("scope", OldScope.Value, Scope.Value)));
            var notes = new List<string>();

            var loaded = Reload(save, new ContentIdMigrator(renamed, SavedWith(old), notes.Add), Rifle, Scope, Beans);

            Assert.Equal(1, loaded.CountOf(Rifle, ItemState.Create([new("condition", 40)], [new(Scope, 2)])));
            Assert.Equal(3, loaded.CountOf(Beans));
            Assert.Equal(2, loaded.Stacks.Count);
            Assert.Empty(notes);
        }
    }

    [Fact]
    public void Save_WithAChainOfRenames_LoadsUnderTheLastName()
    {
        var (save, old) = SaveWithOldVersion();
        using (save)
        {
            var renamed = Load(Gunsmith(
                "3.0.0",
                Item(Rifle.Value),
                Item(Scope.Value),
                Migration("rifle_a", OldRifle.Value, "gunsmith:item/mid_rifle"),
                Migration("rifle_b", "gunsmith:item/mid_rifle", Rifle.Value),
                Migration("scope", OldScope.Value, Scope.Value)));

            var loaded = Reload(save, new ContentIdMigrator(renamed, SavedWith(old), _ => { }), Rifle, Scope, Beans);

            Assert.Equal(1, loaded.CountOf(Rifle, ItemState.Create([new("condition", 40)], [new(Scope, 2)])));
        }
    }

    [Fact]
    public void Save_WithARemovedAttachedItem_DropsItAndSaysSo()
    {
        var (save, old) = SaveWithOldVersion();
        using (save)
        {
            var removed = Load(Gunsmith("2.0.0", Item(Rifle.Value), Migration("rifle", OldRifle.Value, Rifle.Value), Migration("scope", OldScope.Value, null)));
            var notes = new List<string>();

            var loaded = Reload(save, new ContentIdMigrator(removed, SavedWith(old), notes.Add), Rifle, Beans);

            Assert.Equal(1, loaded.CountOf(Rifle, ItemState.Create([new("condition", 40)])));
            var note = Assert.Single(notes);
            Assert.Contains(OldScope.Value, note, StringComparison.Ordinal);
            Assert.Contains("removed", note, StringComparison.Ordinal);
            Assert.Contains(Crate.ToString(), note, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Save_WithARemovedStackItem_DropsTheStack()
    {
        var (save, old) = SaveWithOldVersion();
        using (save)
        {
            var removed = Load(Gunsmith("2.0.0", Migration("rifle", OldRifle.Value, null), Migration("scope", OldScope.Value, null)));

            var loaded = Reload(save, new ContentIdMigrator(removed, SavedWith(old), _ => { }), Beans);

            Assert.Equal(3, loaded.CountOf(Beans));
            Assert.Single(loaded.Stacks);
        }
    }

    [Fact]
    public void Save_WithAnItemNoModDefinesAndNoMigration_KeepsItAndReportsItWithTheModsThatSavedIt()
    {
        var (save, old) = SaveWithOldVersion();
        using (save)
        {
            var without = Load(Gunsmith("2.0.0"));
            var notes = new List<string>();

            var loaded = Reload(save, new ContentIdMigrator(without, SavedWith(old), notes.Add), OldRifle, OldScope, Beans);

            Assert.Equal(1, loaded.CountOf(OldRifle, ItemState.Create([new("condition", 40)], [new(OldScope, 2)])));
            Assert.Equal(2, notes.Count);
            Assert.All(notes, note =>
            {
                Assert.Contains("gunsmith 1.0.0", note, StringComparison.Ordinal);
                Assert.Contains("base 1.0.0", note, StringComparison.Ordinal);
            });
            Assert.Contains(notes, n => n.Contains(OldRifle.Value, StringComparison.Ordinal));
            Assert.Contains(notes, n => n.Contains(OldScope.Value, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Stack_WithTwoAttachedItemsThatMigrateToOne_AddsTheirCounts()
    {
        var merged = Load(Gunsmith("2.0.0", Item(Rifle.Value), Item(Scope.Value), Migration("rifle", OldRifle.Value, Rifle.Value), Migration("scope", OldScope.Value, Scope.Value)));
        var migrator = new ContentIdMigrator(merged, [], _ => { });

        var stack = migrator.Migrate(new StackSnapshot(1, OldRifle.Value, 1, new ItemStateSnapshot([], [new(OldScope.Value, 1), new(Scope.Value, 2)])), "test");

        Assert.Equal([new KeyValuePair<string, int>(Scope.Value, 3)], stack!.State!.Attached);
    }

    [Fact]
    public void Outfit_WearingARenamedItem_LoadsItUnderTheNewName()
    {
        using var save = new TempSave();
        using var database = save.Open();
        var outfit = new Outfit(Fixtures.Wearables);
        Assert.True(outfit.Equip(Fixtures.Jacket).IsSuccess);
        new SqliteOutfitRepository(database, Fixtures.Wearables).Save(1, outfit);
        database.Command(null, "UPDATE outfit_items SET item = 'base:item/old_jacket'").ExecuteNonQuery();
        var renamed = Load(Gunsmith("2.0.0", Migration("jacket", "base:item/old_jacket", Fixtures.Jacket.Value)));

        Assert.True(new SqliteOutfitRepository(database, Fixtures.Wearables, new ContentIdMigrator(renamed, [], _ => { })).TryGet(1, out var loaded));

        Assert.Equal([Fixtures.Jacket], loaded.WornItems);
    }
}
