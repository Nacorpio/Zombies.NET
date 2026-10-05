using UnitsNet;
using Zombies.Domain.Actions;
using Zombies.Domain.Combat;
using Zombies.Domain.Crafting;
using Zombies.Domain.Inventory;
using Zombies.Domain.Items;
using Zombies.Domain.Mods;
using Zombies.Domain.StatusEffects;
using Zombies.Domain.Survival;
using Zombies.Engine.Core.Modding;
using Zombies.Engine.Net;

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
        Assert.Equal(107, result.Registry.Count);
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
    public void BaseFaults_DeclareTwoWeaponFaultsAndAnArmorFault_EachRepairedWithABaseItem()
    {
        var registry = LoadRepositoryMods().Registry;
        var items = new ItemCatalog(registry.OfKind("item").Select(d => ItemDefinitionJson.Parse(d.Json)));
        var categories = registry.OfKind("weapon_category").Select(d => WeaponDefinitionJson.ParseCategory(d.Json).Id).ToHashSet();

        var faults = new FaultCatalog(registry.OfKind("fault").Select(d => FaultJson.Parse(d.Json)));

        Assert.True(faults.All.Count(f => f.Target == FaultTarget.Weapon) >= 2);
        Assert.Contains(faults.All, f => f.Target == FaultTarget.Armor);
        Assert.All(faults.All, f => Assert.True(items.TryGet(f.Repair.Consumes, out _), $"{f.Id} is repaired with unknown item {f.Repair.Consumes}."));
        Assert.All(faults.All.SelectMany(f => f.WeaponCategories), c => Assert.Contains(c, categories));
    }

    [Fact]
    public void BaseLimbScores_ParseAndScoreAWholeBodyAsFull()
    {
        var scores = LoadRepositoryMods().Registry.OfKind("limb_score").Select(d => LimbScoreJson.Parse(d.Json)).ToList();

        Assert.Equal(["blocking", "grip", "manipulation", "movement", "vision"], scores.Select(s => s.Id.Split('/')[1]).Order());
        Assert.All(LimbScores.Compute(scores, new Body(new BodyId(1)), []), s => Assert.Equal(1, s.Value));
    }

    [Fact]
    public void BaseWoundKindsAndTreatments_ParseIntoCatalogs_ThatTheBandageItemFits()
    {
        var registry = LoadRepositoryMods().Registry;
        var items = new ItemCatalog(registry.OfKind("item").Select(d => ItemDefinitionJson.Parse(d.Json)));

        var kinds = new WoundKindCatalog(registry.OfKind("wound_kind").Select(d => WoundKindJson.Parse(d.Json)));
        var treatments = new TreatmentCatalog(registry.OfKind("treatment").Select(d => TreatmentJson.Parse(d.Json)), kinds);

        Assert.True(kinds.All.Count >= 3);
        Assert.True(treatments.All.Count >= 2);
        Assert.All(treatments.All, t => Assert.True(items.TryGet(t.Consumes, out _), $"{t.Id} consumes unknown item {t.Consumes}."));
        Assert.True(treatments.TryGet("base:treatment/bandage", out var bandage));
        Assert.True(bandage.StopsBleeding);
        Assert.Equal(new ItemId("base:item/bandage"), bandage.Consumes);
    }

    [Fact]
    public void BaseMoraleSourcesAndBands_ParseIntoACatalog_WithATeammateDeathAndAComfortItem()
    {
        var registry = LoadRepositoryMods().Registry;
        var items = new ItemCatalog(registry.OfKind("item").Select(d => ItemDefinitionJson.Parse(d.Json)));

        var catalog = new MoraleCatalog(
            registry.OfKind("morale_source").Select(d => MoraleSourceJson.Parse(d.Json)),
            registry.OfKind("morale_band").Select(d => MoraleBandJson.Parse(d.Json)));

        Assert.True(catalog.Sources.Count >= 5);
        Assert.Contains(catalog.Sources, s => s.Trigger.Event == new StatName("teammate_died"));
        var comfort = Assert.Single(catalog.Sources, s => s.Trigger.Item is not null);
        Assert.True(items.TryGet(comfort.Trigger.Item!.Value, out _), $"{comfort.Id} names unknown item {comfort.Trigger.Item}.");
        Assert.Equal(["base:morale_band/low", "base:morale_band/steady", "base:morale_band/high"], catalog.Bands.Select(b => b.Id));

        var morale = new Morale(catalog);
        morale.OnEvent(new StatName("teammate_died"));
        Assert.Equal("base:morale_band/low", morale.Band?.Id);
        Assert.True(morale.EffectiveValue(new StatName("aim_spread"), 1) > 1);
    }

    [Fact]
    public void BaseSubstances_ParseIntoACatalog_WithAStimulantAndAnAlcoholicDrink()
    {
        var registry = LoadRepositoryMods().Registry;
        var items = new ItemCatalog(registry.OfKind("item").Select(d => ItemDefinitionJson.Parse(d.Json)));
        var effects = new StatusEffectCatalog(registry.OfKind("status_effect").Select(d => StatusEffectJson.Parse(d.Json)));

        var substances = new SubstanceCatalog(registry.OfKind("substance").Select(d => SubstanceJson.Parse(d.Json)), effects);

        Assert.Equal(["base:substance/alcohol", "base:substance/stimulant"], substances.All.Select(s => s.Id).Order());
        Assert.All(substances.All.SelectMany(s => s.Items), i => Assert.True(items.TryGet(i.Item, out _), $"{i.Item} is not a defined item."));
        Assert.True(items.TryGet(new ItemId("base:item/beer"), out var beer) && beer.Drinkable);
    }

    [Fact]
    public void DrinkingBeer_ImpairsAimMoreWithEveryDrink_AndAnAddictGetsWithdrawal()
    {
        var registry = LoadRepositoryMods().Registry;
        var catalog = new StatusEffectCatalog(registry.OfKind("status_effect").Select(d => StatusEffectJson.Parse(d.Json)));
        var effects = new CreatureEffects(new CreatureId(1), catalog);
        var substances = new CreatureSubstances(new SubstanceCatalog(registry.OfKind("substance").Select(d => SubstanceJson.Parse(d.Json)), catalog), effects);
        var beer = new ItemId("base:item/beer");
        var aim = new StatName("aim_spread");

        substances.Consume(beer, 0);
        var oneDrink = effects.EffectiveValue(aim, 1);
        substances.Consume(beer, 0.99);
        var twoDrinks = effects.EffectiveValue(aim, 1);

        Assert.True(oneDrink > 1);
        Assert.True(twoDrinks > oneDrink);
        Assert.True(substances.IsAddicted("base:substance/alcohol"));

        substances.Advance(TimeSpan.FromDays(2));
        Assert.True(effects.Has("base:status_effect/alcohol_withdrawal"));
        Assert.False(effects.Has("base:status_effect/drunk"));
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
    public void SampleDataMod_ExtendsALootTableWithoutRestatingItsEntries()
    {
        var registry = LoadRepositoryMods().Registry;
        var entry = registry.OfKind("loot").Single(d => d.Id.Value == "base:loot/garage");

        var garage = LootTableJson.Parse(entry.Json);

        Assert.Contains(garage.Entries, e => e.Item.Value == "base:item/crowbar");
        Assert.Contains(garage.Entries, e => e.Item.Value == "sample_data:item/protein_bar");
        Assert.Contains("sample_data", entry.ModifiedBy);
    }

    [Fact]
    public void SampleDataMod_ScalesAStackSizeByAFactor()
    {
        var registry = LoadRepositoryMods().Registry;

        var water = ItemDefinitionJson.Parse(registry.OfKind("item").Single(d => d.Id.Value == "base:item/water_bottle").Json);

        Assert.Equal(5, water.MaxStack);
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
    public void BaseMod_DeclaresTheZombieDensityAndLootRarityOptions_DefaultingToOne()
    {
        var catalog = WorldOptionCatalog.From(LoadRepositoryMods().Registry);
        var options = new WorldOptions(catalog);

        Assert.Equal([BaseWorldOptions.LootRarity, BaseWorldOptions.ZombieDensity], catalog.All.Select(o => o.Id.Value));
        Assert.Equal(100, BaseWorldOptions.ZombieDensityPercent(options));
        Assert.Equal(100, BaseWorldOptions.LootRarityPercent(options));
    }

    [Fact]
    public void BaseWorldOptions_TurnTheChosenMultipliersIntoPercentages()
    {
        var catalog = WorldOptionCatalog.From(LoadRepositoryMods().Registry);
        var options = new WorldOptions(catalog, [KeyValuePair.Create(BaseWorldOptions.ZombieDensity, 1.5), KeyValuePair.Create(BaseWorldOptions.LootRarity, 0.25)]);

        Assert.Equal(150, BaseWorldOptions.ZombieDensityPercent(options));
        Assert.Equal(25, BaseWorldOptions.LootRarityPercent(options));
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
    public void BaseMod_ShipsInfectionPainkillerFoodPoisoningAndTheFatigueEffects()
    {
        var registry = LoadRepositoryMods().Registry;

        var effects = registry.OfKind("status_effect").Where(d => d.DefinedBy == "base").Select(d => d.Id.Value.Split('/')[1]).Order();

        Assert.Subset(effects.ToHashSet(), new HashSet<string> { "exhausted", "food_poisoning", "infection", "painkiller", "tired" });
    }

    [Fact]
    public void EveryFatigueLevelEffectTheServerAppliesExistsInTheBaseModAndChangesAStat()
    {
        var catalog = LoadEffects(LoadRepositoryMods().Registry);
        var applied = new ServerOptions(new GameIdentity(NetProtocol.Version, 0, []), 1).FatigueEffects;

        Assert.Equal([FatigueLevel.Tired, FatigueLevel.Exhausted], applied.Keys.Order());
        foreach (var effect in applied.Values)
        {
            Assert.True(catalog.TryGet(effect, out var definition), effect);
            Assert.NotEmpty(definition.Modifiers);
        }
    }

    [Fact]
    public void ABaseConsumable_ReducesFatigueThroughItsDefinition()
    {
        var registry = LoadRepositoryMods().Registry;

        var coffee = ItemDefinitionJson.Parse(registry.OfKind("item").Single(d => d.Id.Value == "base:item/coffee").Json);

        Assert.True(coffee.Drinkable);
        Assert.True(coffee.FatigueRelief > 0);
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

    private static ItemActionCatalog LoadActions(DefinitionRegistry registry) =>
        new(registry.OfKind("item_action").Select(d => ItemActionJson.Parse(d.Json)));

    [Fact]
    public void BaseMod_DefinesUseEquipDropSplitInspectAndRepair()
    {
        var registry = LoadRepositoryMods().Registry;

        var actions = registry.OfKind("item_action").Where(d => d.DefinedBy == "base").Select(d => d.Id.Value.Split('/')[1]).Order();

        Assert.Equal(["drop", "equip", "inspect", "repair", "split", "use"], actions);
    }

    [Fact]
    public void SampleDataMod_PatchesAnItemAction()
    {
        var registry = LoadRepositoryMods().Registry;
        var entry = registry.OfKind("item_action").Single(d => d.Id.Value == "base:item_action/drop");

        var drop = ItemActionJson.Parse(entry.Json);

        Assert.Equal("cannot_drop_here", drop.DisabledReason);
        Assert.Equal(["sample_data"], entry.ModifiedBy);
    }

    [Fact]
    public void EveryReferenceInItemActionContentResolves()
    {
        var registry = LoadRepositoryMods().Registry;
        var items = new ItemCatalog(registry.OfKind("item").Select(d => ItemDefinitionJson.Parse(d.Json)));
        var wearables = new WearableCatalog([]);
        var weapons = new WeaponCatalog(
            registry.OfKind("weapon_category").Select(d => WeaponDefinitionJson.ParseCategory(d.Json)),
            registry.OfKind("weapon").Select(d => WeaponDefinitionJson.ParseWeapon(d.Json)),
            registry.OfKind("attachment").Select(d => WeaponDefinitionJson.ParseAttachment(d.Json)));
        var service = new ItemActionService(LoadActions(registry), items, wearables, weapons);

        var beans = new ItemStack(new StackId(1), new ItemId("base:item/canned_beans"), 4);
        var actions = service.Available(beans, new ItemActionContext(IsOwnContainer: true, IsReadOnly: false));

        Assert.Equal(["inspect", "drop", "split", "use"], actions.Select(a => a.Id.Split('/')[1]));
        Assert.All(actions, a => Assert.True(a.IsEnabled));
    }

    private static AreaTypeCatalog LoadAreas(DefinitionRegistry registry) =>
        new(registry.OfKind("area_type").Select(d => AreaTypeJson.Parse(d.Json)));

    [Fact]
    public void BaseMod_DefinesFourAreaTypes()
    {
        var registry = LoadRepositoryMods().Registry;

        var areas = registry.OfKind("area_type").Where(d => d.DefinedBy == "base").Select(d => d.Id.Value.Split('/')[1]).Order();

        Assert.Equal(["bathroom", "bedroom", "garage", "kitchen"], areas);
    }

    [Fact]
    public void SampleDataMod_PatchesAnAreaType()
    {
        var registry = LoadRepositoryMods().Registry;
        var entry = registry.OfKind("area_type").Single(d => d.Id.Value == "base:area_type/garage");

        var garage = AreaTypeJson.Parse(entry.Json);

        Assert.Equal(["sample_data"], entry.ModifiedBy);
        var shelf = Assert.Single(garage.Rules);
        Assert.Equal(5, shelf.Tables.Single(t => t.Table == "base:loot/military").Weight);
    }

    [Fact]
    public void EveryLootTableAnAreaTypeNamesExists()
    {
        var registry = LoadRepositoryMods().Registry;
        var loot = new LootTableCatalog(registry.OfKind("loot").Select(d => LootTableJson.Parse(d.Json)));

        foreach (var area in LoadAreas(registry).All)
        {
            foreach (var rule in area.Rules)
            {
                foreach (var table in rule.Tables)
                {
                    Assert.True(loot.TryGet(table.Table, out _), $"{area.Id} names {table.Table}, which is not a loot table");
                }
            }
        }
    }

    [Fact]
    public void AreaLoot_IsDeterministicAndFillsAContainer()
    {
        var registry = LoadRepositoryMods().Registry;
        var items = new ItemCatalog(registry.OfKind("item").Select(d => ItemDefinitionJson.Parse(d.Json)));
        var loot = new LootTableCatalog(registry.OfKind("loot").Select(d => LootTableJson.Parse(d.Json)));
        var service = new AreaLootService(LoadAreas(registry), new LootService(loot));

        var first = new Container(new ContainerId(1), Mass.FromKilograms(50), Volume.FromLiters(50), items);
        var second = new Container(new ContainerId(2), Mass.FromKilograms(50), Volume.FromLiters(50), items);
        var a = service.Fill("base:area_type/kitchen", "fridge", 2, 99, new ContainerItemSink(new InventoryService(items, new InMemoryContainerRepository()), first.Id));
        var b = service.Fill("base:area_type/kitchen", "fridge", 2, 99, new ContainerItemSink(new InventoryService(items, new InMemoryContainerRepository()), second.Id));

        Assert.True(a.IsSuccess, a.Error?.ToString());
        Assert.Equal(a.Table, b.Table);
        Assert.Equal(first.Stacks.Select(s => (s.Item, s.Count)), second.Stacks.Select(s => (s.Item, s.Count)));
    }
}