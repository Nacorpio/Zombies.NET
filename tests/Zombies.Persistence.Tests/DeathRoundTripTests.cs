using System.Numerics;
using Zombies.Domain.Combat;
using Zombies.Domain.Death;
using Zombies.Domain.Inventory;
using Zombies.Domain.Items;
using Zombies.Persistence.Sqlite;
using static Zombies.Persistence.Tests.Fixtures;

namespace Zombies.Persistence.Tests;

public sealed class DeathRoundTripTests
{
    private static readonly ItemState Worn = ItemState.Create([new("condition", 40), new("rounds", 7)], [new(Scope, 1)]);

    private static DeathStores StoresIn(SaveDatabase database) =>
        new(new SqliteContainerRepository(database, Items), new SqliteCorpseRepository(database), new SqliteMemorialRepository(database));

    private static DeathReport DieCarryingARifle(DeathStores stores)
    {
        var carried = new Container(new ContainerId(1000), UnitsNet.Mass.FromKilograms(40), UnitsNet.Volume.FromLiters(60), Items);
        Assert.True(carried.TryAdd(Rifle, 1, Worn).IsSuccess);
        Assert.True(carried.TryAdd(Beans, 3).IsSuccess);
        var outfit = new Outfit(Wearables);
        return new DeathService(Items, stores).Die(carried, outfit, "alice", DeathCause.BloodLoss, 4, 9, new Vector3(10.5f, 64, -3.25f));
    }

    [Fact]
    public void Corpse_AndItsContainer_SurviveASaveAndLoad()
    {
        using var save = new TempSave();
        DeathReport report;
        using (var database = save.Open())
        {
            report = DieCarryingARifle(StoresIn(database));
        }

        using var reopened = save.Open();
        var stores = StoresIn(reopened);

        Assert.Equal(report.Corpse, Assert.Single(stores.Corpses.All()));
        Assert.True(stores.Containers.TryGet(report.Corpse.Container, out var corpse));
        Assert.Equal(1, corpse.CountOf(Rifle, Worn));
        Assert.Equal(3, corpse.CountOf(Beans));
    }

    [Fact]
    public void Memorial_SurvivesASaveAndLoad_InTheOrderItWasWritten()
    {
        using var save = new TempSave();
        using (var database = save.Open())
        {
            var stores = StoresIn(database);
            stores.Memorials.Add(new Memorial("alice", 4, 9, DeathCause.BloodLoss));
            stores.Memorials.Add(new Memorial("bob", 0, 0, DeathCause.Trauma));
        }

        using var reopened = save.Open();

        Assert.Equal([new Memorial("alice", 4, 9, DeathCause.BloodLoss), new Memorial("bob", 0, 0, DeathCause.Trauma)], StoresIn(reopened).Memorials.All());
    }

    [Fact]
    public void LootedCorpse_IsGoneAfterASaveAndLoad()
    {
        using var save = new TempSave();
        using (var database = save.Open())
        {
            var stores = StoresIn(database);
            var report = DieCarryingARifle(stores);
            var looter = new Container(new ContainerId(1001), UnitsNet.Mass.FromKilograms(40), UnitsNet.Volume.FromLiters(60), Items);
            Assert.True(new DeathService(Items, stores).Loot(report.Corpse, looter, out var emptied));
            Assert.True(emptied);
        }

        using var reopened = save.Open();

        Assert.Empty(StoresIn(reopened).Corpses.All());
        Assert.Single(StoresIn(reopened).Memorials.All());
    }

    [Fact]
    public void Corpse_ContainerIds_ContinuePastTheStoredOnes_AfterAReload()
    {
        using var save = new TempSave();
        DeathReport first;
        using (var database = save.Open())
        {
            first = DieCarryingARifle(StoresIn(database));
        }

        using var reopened = save.Open();

        Assert.True(new DeathService(Items, StoresIn(reopened)).NextContainerId().Value > first.Corpse.Container.Value);
    }

    [Fact]
    public void Save_FromBeforeDeath_IsMigratedAndThenStoresCorpses()
    {
        using var save = new TempSave();
        using (var old = SaveDatabase.Open(save.Path, SaveSchema.Migrations[..1]))
        {
            Assert.Equal(1, old.SchemaVersion);
        }

        using var migrated = save.Open();

        Assert.Equal(SaveSchema.CurrentVersion, migrated.SchemaVersion);
        Assert.Empty(StoresIn(migrated).Corpses.All());
        Assert.Equal("alice", DieCarryingARifle(StoresIn(migrated)).Corpse.Owner);
    }

    [Fact]
    public void Memorial_WithAnUnknownStoredCause_ThrowsASaveCorruptException()
    {
        using var save = new TempSave();
        using var database = save.Open();
        var memorials = new SqliteMemorialRepository(database);
        memorials.Add(new Memorial("alice", 1, 1, DeathCause.Trauma));
        database.Transact(t => database.Command(t, "UPDATE memorials SET cause = 99").ExecuteNonQuery());

        Assert.Throws<SaveCorruptException>(memorials.All);
    }
}
