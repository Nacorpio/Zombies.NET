using System.Numerics;
using UnitsNet;
using Zombies.Domain.Combat;
using Zombies.Domain.Death;
using Zombies.Domain.Inventory;
using Zombies.Domain.Items;

namespace Zombies.Domain.Tests;

public sealed class DeathTests
{
    private static readonly ItemId Beans = new("base:item/canned_beans");
    private static readonly ItemId Rifle = new("base:item/rifle");
    private static readonly ItemId Scope = new("base:item/scope");
    private static readonly ItemId Jacket = new("base:item/jacket");

    private static readonly ItemCatalog Items = new(
    [
        new ItemDefinition(Beans, Mass.FromKilograms(0.4), Volume.FromLiters(0.35), maxStack: 4),
        new ItemDefinition(Rifle, Mass.FromKilograms(3.5), Volume.FromLiters(4), maxStack: 1),
        new ItemDefinition(Scope, Mass.FromKilograms(0.5), Volume.FromLiters(0.4), maxStack: 1),
        new ItemDefinition(Jacket, Mass.FromKilograms(1), Volume.FromLiters(3), maxStack: 1),
    ]);

    private static readonly WearableCatalog Wearables = new(
    [
        new WearableDefinition(Jacket, ClothingLayer.Outer, [BodyPart.Torso], ThermalResistance.FromSquareMeterKelvinsPerWatt(0.1)),
    ]);

    private static readonly ItemState ScopedRifle = ItemState.Create([new("condition", 70), new("rounds", 5)], [new(Scope, 1)]);

    private static Container Pack(long id = 1000) => new(new ContainerId(id), Mass.FromKilograms(40), Volume.FromLiters(60), Items);

    private static Container Loaded()
    {
        var pack = Pack();
        Assert.True(pack.TryAdd(Beans, 6).IsSuccess);
        Assert.True(pack.TryAdd(Rifle, 1, ScopedRifle).IsSuccess);
        return pack;
    }

    private static DeathService Service(DeathStores? stores = null) => new(Items, stores ?? DeathStores.InMemory());

    [Fact]
    public void Death_MovesCarriedStacksIntoACorpse_WithTheirItemState()
    {
        var stores = DeathStores.InMemory();
        var carried = Loaded();

        var report = Service(stores).Die(carried, new Outfit(Wearables), "alice", DeathCause.Trauma, 3, 7, new Vector3(1, 2, 3));

        Assert.Empty(carried.Stacks);
        Assert.True(stores.Containers.TryGet(report.Corpse.Container, out var corpse));
        Assert.Equal(6, corpse.CountOf(Beans));
        Assert.Equal(1, corpse.CountOf(Rifle, ScopedRifle));
        Assert.Equal(new Vector3(1, 2, 3), report.Corpse.Position);
        Assert.Equal("alice", report.Corpse.Owner);
    }

    [Fact]
    public void Death_MovesWornItemsIntoTheCorpse_AndKeepsTheirWearState()
    {
        var stores = DeathStores.InMemory();
        var outfit = new Outfit(Wearables);
        Assert.True(outfit.Equip(Jacket).IsSuccess);
        Assert.True(outfit.SetWearState(Jacket, wetness: 0.5, condition: 0.8).IsSuccess);

        var report = Service(stores).Die(Pack(), outfit, "alice", DeathCause.BloodLoss, 0, 0, Vector3.Zero);

        Assert.Empty(outfit.WornItems);
        Assert.True(stores.Containers.TryGet(report.Corpse.Container, out var corpse));
        var worn = ItemState.Create([new(DeathService.WetnessState, 50), new(DeathService.ConditionState, 80)]);
        Assert.Equal(1, corpse.CountOf(Jacket, worn));
    }

    [Fact]
    public void Death_OfAWornItemInPristineShape_LeavesAnInterchangeableStack()
    {
        var stores = DeathStores.InMemory();
        var outfit = new Outfit(Wearables);
        Assert.True(outfit.Equip(Jacket).IsSuccess);

        var report = Service(stores).Die(Pack(), outfit, "alice", DeathCause.Trauma, 0, 0, Vector3.Zero);

        Assert.True(stores.Containers.TryGet(report.Corpse.Container, out var corpse));
        Assert.Equal(1, corpse.CountOf(Jacket, null));
    }

