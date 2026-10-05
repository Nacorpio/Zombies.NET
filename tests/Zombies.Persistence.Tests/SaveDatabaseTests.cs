using Microsoft.Data.Sqlite;
using Zombies.Persistence.Sqlite;

namespace Zombies.Persistence.Tests;

public sealed class SaveDatabaseTests
{
    private static readonly string[] V1 = ["CREATE TABLE thing (id INTEGER PRIMARY KEY, name TEXT NOT NULL);"];

    private static readonly string[] V2 = [.. V1, "ALTER TABLE thing ADD COLUMN size INTEGER NOT NULL DEFAULT 7;"];

    [Fact]
    public void Open_NewFile_IsAtTheCurrentSchemaVersion()
    {
        using var save = new TempSave();
        using var database = save.Open();

        Assert.Equal(SaveDatabase.CurrentSchemaVersion, database.SchemaVersion);
        Assert.True(File.Exists(save.Path));
    }

    [Fact]
    public void Open_Reopened_KeepsTheStoredSchemaVersionAndData()
    {
        using var save = new TempSave();
        using (var first = save.Open())
        {
            new SqliteSaveHeaderRepository(first).Write(new SaveHeader(42, 3, []));
        }

        using var second = save.Open();

        Assert.Equal(SaveDatabase.CurrentSchemaVersion, second.SchemaVersion);
        Assert.Equal(42UL, new SqliteSaveHeaderRepository(second).Load()!.WorldSeed);
    }

    [Fact]
    public void Open_OlderSchema_IsMigratedForwardWithoutLosingData()
    {
        using var save = new TempSave();
        using (var old = SaveDatabase.Open(save.Path, V1))
        {
            old.Transact(t => old.Command(t, "INSERT INTO thing (id, name) VALUES (1, 'crate')").ExecuteNonQuery());
        }

        using var migrated = SaveDatabase.Open(save.Path, V2);

        Assert.Equal(2, migrated.SchemaVersion);
        using var command = migrated.Command(null, "SELECT size FROM thing WHERE id = 1");
        Assert.Equal(7L, command.ExecuteScalar());
    }

    [Fact]
    public void Open_NewerSchema_IsRefusedAndLeftUntouched()
    {
        using var save = new TempSave();
        using (SaveDatabase.Open(save.Path, V2))
        {
        }

        var thrown = Assert.Throws<SaveTooNewException>(() => SaveDatabase.Open(save.Path, V1));

        Assert.Equal(2, thrown.Found);
        Assert.Equal(1, thrown.Supported);
        using var database = SaveDatabase.Open(save.Path, V2);
        Assert.Equal(2, database.SchemaVersion);
    }

    [Fact]
    public void Open_AFileThatIsNotADatabase_ThrowsASaveException()
    {
        using var save = new TempSave();
        File.WriteAllText(save.Path, "this is not a sqlite file, and it is long enough that sqlite will not mistake it for an empty one");

        Assert.Throws<SaveException>(() => save.Open());
    }

    [Fact]
    public void Transact_WhenTheWorkFails_StoresNothing()
    {
        using var save = new TempSave();
        using var database = save.Open();
        var header = new SqliteSaveHeaderRepository(database);

        Assert.Throws<SqliteException>(() => database.Transact(t =>
        {
            database.Command(t, "INSERT INTO meta (key, value) VALUES ('world_seed', '1')").ExecuteNonQuery();
            database.Command(t, "INSERT INTO meta (key, value) VALUES ('world_seed', '2')").ExecuteNonQuery();
        }));

        Assert.Null(header.Load());
    }
}
