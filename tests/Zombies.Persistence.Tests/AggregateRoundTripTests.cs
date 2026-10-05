using Zombies.Domain.Combat;
using Zombies.Domain.Inventory;
using Zombies.Domain.Items;
using Zombies.Domain.Survival;
using Zombies.Persistence.Sqlite;
using static Zombies.Persistence.Tests.Fixtures;

namespace Zombies.Persistence.Tests;

public sealed class AggregateRoundTripTests
{
    private static readonly ContainerId Backpack = new(1);
    private static readonly ContainerId Crate = new(2);

    private static Container FilledBackpack()
    {
        var repository = new InMemoryContainerRepository();
        var service = new InventoryService(Items, repository);
        Assert.True(service.AddContainer(new Container(Backpack, UnitsNet.Mass.FromKilograms(10), UnitsNet.Volume.FromLiters(10), Items)).IsSuccess);
        Assert.True(service.AddItems(Backpack, Beans, 6).IsSuccess);
        Assert.True(service.AddItems(Backpack, Water, 1).IsSuccess);
        Assert.True(service.SplitStack(Backpack, new StackId(1), 1).IsSuccess);
        repository.TryGet(Backpack, out var container);
        return container;
    }

    [Fact]
    public void Container_SavedAndReloaded_HasTheSameLimitsAndStacks()
    {
        using var save = new TempSave();
        var original = FilledBackpack();
        using (var database = save.Open())
        {
            new SqliteContainerRepository(database, Items).Save(original);
        }

        using var reopened = save.Open();
        Assert.True(new SqliteContainerRepository(reopened, Items).TryGet(Backpack, out var loaded));

        Assert.Equal(original.Stacks, loaded.Stacks);
        Assert.Equal(original.MassLimit.Kilograms, loaded.MassLimit.Kilograms, 9);
        Assert.Equal(original.VolumeLimit.CubicMeters, loaded.VolumeLimit.CubicMeters, 12);
        Assert.Equal(original.TotalMass.Kilograms, loaded.TotalMass.Kilograms, 9);
    }

    [Fact]
    public void Container_Reloaded_KeepsHandingOutFreshStackIds()
    {
        using var save = new TempSave();
        using var database = save.Open();
        var repository = new SqliteContainerRepository(database, Items);
        var original = FilledBackpack();
        var lastId = original.Stacks.Max(s => s.Id.Value);
        repository.Save(original);
        Assert.True(repository.TryGet(Backpack, out var loaded));

        var memory = new InMemoryContainerRepository();
        Assert.True(memory.TryAdd(loaded));
        var split = new InventoryService(Items, memory).SplitStack(Backpack, new StackId(1), 1);

        Assert.True(split.IsSuccess);
        var ids = loaded.Stacks.Select(s => s.Id.Value).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.True(ids.Max() > lastId);
    }

    [Fact]
    public void Container_SavedAgainAfterItChanged_ReplacesTheStoredStacks()
    {
        using var save = new TempSave();
        using var database = save.Open();
        var repository = new SqliteContainerRepository(database, Items);
        var container = FilledBackpack();
        repository.Save(container);
        var memory = new InMemoryContainerRepository();
        memory.Save(container);
        Assert.True(new InventoryService(Items, memory).RemoveItems(Backpack, Beans, 6).IsSuccess);

        repository.Save(container);

        Assert.True(repository.TryGet(Backpack, out var loaded));
        Assert.Equal(container.Stacks, loaded.Stacks);
        Assert.Equal(0, loaded.CountOf(Beans));
    }

    [Fact]
    public void Container_TryGetOfAnUnknownId_ReturnsFalse()
    {
        using var save = new TempSave();
        using var database = save.Open();

        Assert.False(new SqliteContainerRepository(database, Items).TryGet(Crate, out _));
    }

    [Fact]
    public void Container_TryAdd_RefusesAnIdThatIsAlreadyStored()
    {
        using var save = new TempSave();
        using var database = save.Open();
        var repository = new SqliteContainerRepository(database, Items);

        Assert.True(repository.TryAdd(FilledBackpack()));
        Assert.False(repository.TryAdd(new Container(Backpack, UnitsNet.Mass.FromKilograms(1), UnitsNet.Volume.FromLiters(1), Items)));
        Assert.Equal([Backpack], repository.Ids());
    }

