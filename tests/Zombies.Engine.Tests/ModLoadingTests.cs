using UnitsNet;
using Zombies.Domain.Combat;
using Zombies.Domain.Crafting;
using Zombies.Domain.Inventory;
using Zombies.Domain.Items;
using Zombies.Domain.Mods;
using Zombies.Domain.StatusEffects;
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
        Assert.Equal(36, result.Registry.Count);
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

    private static WeaponCatalog LoadWeapons(DefinitionRegistry registry) => new(
        registry.OfKind("weapon_category").Select(d => WeaponDefinitionJson.ParseCategory(d.Json)),
        registry.OfKind("weapon").Select(d => WeaponDefinitionJson.ParseWeapon(d.Json)),
        registry.OfKind("attachment").Select(d => WeaponDefinitionJson.ParseAttachment(d.Json)));

    [Fact]
    public void BaseMod_ShipsWeaponCategoriesWeaponsAndAttachments()
    {
        var registry = LoadRepositoryMods().Registry;

        Assert.Equal(["bladed_melee", "blunt_melee", "pistol"], registry.OfKind("weapon_category").Where(d => d.DefinedBy == "base").Select(d => d.Id.Value.Split('/')[1]).Order());
        Assert.True(registry.OfKind("weapon").Count(d => d.DefinedBy == "base") >= 5);

        var attachments = registry.OfKind("attachment").Select(d => WeaponDefinitionJson.ParseAttachment(d.Json)).ToList();
        Assert.True(attachments.Count >= 4);
        Assert.Equal(attachments.Count, attachments.Select(a => string.Join(',', a.Effects.Select(e => $"{e.Stat}:{e.Operation}").Order())).Distinct().Count());
        Assert.Contains(attachments, a => a.Effects.Any(e => e.Stat.Value == "noise" && e.Operation == ModifierOperation.Multiply && e.Value < 1));
    }

    [Fact]
    public void EveryItemCategoryAndMountReferenceInWeaponContentResolves()
    {
        var registry = LoadRepositoryMods().Registry;
        var items = new ItemCatalog(registry.OfKind("item").Select(d => ItemDefinitionJson.Parse(d.Json)));
        var catalog = LoadWeapons(registry);

        var weapons = registry.OfKind("weapon").Select(d => WeaponDefinitionJson.ParseWeapon(d.Json)).ToList();
        var categories = registry.OfKind("weapon_category").Select(d => WeaponDefinitionJson.ParseCategory(d.Json)).ToDictionary(c => c.Id);
        var attachments = registry.OfKind("attachment").Select(d => WeaponDefinitionJson.ParseAttachment(d.Json)).ToList();

        foreach (var weapon in weapons)
        {
            Assert.True(items.TryGet(weapon.Item, out _), $"{weapon.Item} is not an item");
            Assert.True(weapon.AmmoItem is not { } ammo || items.TryGet(ammo, out _), $"{weapon.Item} ammo is not an item");
            Assert.True(catalog.TryGetCategory(weapon.Category, out _), $"{weapon.Category} is not a category");
        }

        var mounts = weapons.SelectMany(w => categories[w.Category].Mounts.Concat(w.ExtraMounts)).ToHashSet();
        foreach (var attachment in attachments)
        {
            Assert.True(items.TryGet(attachment.Item, out _), $"{attachment.Item} is not an item");
            Assert.Contains(attachment.Mount, mounts);
        }
    }

    [Fact]
    public void SampleDataMod_AddsAWeaponAndPatchesABaseWeaponStat()
    {
        var registry = LoadRepositoryMods().Registry;
        var weapons = registry.OfKind("weapon").ToDictionary(d => d.Id.Value);

        var crowbar = WeaponDefinitionJson.ParseWeapon(weapons["base:weapon/crowbar"].Json);

        Assert.Equal(26, crowbar.Damage);
        Assert.Equal(["sample_data"], weapons["base:weapon/crowbar"].ModifiedBy);
        Assert.Equal("sample_data", weapons["sample_data:weapon/fire_axe"].DefinedBy);
    }

    [Fact]
    public void WeaponContentWorksThroughTheWeaponService()
    {
        var service = new WeaponService(LoadWeapons(LoadRepositoryMods().Registry));
        var pistol = new ItemId("base:item/pistol_9mm");
        var loaded = ItemState.Create([new(WeaponService.RoundsValue, 12)]);

        var quiet = service.Attach(pistol, loaded, new ItemId("base:item/suppressor"));
        var loud = service.Use(pistol, loaded);
        var shot = service.Use(pistol, quiet.State);

        Assert.True(quiet.IsSuccess);
        Assert.True(((WeaponUsed)shot.Events[0]).Noise < ((WeaponUsed)loud.Events[0]).Noise);
    }

    private static StatusEffectCatalog LoadEffects(DefinitionRegistry registry) =>
        new(registry.OfKind("status_effect").Select(d => StatusEffectJson.Parse(d.Json)));

    [Fact]
    public void BaseMod_ShipsInfectionPainkillerAndFoodPoisoning()
    {
        var registry = LoadRepositoryMods().Registry;

        var effects = registry.OfKind("status_effect").Where(d => d.DefinedBy == "base").Select(d => d.Id.Value.Split('/')[1]).Order();

        Assert.Equal(["food_poisoning", "infection", "painkiller"], effects);
    }

    [Fact]
    public void Infection_HasStagesAndACureItemInTheBaseMod()
    {
        var registry = LoadRepositoryMods().Registry;
        var infection = StatusEffectJson.Parse(registry.OfKind("status_effect").Single(d => d.Id.Value == "base:status_effect/infection").Json);

        Assert.Equal(EffectCategory.Ailment, infection.Category);
        Assert.Equal(["mild", "severe"], infection.Stages.Select(s => s.Name));
        Assert.Equal(new ItemId("base:item/antibiotics"), Assert.Single(infection.CuredByItems));
        Assert.Contains(infection.Stages, s => s.Periodic.Count > 0);
    }

    [Fact]
    public void SampleDataMod_PatchesAnEffectDuration()
    {
        var registry = LoadRepositoryMods().Registry;
        var entry = registry.OfKind("status_effect").Single(d => d.Id.Value == "base:status_effect/painkiller");

        var painkiller = StatusEffectJson.Parse(entry.Json);

        Assert.Equal(TimeSpan.FromSeconds(600), painkiller.Duration);
        Assert.Equal(["sample_data"], entry.ModifiedBy);
    }

    [Fact]
    public void EveryReferenceInStatusEffectContentResolves()
    {
        var registry = LoadRepositoryMods().Registry;
        var items = new ItemCatalog(registry.OfKind("item").Select(d => ItemDefinitionJson.Parse(d.Json)));
        var catalog = LoadEffects(registry);

        foreach (var definition in catalog.All)
        {
            foreach (var cure in definition.CuredByItems)
            {
                Assert.True(items.TryGet(cure, out _), $"{definition.Id} is cured by {cure}, which is not an item");
            }

            foreach (var cure in definition.CuredByEffects)
            {
                Assert.True(catalog.TryGet(cure, out _), $"{definition.Id} is cured by {cure}, which is not an effect");
            }
        }
    }

    [Fact]
    public void StatusEffectContentWorksThroughTheDomain()
    {
        var catalog = LoadEffects(LoadRepositoryMods().Registry);
        var creature = new CreatureEffects(new CreatureId(1), catalog);

        var applied = creature.Apply("base:status_effect/infection");
        var cured = creature.CureWithItem(new ItemId("base:item/antibiotics"));

        Assert.Equal(new EffectApplied(new CreatureId(1), "base:status_effect/infection", 1), applied.Events[0]);
        Assert.Equal(new EffectCured(new CreatureId(1), "base:status_effect/infection", CureCause.Item, "base:item/antibiotics"), Assert.Single(cured.Events));
    }
}