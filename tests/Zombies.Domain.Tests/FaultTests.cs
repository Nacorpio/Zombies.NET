using UnitsNet;
using Zombies.Domain.Actions;
using Zombies.Domain.Combat;
using Zombies.Domain.Inventory;
using Zombies.Domain.Items;

namespace Zombies.Domain.Tests;

public sealed class FaultTests
{
    private const string Chipped = "test:fault/chipped";
    private const string Jammed = "test:fault/jammed";
    private const string Ripped = "test:fault/ripped";

    private static readonly ItemId Knife = new("test:item/knife");
    private static readonly ItemId Pistol = new("test:item/pistol");
    private static readonly ItemId Rounds = new("test:item/rounds");
    private static readonly ItemId Jacket = new("test:item/jacket");
    private static readonly ItemId Whetstone = new("test:item/whetstone");
    private static readonly ItemId Kit = new("test:item/kit");
    private static readonly ContainerId Pack = new(1);

    private static readonly FaultCatalog Faults = new(
        new[]
        {
            $$"""{ "id": "{{Chipped}}", "target": "weapon", "weaponCategories": [ "test:weapon_category/blade" ], "effects": [ { "stat": "damage", "operation": "multiply", "value": 0.5 } ], "gained": { "cause": "weapon_use", "chance": 0.25 }, "repair": { "consumes": "test:item/whetstone", "time": 30 } }""",
            $$"""{ "id": "{{Jammed}}", "target": "weapon", "weaponCategories": [ "test:weapon_category/pistol" ], "effects": [ { "stat": "jam_chance", "operation": "add", "value": 0.4 }, { "stat": "handling", "operation": "multiply", "value": 0.5 } ], "gained": { "cause": "weapon_use", "chance": 0.25 }, "repair": { "consumes": "test:item/kit", "time": 20 } }""",
            $$"""{ "id": "{{Ripped}}", "target": "armor", "effects": [ { "stat": "protection", "operation": "multiply", "value": 0.5 } ], "gained": { "cause": "armor_hit", "chance": 0.1 }, "repair": { "consumes": "test:item/kit", "time": 45 } }""",
        }.Select(FaultJson.Parse));

    private static readonly ItemCatalog Items = new(
    [
        new ItemDefinition(Knife, Mass.FromKilograms(0.2), Volume.FromLiters(0.2), maxStack: 3),
        new ItemDefinition(Pistol, Mass.FromKilograms(1), Volume.FromLiters(1), maxStack: 1),
        new ItemDefinition(Rounds, Mass.FromKilograms(0.1), Volume.FromLiters(0.1), maxStack: 10),
        new ItemDefinition(Jacket, Mass.FromKilograms(1), Volume.FromLiters(2), maxStack: 1),
        new ItemDefinition(Whetstone, Mass.FromKilograms(0.1), Volume.FromLiters(0.1), maxStack: 3),
        new ItemDefinition(Kit, Mass.FromKilograms(0.3), Volume.FromLiters(0.3), maxStack: 3),
    ]);

    private static readonly WeaponCatalog Weapons = new(
        [new WeaponCategory("test:weapon_category/blade", 10, []), new WeaponCategory("test:weapon_category/pistol", 10, [])],
        [
            new WeaponDefinition(Knife, "test:weapon_category/blade", damage: 20, DamageType.Cut, rateOfFire: 1, reach: 1, noise: 10, wearPerUse: 1),
            new WeaponDefinition(Pistol, "test:weapon_category/pistol", damage: 30, DamageType.Pierce, rateOfFire: 2, reach: 25, noise: 90, ammoItem: Rounds, wearPerUse: 1),
        ],
        []);

    private static readonly WearableCatalog Wearables = new(
        [new WearableDefinition(Jacket, ClothingLayer.Outer, [BodyPart.Torso], ThermalResistance.FromSquareMeterKelvinsPerWatt(0.1), new Dictionary<DamageType, double> { [DamageType.Cut] = 0.6 })]);

    private static readonly WeaponService Service = new(Weapons, Faults);

    private static ItemState Faulty(string fault, params (string Name, int Value)[] values) =>
        ItemFaults.With(values.Length == 0 ? null : ItemState.Create(values.Select(v => new KeyValuePair<string, int>(v.Name, v.Value))), fault);

    private static ItemState LoadedPistol(string? fault = null) =>
        fault is null ? ItemState.Create([new(WeaponService.RoundsValue, 6)]) : Faulty(fault, (WeaponService.RoundsValue, 6));

    private static void AssertInvalid(string json) => Assert.Throws<FaultDefinitionException>(() => FaultJson.Parse(json));