    [Fact]
    public void Container_WithAStoredCountOfZero_ThrowsASaveCorruptException()
    {
        using var save = new TempSave();
        using var database = save.Open();
        var repository = new SqliteContainerRepository(database, Items);
        repository.Save(FilledBackpack());
        database.Transact(t => database.Command(t, "UPDATE container_stacks SET count = 0").ExecuteNonQuery());

        Assert.Throws<SaveCorruptException>(() => repository.TryGet(Backpack, out _));
    }

    private static InMemoryContainerRepository Armory(out InventoryService service)
    {
        var memory = new InMemoryContainerRepository();
        service = new InventoryService(Items, memory);
        Assert.True(service.AddContainer(new Container(Crate, UnitsNet.Mass.FromKilograms(50), UnitsNet.Volume.FromLiters(50), Items)).IsSuccess);
        return memory;
    }

    [Fact]
    public void Container_WithItemState_KeepsTheValuesAndAttachmentsOfEachStack()
    {
        var memory = Armory(out var service);
        var worn = ItemState.Create([new("condition", 40), new("rounds", 7)], [new(Scope, 1)]);
        var fresh = ItemState.Create([new("condition", 90)]);
        Assert.True(service.AddItems(Crate, Rifle, 1, worn).IsSuccess);
        Assert.True(service.AddItems(Crate, Rifle, 1, fresh).IsSuccess);
        Assert.True(service.AddItems(Crate, Beans, 2).IsSuccess);
        memory.TryGet(Crate, out var original);
        using var save = new TempSave();
        using (var database = save.Open())
        {
            new SqliteContainerRepository(database, Items).Save(original);
        }

        using var reopened = save.Open();
        Assert.True(new SqliteContainerRepository(reopened, Items).TryGet(Crate, out var loaded));

        Assert.Equal(original.Stacks, loaded.Stacks);
        Assert.Equal(3, loaded.Stacks.Count);
        Assert.Equal(1, loaded.CountOf(Rifle, worn));
        Assert.Equal(1, loaded.CountOf(Rifle, fresh));
        Assert.Equal(original.TotalMass.Kilograms, loaded.TotalMass.Kilograms, 9);
    }

    [Fact]
    public void Container_RestoredFromAnEmptyState_HoldsTheStackWithNoState()
    {
        var snapshot = new ContainerSnapshot(Crate.Value, 50, 0.05, 2, [new StackSnapshot(1, Beans.Value, 2, new ItemStateSnapshot([], []))]);

        var container = Container.Restore(snapshot, Items);

        Assert.Null(Assert.Single(container.Stacks).State);
    }
    [Fact]
    public void Container_SavedAgainAfterAStatefulStackWasRemoved_LeavesNoStateBehind()
    {
        var memory = Armory(out var service);
        var state = ItemState.Create([new("condition", 40)], [new(Scope, 1)]);
        Assert.True(service.AddItems(Crate, Rifle, 1, state).IsSuccess);
        memory.TryGet(Crate, out var container);
        using var save = new TempSave();
        using var database = save.Open();
        var repository = new SqliteContainerRepository(database, Items);
        repository.Save(container);
        Assert.True(service.RemoveItems(Crate, Rifle, 1, state).IsSuccess);

        repository.Save(container);

        foreach (var table in new[] { "stack_state_values", "stack_state_attached", "container_stacks" })
        {
            using var count = database.Command(null, $"SELECT COUNT(*) FROM {table}");
            Assert.Equal(0L, count.ExecuteScalar());
        }
    }

    [Fact]
    public void Container_WithAnImpossibleStoredAttachmentCount_ThrowsASaveCorruptException()
    {
        var memory = Armory(out var service);
        Assert.True(service.AddItems(Crate, Rifle, 1, ItemState.Create(attached: [new(Scope, 1)])).IsSuccess);
        memory.TryGet(Crate, out var container);
        using var save = new TempSave();
        using var database = save.Open();
        var repository = new SqliteContainerRepository(database, Items);
        repository.Save(container);
        database.Transact(t => database.Command(t, "UPDATE stack_state_attached SET count = 0").ExecuteNonQuery());

        Assert.Throws<SaveCorruptException>(() => repository.TryGet(Crate, out _));
    }