    [Fact]
    public void Death_RecordsAMemorialWithTheDeathCause()
    {
        var stores = DeathStores.InMemory();

        var report = Service(stores).Die(Pack(), new Outfit(Wearables), "alice", DeathCause.BloodLoss, 12, 40, Vector3.Zero);

        var memorial = Assert.Single(stores.Memorials.All());
        Assert.Equal(new Memorial("alice", 12, 40, DeathCause.BloodLoss), memorial);
        Assert.Equal(memorial, report.Memorial);
        Assert.Equal(report.Corpse, Assert.Single(stores.Corpses.All()));
    }

    [Fact]
    public void Death_GivesTheCorpseAFreshContainerId()
    {
        var stores = DeathStores.InMemory();
        var service = Service(stores);
        var first = service.Die(Pack(), new Outfit(Wearables), "alice", DeathCause.Trauma, 0, 0, Vector3.Zero);
        var second = service.Die(Pack(), new Outfit(Wearables), "bob", DeathCause.Trauma, 0, 0, Vector3.Zero);

        Assert.NotEqual(first.Corpse.Container, second.Corpse.Container);
        Assert.Equal(2, stores.Corpses.All().Count);
    }

    [Fact]
    public void Looting_MovesTheStacksToTheLooter_WithTheirItemState()
    {
        var stores = DeathStores.InMemory();
        var service = Service(stores);
        var report = service.Die(Loaded(), new Outfit(Wearables), "alice", DeathCause.Trauma, 0, 0, Vector3.Zero);
        var looter = Pack(1001);

        Assert.True(service.Loot(report.Corpse, looter, out var emptied));

        Assert.True(emptied);
        Assert.Equal(6, looter.CountOf(Beans));
        Assert.Equal(1, looter.CountOf(Rifle, ScopedRifle));
        Assert.Empty(stores.Corpses.All());
        Assert.Single(stores.Memorials.All());
    }

    [Fact]
    public void Looting_TakesWhatFits_AndLeavesTheCorpseWithTheRest()
    {
        var stores = DeathStores.InMemory();
        var service = Service(stores);
        var report = service.Die(Loaded(), new Outfit(Wearables), "alice", DeathCause.Trauma, 0, 0, Vector3.Zero);
        var small = new Container(new ContainerId(1001), Mass.FromKilograms(1.2), Volume.FromLiters(1), Items);

        Assert.True(service.Loot(report.Corpse, small, out var emptied));

        Assert.False(emptied);
        Assert.Equal(2, small.CountOf(Beans));
        Assert.Equal(0, small.CountOf(Rifle, ScopedRifle));
        Assert.True(stores.Containers.TryGet(report.Corpse.Container, out var corpse));
        Assert.Equal(4, corpse.CountOf(Beans));
        Assert.Equal(1, corpse.CountOf(Rifle, ScopedRifle));
        Assert.Single(stores.Corpses.All());
    }

    [Fact]
    public void Looting_AMissingContainer_Fails()
    {
        var service = Service();

        Assert.False(service.Loot(new Corpse(new ContainerId(99), "alice", Vector3.Zero), Pack(1001), out var emptied));
        Assert.False(emptied);
    }

    [Fact]
    public void Death_WithAnItemTheCatalogDoesNotKnow_RefusesInsteadOfLosingIt()
    {
        var unknown = new ItemId("base:item/mystery");
        var outfit = new Outfit(new WearableCatalog([new WearableDefinition(unknown, ClothingLayer.Base, [BodyPart.Torso], ThermalResistance.Zero)]));
        Assert.True(outfit.Equip(unknown).IsSuccess);

        Assert.Throws<InvalidOperationException>(() => Service().Die(Pack(), outfit, "alice", DeathCause.Trauma, 0, 0, Vector3.Zero));
    }

    [Fact]
    public void ContainerMoveFittingTo_IntoItself_Fails()
    {
        var pack = Loaded();

        Assert.Equal(InventoryError.SameContainer, pack.MoveFittingTo(pack).Error);
    }
}