    [Fact]
    public void AFaultDefinition_DeclaresItsEffectsGainAndRepair()
    {
        Assert.True(Faults.TryGet(Chipped, out var fault));

        Assert.Equal(FaultTarget.Weapon, fault.Target);
        Assert.Equal(new FaultEffect(new StatName("damage"), ModifierOperation.Multiply, 0.5), Assert.Single(fault.Effects));
        Assert.Equal(new FaultGain(FaultCause.WeaponUse, 0.25), fault.Gain);
        Assert.Equal(new FaultRepair(Whetstone, TimeSpan.FromSeconds(30)), fault.Repair);
    }

    [Theory]
    [InlineData("""{ "id": "test:fault/x", "target": "weapon", "effects": [], "repair": { "consumes": "test:item/a", "time": 1 } }""")]
    [InlineData("""{ "id": "test:fault/x", "target": "weapon", "effects": [ { "stat": "protection", "operation": "multiply", "value": 0.5 } ], "repair": { "consumes": "test:item/a", "time": 1 } }""")]
    [InlineData("""{ "id": "test:fault/x", "target": "armor", "effects": [ { "stat": "damage", "operation": "multiply", "value": 0.5 } ], "repair": { "consumes": "test:item/a", "time": 1 } }""")]
    [InlineData("""{ "id": "test:fault/x", "target": "armor", "effects": [ { "stat": "protection", "operation": "multiply", "value": 0.5 } ], "gained": { "cause": "weapon_use", "chance": 0.1 }, "repair": { "consumes": "test:item/a", "time": 1 } }""")]
    [InlineData("""{ "id": "test:fault/x", "target": "weapon", "effects": [ { "stat": "damage", "operation": "multiply", "value": 0.5 } ], "gained": { "cause": "weapon_use", "chance": 0 }, "repair": { "consumes": "test:item/a", "time": 1 } }""")]
    [InlineData("""{ "id": "test:fault/x", "target": "weapon", "effects": [ { "stat": "damage", "operation": "multiply", "value": 0.5 } ], "repair": { "consumes": "not an id", "time": 1 } }""")]
    [InlineData("""{ "id": "test:fault/x", "target": "weapon", "effects": [ { "stat": "damage", "operation": "multiply", "value": 0.5 } ], "repair": { "consumes": "test:item/a", "time": -1 } }""")]
    [InlineData("""{ "id": "test:fault/x", "target": "armor", "weaponCategories": [ "test:weapon_category/blade" ], "effects": [ { "stat": "protection", "operation": "multiply", "value": 0.5 } ], "repair": { "consumes": "test:item/a", "time": 1 } }""")]
    [InlineData("""{ "id": "test:fault/x", "target": "boot", "effects": [ { "stat": "damage", "operation": "multiply", "value": 0.5 } ], "repair": { "consumes": "test:item/a", "time": 1 } }""")]
    public void AFaultDefinition_ThatContradictsItself_IsRejected(string json) => AssertInvalid(json);

    [Fact]
    public void FaultCatalog_RejectsADuplicate()
    {
        Assert.True(Faults.TryGet(Chipped, out var chipped));

        Assert.Throws<ArgumentException>(() => new FaultCatalog([chipped, chipped]));
    }

    [Fact]
    public void StacksWithDifferentFaults_DoNotMerge()
    {
        var catalog = Items;
        var container = new Container(Pack, Mass.FromKilograms(10), Volume.FromLiters(10), catalog);
        var chipped = ItemFaults.With(null, Chipped);

        Assert.True(container.TryAdd(Knife, 1, chipped).IsSuccess);
        Assert.True(container.TryAdd(Knife, 1, ItemFaults.With(null, Jammed)).IsSuccess);
        Assert.True(container.TryAdd(Knife, 1).IsSuccess);
        Assert.True(container.TryAdd(Knife, 1, chipped).IsSuccess);

        Assert.Equal(3, container.Stacks.Count);
        Assert.Equal(2, container.CountOf(Knife, chipped));
    }

    [Fact]
    public void AFault_IsPartOfItemState_AndLeavesTheOtherValuesAlone()
    {
        var state = Faulty(Chipped, (WeaponService.ConditionValue, 40));

        Assert.Equal([Chipped], ItemFaults.Of(state));
        Assert.Equal(40, WeaponService.ConditionOf(state));
        Assert.Equal(ItemState.Create([new(WeaponService.ConditionValue, 40)]), ItemFaults.Without(state, Chipped));
        Assert.Null(ItemFaults.Without(ItemFaults.With(null, Chipped), Chipped));
    }