    [Fact]
    public void Body_WoundedAndMissingAPart_SurvivesASaveAndKeepsBleedingTheSame()
    {
        var original = new Body(new BodyId(10));
        Assert.True(original.TakeHit(BodyPart.Torso, DamageType.Cut, 30).IsSuccess);
        Assert.True(original.TakeHit(BodyPart.LeftArm, DamageType.Cut, 150).IsSuccess);
        Assert.True(original.Advance(TimeSpan.FromMinutes(10)).IsSuccess);
        using var save = new TempSave();
        using (var database = save.Open())
        {
            new SqliteBodyRepository(database).Save(original);
        }

        using var reopened = save.Open();
        Assert.True(new SqliteBodyRepository(reopened).TryGet(new BodyId(10), out var loaded));

        var before = original.ToSnapshot();
        var after = loaded.ToSnapshot();
        Assert.Equal(before.Parts, after.Parts);
        Assert.Equal(before.Wounds, after.Wounds);
        Assert.Equal(before.BloodLiters, after.BloodLiters, 12);
        Assert.Equal([BodyPart.LeftArm], loaded.MissingParts);

        original.Advance(TimeSpan.FromMinutes(5));
        loaded.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(original.BloodVolume.Liters, loaded.BloodVolume.Liters, 12);
    }

    [Fact]
    public void Body_Reloaded_NumbersItsNextWoundAfterTheSavedOnes()
    {
        var original = new Body(new BodyId(11));
        original.TakeHit(BodyPart.Torso, DamageType.Cut, 10);
        using var save = new TempSave();
        using var database = save.Open();
        var repository = new SqliteBodyRepository(database);
        repository.Save(original);
        Assert.True(repository.TryGet(new BodyId(11), out var loaded));

        loaded.TakeHit(BodyPart.Head, DamageType.Cut, 10);

        Assert.Equal(2, loaded.Wounds.Select(w => w.Id.Value).Distinct().Count());
    }

    [Fact]
    public void Body_ThatDied_StaysDeadAfterReloading()
    {
        var original = new Body(new BodyId(12));
        original.TakeHit(BodyPart.Torso, DamageType.Blunt, 500);
        Assert.False(original.IsAlive);
        using var save = new TempSave();
        using var database = save.Open();
        var repository = new SqliteBodyRepository(database);
        repository.Save(original);

        Assert.True(repository.TryGet(new BodyId(12), out var loaded));
        Assert.False(loaded.IsAlive);
        Assert.Equal(CombatError.AlreadyDead, loaded.TakeHit(BodyPart.Head, DamageType.Blunt, 1).Error);
    }

    [Fact]
    public void Body_SavedAgain_ReplacesItsWounds()
    {
        var body = new Body(new BodyId(13));
        body.TakeHit(BodyPart.Torso, DamageType.Cut, 20);
        using var save = new TempSave();
        using var database = save.Open();
        var repository = new SqliteBodyRepository(database);
        repository.Save(body);
        body.Bandage(BodyPart.Torso);

        repository.Save(body);

        Assert.True(repository.TryGet(new BodyId(13), out var loaded));
        Assert.All(loaded.Wounds, w => Assert.True(w.IsBandaged));
        Assert.Equal([new BodyId(13)], repository.Ids());
    }

    [Fact]
    public void Body_WithANegativeStoredHealth_ThrowsASaveCorruptException()
    {
        using var save = new TempSave();
        using var database = save.Open();
        var repository = new SqliteBodyRepository(database);
        repository.Save(new Body(new BodyId(14)));
        database.Transact(t => database.Command(t, "UPDATE body_parts SET health = -5 WHERE part = 0").ExecuteNonQuery());

        Assert.Throws<SaveCorruptException>(() => repository.TryGet(new BodyId(14), out _));
    }

    [Fact]
    public void Needs_SavedAfterTimePassed_ReloadWithTheSameValues()
    {
        var original = new Needs();
        original.Advance(TimeSpan.FromHours(6), UnitsNet.Temperature.FromDegreesCelsius(5), UnitsNet.ThermalResistance.Zero);
        using var save = new TempSave();
        using (var database = save.Open())
        {
            new SqliteNeedsRepository(database).Save(100, original);
        }

        using var reopened = save.Open();
        var repository = new SqliteNeedsRepository(reopened);
        Assert.True(repository.TryGet(100, out var loaded));

        Assert.Equal(original.ToSnapshot(), loaded.ToSnapshot());
        Assert.Equal(original.Warmth, loaded.Warmth);
        Assert.Equal([100L], repository.Owners());
        Assert.False(repository.TryGet(101, out _));
    }

