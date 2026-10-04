using UnitsNet;
using Zombies.Domain.Combat;
using Zombies.Domain.Inventory;
using Zombies.Domain.Items;
using Zombies.Domain.Survival;

namespace Zombies.Domain.Tests;

public sealed class OutfitTests
{
    private static readonly ItemId Shirt = new("base:item/shirt");
    private static readonly ItemId Jacket = new("base:item/jacket");
    private static readonly ItemId Vest = new("base:item/kevlar_vest");
    private static readonly ItemId Helmet = new("base:item/helmet");
    private static readonly ItemId Hoodie = new("base:item/hoodie");
    private static readonly ItemId Rock = new("base:item/rock");

    private static readonly BodyPart[] UpperBody = [BodyPart.Torso, BodyPart.LeftArm, BodyPart.RightArm];

    private static ThermalResistance R(double value) => ThermalResistance.FromSquareMeterKelvinsPerWatt(value);

    private static Outfit NewOutfit() => new(new WearableCatalog(
    [
        new WearableDefinition(Shirt, ClothingLayer.Base, UpperBody, R(0.05)),
        new WearableDefinition(Jacket, ClothingLayer.Outer, UpperBody, R(0.15), new Dictionary<DamageType, double> { [DamageType.Cut] = 0.3 }),
        new WearableDefinition(Vest, ClothingLayer.Armor, [BodyPart.Torso], R(0.02), new Dictionary<DamageType, double> { [DamageType.Cut] = 0.5, [DamageType.Pierce] = 0.4 }),
        new WearableDefinition(Helmet, ClothingLayer.Armor, [BodyPart.Head], R(0.01), new Dictionary<DamageType, double> { [DamageType.Blunt] = 0.6 }),
        new WearableDefinition(Hoodie, ClothingLayer.Outer, [BodyPart.Torso], R(0.1)),
    ]));

    private static Outfit Dressed()
    {
        var outfit = NewOutfit();
        Assert.True(outfit.Equip(Shirt).IsSuccess);
        Assert.True(outfit.Equip(Jacket).IsSuccess);
        Assert.True(outfit.Equip(Vest).IsSuccess);
        return outfit;
    }

    [Fact]
    public void Equip_RaisesEventAndListsTheItem()
    {
        var outfit = NewOutfit();

        var result = outfit.Equip(Jacket);

        Assert.Equal(new ItemEquipped(Jacket, ClothingLayer.Outer), Assert.Single(result.Events));
        Assert.Equal([Jacket], outfit.WornItems);
    }

    [Fact]
    public void Equip_RejectsOccupiedLayerButAllowsOtherLayersAndNonOverlappingParts()
    {
        var outfit = NewOutfit();
        outfit.Equip(Jacket);

        Assert.Equal(InventoryError.LayerOccupied, outfit.Equip(Hoodie).Error);
        Assert.True(outfit.Equip(Shirt).IsSuccess);
        Assert.True(outfit.Equip(Vest).IsSuccess);
        Assert.True(outfit.Equip(Helmet).IsSuccess);
    }

    [Fact]
    public void Equip_RejectsItemsThatAreNotWearable()
    {
        Assert.Equal(InventoryError.NotWearable, NewOutfit().Equip(Rock).Error);
    }

    [Fact]
    public void Unequip_FreesTheLayer()
    {
        var outfit = NewOutfit();
        outfit.Equip(Jacket);

        var result = outfit.Unequip(Jacket);

        Assert.Equal(new ItemUnequipped(Jacket), Assert.Single(result.Events));
        Assert.True(outfit.Equip(Hoodie).IsSuccess);
        Assert.Equal(InventoryError.NotWorn, outfit.Unequip(Jacket).Error);
    }

    [Fact]
    public void Insulation_AddsUpAcrossLayersPerBodyPart()
    {
        var outfit = Dressed();

        Assert.Equal(0.22, outfit.Insulation(BodyPart.Torso).SquareMeterKelvinsPerWatt, 9);
        Assert.Equal(0.20, outfit.Insulation(BodyPart.LeftArm).SquareMeterKelvinsPerWatt, 9);
        Assert.Equal(0, outfit.Insulation(BodyPart.Head).SquareMeterKelvinsPerWatt, 9);
        Assert.Equal(0.62 / 6, outfit.AverageInsulation().SquareMeterKelvinsPerWatt, 9);
    }

    [Fact]
    public void Protection_CombinesLayersMultiplicatively()
    {
        var outfit = Dressed();

        Assert.Equal(0.65, outfit.Protection(BodyPart.Torso, DamageType.Cut), 9);
        Assert.Equal(0.4, outfit.Protection(BodyPart.Torso, DamageType.Pierce), 9);
        Assert.Equal(0.3, outfit.Protection(BodyPart.LeftArm, DamageType.Cut), 9);
        Assert.Equal(0, outfit.Protection(BodyPart.Head, DamageType.Cut), 9);
        Assert.Equal(0, outfit.Protection(BodyPart.Torso, DamageType.Bite), 9);
    }

    [Fact]
    public void WearState_WetnessCutsInsulationAndConditionCutsProtection()
    {
        var outfit = Dressed();

        Assert.True(outfit.SetWearState(Jacket, wetness: 1, condition: 0.5).IsSuccess);

        Assert.Equal(0.05 + (0.15 * 0.3) + 0.02, outfit.Insulation(BodyPart.Torso).SquareMeterKelvinsPerWatt, 9);
        Assert.Equal(1 - ((1 - 0.15) * 0.5), outfit.Protection(BodyPart.Torso, DamageType.Cut), 9);
    }

    [Fact]
    public void WearState_RejectsOutOfRangeValuesAndUnwornItems()
    {
        var outfit = Dressed();

        Assert.Equal(InventoryError.InvalidWearState, outfit.SetWearState(Jacket, 1.5, 1).Error);
        Assert.Equal(InventoryError.InvalidWearState, outfit.SetWearState(Jacket, 0, -0.1).Error);
        Assert.Equal(InventoryError.NotWorn, outfit.SetWearState(Helmet, 0, 1).Error);
    }

    [Fact]
    public void Wearable_RejectsInvalidDefinitions()
    {
        Assert.Throws<ArgumentException>(() => new WearableDefinition(Shirt, ClothingLayer.Base, [], R(0.1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WearableDefinition(Shirt, ClothingLayer.Base, UpperBody, R(0.1), new Dictionary<DamageType, double> { [DamageType.Cut] = 1.5 }));
    }

    [Fact]
    public void WornProtection_FeedsCombatDamage()
    {
        var outfit = Dressed();
        var body = new Body(new BodyId(1));

        body.TakeHit(BodyPart.Torso, DamageType.Cut, 20, outfit.Protection(BodyPart.Torso, DamageType.Cut));

        Assert.Equal(93, body.Health(BodyPart.Torso), 6);
    }

    [Fact]
    public void WornInsulation_FeedsSurvivalTemperature()
    {
        var bare = new Needs();
        var dressed = new Needs();
        var cold = Temperature.FromDegreesCelsius(15);
        var outfit = Dressed();

        bare.Advance(TimeSpan.FromHours(4), cold, ThermalResistance.Zero);
        dressed.Advance(TimeSpan.FromHours(4), cold, outfit.AverageInsulation());

        Assert.True(dressed.BodyTemperature > bare.BodyTemperature);
    }
}