    [Fact]
    public void AWeaponFault_ChangesTheStatsItDeclares()
    {
        Assert.True(Service.TryGetEffectiveStats(Knife, null, out var sound));
        Assert.True(Service.TryGetEffectiveStats(Knife, ItemFaults.With(null, Chipped), out var chipped));
        Assert.True(Service.TryGetEffectiveStats(Pistol, LoadedPistol(Jammed), out var jammed));

        Assert.Equal(sound.Damage / 2, chipped.Damage, 9);
        Assert.Equal(sound.Handling, chipped.Handling, 9);
        Assert.Equal(5, jammed.Handling, 9);
    }

    [Fact]
    public void AWeaponFault_IsIgnoredWhenNoFaultCatalogIsGiven()
    {
        var plain = new WeaponService(Weapons);

        Assert.True(plain.TryGetEffectiveStats(Knife, ItemFaults.With(null, Chipped), out var stats));
        Assert.Equal(20, stats.Damage, 9);
    }

    [Fact]
    public void AJammedWeapon_JamsWhenTheRollIsBelowItsChance_AndSpendsNothing()
    {
        var state = LoadedPistol(Jammed);

        var jammed = Service.Use(Pistol, state, jamRoll: 0.39);
        var fired = Service.Use(Pistol, state, jamRoll: 0.4);

        Assert.Equal(WeaponError.Jammed, jammed.Error);
        Assert.True(fired.IsSuccess);
        Assert.Equal(5, WeaponService.RoundsOf(fired.State));
        Assert.Equal(0.4, Service.JamChanceOf(state), 9);
        Assert.Equal(0, Service.JamChanceOf(LoadedPistol()), 9);
    }

    [Fact]
    public void AWeaponWithoutFaults_NeverJams()
    {
        Assert.True(Service.Use(Pistol, LoadedPistol(), jamRoll: 0).IsSuccess);
    }

    [Theory]
    [InlineData(0.0, Chipped)]
    [InlineData(0.24, Chipped)]
    [InlineData(0.25, null)]
    [InlineData(0.99, null)]
    public void UsingABlade_MayGainAFaultOfItsCategory(double roll, string? gained)
    {
        var result = Service.Use(Knife, null, gainRoll: roll);

        Assert.True(result.IsSuccess);
        Assert.Equal(gained is null ? [] : [gained], ItemFaults.Of(result.State));
        Assert.Equal(gained is null ? 0 : 1, result.Events.OfType<WeaponFaulted>().Count());
    }

    [Fact]
    public void UsingAPistol_GainsOnlyFaultsThatApplyToPistols()
    {
        var result = Service.Use(Pistol, LoadedPistol(), gainRoll: 0);

        Assert.Equal([Jammed], ItemFaults.Of(result.State));
        Assert.Equal(new WeaponFaulted(Pistol, Jammed), result.Events.OfType<WeaponFaulted>().Single());
    }

    [Fact]
    public void AFaultAlreadyOnTheWeapon_IsNotGainedAgain()
    {
        var result = Service.Use(Knife, ItemFaults.With(null, Chipped), gainRoll: 0);

        Assert.Empty(result.Events.OfType<WeaponFaulted>());
    }

    [Fact]
    public void AnArmorFault_LowersTheProtectionOfTheWornItem()
    {
        var outfit = new Outfit(Wearables, Faults);
        Assert.True(outfit.Equip(Jacket).IsSuccess);
        var whole = outfit.Protection(BodyPart.Torso, DamageType.Cut);

        Assert.True(outfit.SetFaults(Jacket, [Ripped]).IsSuccess);

        Assert.Equal(0.6, whole, 9);
        Assert.Equal(0.3, outfit.Protection(BodyPart.Torso, DamageType.Cut), 9);
        Assert.Equal([Ripped], outfit.FaultsOf(Jacket));
    }

    [Fact]
    public void AnArmorFault_CannotBeSetOnSomethingNotWorn()
    {
        var outfit = new Outfit(Wearables, Faults);

        Assert.Equal(InventoryError.NotWorn, outfit.SetFaults(Jacket, [Ripped]).Error);
    }

    [Fact]
    public void AnOutfitWithAFault_SurvivesASnapshot()
    {
        var outfit = new Outfit(Wearables, Faults);
        Assert.True(outfit.Equip(Jacket).IsSuccess);
        Assert.True(outfit.SetFaults(Jacket, [Ripped]).IsSuccess);

        var restored = Outfit.Restore(outfit.ToSnapshot(), Wearables, Faults);

        Assert.Equal([Ripped], restored.FaultsOf(Jacket));
        Assert.Equal(0.3, restored.Protection(BodyPart.Torso, DamageType.Cut), 9);
    }