    [Fact]
    public void Needs_WithAnImpossibleStoredTemperature_ThrowsASaveCorruptException()
    {
        using var save = new TempSave();
        using var database = save.Open();
        var repository = new SqliteNeedsRepository(database);
        repository.Save(1, new Needs());
        database.Transact(t => database.Command(t, "UPDATE needs SET body_celsius = 400").ExecuteNonQuery());

        Assert.Throws<SaveCorruptException>(() => repository.TryGet(1, out _));
    }

    [Fact]
    public void Outfit_WithWearState_SurvivesASaveInTheSameOrder()
    {
        var original = new Outfit(Wearables);
        Assert.True(original.Equip(Shirt).IsSuccess);
        Assert.True(original.Equip(Jacket).IsSuccess);
        Assert.True(original.SetWearState(Jacket, 0.25, 0.5).IsSuccess);
        using var save = new TempSave();
        using (var database = save.Open())
        {
            new SqliteOutfitRepository(database, Wearables).Save(5, original);
        }

        using var reopened = save.Open();
        Assert.True(new SqliteOutfitRepository(reopened, Wearables).TryGet(5, out var loaded));

        Assert.Equal(original.WornItems, loaded.WornItems);
        Assert.Equal(original.ToSnapshot().Worn, loaded.ToSnapshot().Worn);
        Assert.Equal(original.Protection(BodyPart.Torso, DamageType.Cut), loaded.Protection(BodyPart.Torso, DamageType.Cut), 12);
    }

    [Fact]
    public void Outfit_ThatWearsNothing_ReloadsAsAnEmptyOutfit()
    {
        using var save = new TempSave();
        using var database = save.Open();
        var repository = new SqliteOutfitRepository(database, Wearables);
        repository.Save(6, new Outfit(Wearables));

        Assert.True(repository.TryGet(6, out var loaded));
        Assert.Empty(loaded.WornItems);
        Assert.Equal([6L], repository.Owners());
        Assert.False(repository.TryGet(7, out _));
    }

    [Fact]
    public void Outfit_SavedAgainAfterUndressing_ReplacesTheStoredItems()
    {
        var outfit = new Outfit(Wearables);
        outfit.Equip(Shirt);
        using var save = new TempSave();
        using var database = save.Open();
        var repository = new SqliteOutfitRepository(database, Wearables);
        repository.Save(8, outfit);
        outfit.Unequip(Shirt);

        repository.Save(8, outfit);

        Assert.True(repository.TryGet(8, out var loaded));
        Assert.Empty(loaded.WornItems);
    }

    [Fact]
    public void Outfit_StoredWithAnItemThatCannotBeWornAnymore_ThrowsASaveCorruptException()
    {
        var outfit = new Outfit(Wearables);
        outfit.Equip(Shirt);
        using var save = new TempSave();
        using var database = save.Open();
        new SqliteOutfitRepository(database, Wearables).Save(9, outfit);

        var withoutShirt = new WearableCatalog([]);

        Assert.Throws<SaveCorruptException>(() => new SqliteOutfitRepository(database, withoutShirt).TryGet(9, out _));
    }

    [Fact]
    public void EverythingInOneSave_ComesBackAfterTheFileIsClosedAndReopened()
    {
        using var save = new TempSave();
        using (var database = save.Open())
        {
            new SqliteSaveHeaderRepository(database).Write(new SaveHeader(12345, 1, [new SavedMod("base", "0.1.0", "abc")]));
            new SqliteContainerRepository(database, Items).Save(FilledBackpack());
            new SqliteBodyRepository(database).Save(new Body(new BodyId(1)));
            new SqliteNeedsRepository(database).Save(1, new Needs());
            new SqliteChunkEditRepository(database).Save(new Zombies.Engine.Voxel.ChunkCoord(0, 0), [new BlockEdit(1, 1, 1, Zombies.Engine.Voxel.Blocks.Dirt)]);
        }

        using var reopened = save.Open();

        Assert.Equal(12345UL, new SqliteSaveHeaderRepository(reopened).Load()!.WorldSeed);
        Assert.Equal([Backpack], new SqliteContainerRepository(reopened, Items).Ids());
        Assert.Equal([new BodyId(1)], new SqliteBodyRepository(reopened).Ids());
        Assert.Equal([1L], new SqliteNeedsRepository(reopened).Owners());
        Assert.Single(new SqliteChunkEditRepository(reopened).EditedChunks());
    }
}
