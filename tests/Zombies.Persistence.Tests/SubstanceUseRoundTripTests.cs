using Zombies.Domain.Items;
using Zombies.Domain.StatusEffects;
using Zombies.Persistence.Sqlite;

namespace Zombies.Persistence.Tests;

public sealed class SubstanceUseRoundTripTests
{
    private static readonly CreatureId Bob = new(100);
    private static readonly ItemId Pills = new("test:item/pills");

    private static readonly StatusEffectCatalog Effects = new(
        new[]
        {
            """{ "id": "test:status_effect/stimulated", "category": "buff", "duration": 600 }""",
            """{ "id": "test:status_effect/withdrawal", "category": "ailment", "stages": [ { "name": "craving", "after": 0 } ] }""",
        }.Select(StatusEffectJson.Parse));

    private static readonly SubstanceCatalog Catalog = new(
        [
            SubstanceJson.Parse("""
                { "id": "test:substance/stimulant", "items": [ { "item": "test:item/pills" } ], "statusEffect": "test:status_effect/stimulated",
                  "addictionChance": 0.5, "toleranceGrowth": 0.1, "withdrawal": { "statusEffect": "test:status_effect/withdrawal", "delay": 1000 } }
                """),
        ],
        Effects);

    [Fact]
    public void ToleranceAndAddiction_SavedAndReloaded_KeepWorking()
    {
        var original = new CreatureSubstances(Catalog, new CreatureEffects(Bob, Effects));
        original.Consume(Pills, 0);
        original.Consume(Pills, 0.9);
        original.Advance(TimeSpan.FromSeconds(400));
        using var save = new TempSave();
        using (var database = save.Open())
        {
            new SqliteSubstanceUseRepository(database).Save(100, original.ToSnapshot());
        }

        using var reopened = save.Open();
        var effects = new CreatureEffects(Bob, Effects);
        var loaded = CreatureSubstances.Restore(Catalog, effects, new SqliteSubstanceUseRepository(reopened).Load(100));

        Assert.Equal(original.ToSnapshot(), loaded.ToSnapshot());
        Assert.Equal(0.2, loaded.Tolerance("test:substance/stimulant"), 9);
        Assert.True(loaded.IsAddicted("test:substance/stimulant"));
        loaded.Advance(TimeSpan.FromSeconds(600));
        Assert.True(effects.Has("test:status_effect/withdrawal"));
    }

    [Fact]
    public void Saving_ReplacesWhatWasStoredForTheOwner_AndLeavesOthersAlone()
    {
        using var save = new TempSave();
        using var database = save.Open();
        var repository = new SqliteSubstanceUseRepository(database);
        repository.Save(1, [new SubstanceUseSnapshot("test:substance/stimulant", 0.3, true, TimeSpan.FromSeconds(5))]);
        repository.Save(2, [new SubstanceUseSnapshot("test:substance/stimulant", 0.1, false, TimeSpan.Zero)]);

        repository.Save(1, []);

        Assert.Empty(repository.Load(1));
        Assert.Equal(0.1, Assert.Single(repository.Load(2)).Tolerance);
        Assert.Empty(repository.Load(3));
    }

    [Fact]
    public void ASaveMadeBeforeSubstances_IsMigratedForwardAndCanStoreThem()
    {
        using var save = new TempSave();
        using (var old = SaveDatabase.Open(save.Path, SaveSchema.Migrations[..3]))
        {
            Assert.Equal(3, old.SchemaVersion);
        }

        using var migrated = save.Open();
        var repository = new SqliteSubstanceUseRepository(migrated);
        repository.Save(1, [new SubstanceUseSnapshot("test:substance/stimulant", 0.3, true, TimeSpan.FromSeconds(5))]);

        Assert.Equal(SaveDatabase.CurrentSchemaVersion, migrated.SchemaVersion);
        Assert.Equal(0.3, Assert.Single(repository.Load(1)).Tolerance);
    }

    [Fact]
    public void AnImpossibleStoredTimeSinceLastUse_ThrowsASaveCorruptException()
    {
        using var save = new TempSave();
        using var database = save.Open();
        var repository = new SqliteSubstanceUseRepository(database);
        repository.Save(1, [new SubstanceUseSnapshot("test:substance/stimulant", 0.3, true, TimeSpan.FromSeconds(5))]);
        database.Transact(t => database.Command(t, "UPDATE substance_use SET since_last_use_seconds = 1e300").ExecuteNonQuery());

        Assert.Throws<SaveCorruptException>(() => repository.Load(1));
    }
}