    private static (RepairService Repairs, InventoryService Inventory, InMemoryContainerRepository Containers) Rig()
    {
        var containers = new InMemoryContainerRepository();
        var inventory = new InventoryService(Items, containers);
        Assert.True(inventory.AddContainer(new Container(Pack, Mass.FromKilograms(10), Volume.FromLiters(10), Items)).IsSuccess);
        return (new RepairService(Faults, inventory), inventory, containers);
    }

    [Fact]
    public void Repair_UsesUpTheRequiredItem_AndRemovesTheFault()
    {
        var (repairs, inventory, containers) = Rig();
        var chipped = Faulty(Chipped, (WeaponService.ConditionValue, 70));
        Assert.True(inventory.AddItems(Pack, Knife, 1, chipped).IsSuccess);
        Assert.True(inventory.AddItems(Pack, Whetstone, 2).IsSuccess);

        var result = repairs.Repair(Pack, Knife, chipped, Chipped);

        Assert.True(result.IsSuccess);
        Assert.Equal(TimeSpan.FromSeconds(30), result.Time);
        Assert.Equal(new ItemRepaired(Pack, Knife, Chipped), result.Events.OfType<ItemRepaired>().Single());
        Assert.True(containers.TryGet(Pack, out var pack));
        Assert.Equal(0, pack.CountOf(Knife, chipped));
        Assert.Equal(1, pack.CountOf(Knife, ItemState.Create([new(WeaponService.ConditionValue, 70)])));
        Assert.Equal(1, pack.CountOf(Whetstone));
    }

    [Fact]
    public void Repair_OfOneItemInAStack_LeavesTheOthersFaulty()
    {
        var (repairs, inventory, containers) = Rig();
        var chipped = ItemFaults.With(null, Chipped);
        Assert.True(inventory.AddItems(Pack, Knife, 2, chipped).IsSuccess);
        Assert.True(inventory.AddItems(Pack, Whetstone, 1).IsSuccess);

        Assert.True(repairs.Repair(Pack, Knife, chipped, Chipped).IsSuccess);

        Assert.True(containers.TryGet(Pack, out var pack));
        Assert.Equal(1, pack.CountOf(Knife, chipped));
        Assert.Equal(1, pack.CountOf(Knife, null));
    }

    [Fact]
    public void Repair_WithoutTheRequiredItem_ChangesNothing()
    {
        var (repairs, inventory, containers) = Rig();
        var chipped = ItemFaults.With(null, Chipped);
        Assert.True(inventory.AddItems(Pack, Knife, 1, chipped).IsSuccess);
        Assert.True(inventory.AddItems(Pack, Kit, 1).IsSuccess);

        var result = repairs.Repair(Pack, Knife, chipped, Chipped);

        Assert.Equal(RepairError.RepairItemMissing, result.Error);
        Assert.True(containers.TryGet(Pack, out var pack));
        Assert.Equal(1, pack.CountOf(Knife, chipped));
        Assert.Equal(1, pack.CountOf(Kit));
    }

    [Fact]
    public void Repair_RefusesWhatCannotBeRepaired()
    {
        var (repairs, inventory, _) = Rig();
        var chipped = ItemFaults.With(null, Chipped);
        Assert.True(inventory.AddItems(Pack, Knife, 1).IsSuccess);
        Assert.True(inventory.AddItems(Pack, Whetstone, 1).IsSuccess);

        Assert.Equal(RepairError.UnknownFault, repairs.Repair(Pack, Knife, chipped, "test:fault/missing").Error);
        Assert.Equal(RepairError.NotFaulty, repairs.Repair(Pack, Knife, null, Chipped).Error);
        Assert.Equal(RepairError.ItemNotHeld, repairs.Repair(Pack, Knife, chipped, Chipped).Error);
        Assert.Equal(RepairError.UnknownContainer, repairs.Repair(new ContainerId(99), Knife, chipped, Chipped).Error);
    }

    [Fact]
    public void TheRepairAction_IsOnlyOfferedForItemsWithAFault()
    {
        var actions = new ItemActionService(
            new ItemActionCatalog([ItemActionJson.Parse("""{ "id": "test:item_action/repair", "label": "action.repair", "icon": "use", "group": "use", "order": 2, "appliesWhen": [ "faulty" ] }""")]),
            Items,
            Wearables,
            Weapons);
        var context = new ItemActionContext(IsOwnContainer: true, IsReadOnly: false);

        Assert.Empty(actions.Available(new ItemStack(new StackId(1), Knife, 1), context));
        Assert.Single(actions.Available(new ItemStack(new StackId(1), Knife, 1, ItemFaults.With(null, Chipped)), context));
    }
}
