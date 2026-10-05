using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Zombies.Persistence.Sqlite;

/// <summary>
/// One save file: a SQLite database with the world seed, generator version, mod list, chunk edits, and domain aggregates.
/// Opening a file creates it, migrates an older schema forward, and refuses a newer one. Not thread-safe: use one
/// <see cref="SaveDatabase"/> from one thread at a time.
/// </summary>
public sealed class SaveDatabase : IDisposable
{
    private readonly SqliteConnection _connection;

    private SaveDatabase(SqliteConnection connection, int schemaVersion)
    {
        _connection = connection;
        SchemaVersion = schemaVersion;
    }

    /// <summary>The schema version of the file, which is <see cref="CurrentSchemaVersion"/> once it has been opened.</summary>
    public int SchemaVersion { get; }

    /// <summary>The schema version this build writes.</summary>
    public static int CurrentSchemaVersion => SaveSchema.CurrentVersion;

    /// <summary>Opens the save at <paramref name="path"/>, creating it when it does not exist.</summary>
    public static SaveDatabase Open(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Open(path, SaveSchema.Migrations);
    }

    internal static SaveDatabase Open(string path, string[] migrations)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            ForeignKeys = true,
        }.ToString());

        try
        {
            connection.Open();
            var version = Migrate(connection, migrations);
            return new SaveDatabase(connection, version);
        }
        catch (SqliteException ex)
        {
            connection.Dispose();
            throw new SaveException($"'{path}' could not be opened as a save: {ex.Message}", ex);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    public void Dispose() => _connection.Dispose();

    internal SqliteCommand Command(SqliteTransaction? transaction, string sql, params (string Name, object? Value)[] values)
    {
        var command = _connection.CreateCommand();
        command.Transaction = transaction;
#pragma warning disable CA2100 // Every caller passes a constant; values only ever travel as parameters.
        command.CommandText = sql;
#pragma warning restore CA2100
        foreach (var (name, value) in values)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command;
    }

    /// <summary>Runs <paramref name="work"/> in one transaction: all of it is stored or none of it is.</summary>
    internal void Transact(Action<SqliteTransaction> work)
    {
        using var transaction = _connection.BeginTransaction();
        work(transaction);
        transaction.Commit();
    }

    private static int Migrate(SqliteConnection connection, string[] migrations)
    {
        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA user_version";
            var found = Convert.ToInt32(pragma.ExecuteScalar(), CultureInfo.InvariantCulture);
            if (found > migrations.Length)
            {
                throw new SaveTooNewException(found, migrations.Length);
            }

            for (var version = found; version < migrations.Length; version++)
            {
                using var transaction = connection.BeginTransaction();
                using var script = connection.CreateCommand();
                script.Transaction = transaction;
#pragma warning disable CA2100 // The scripts are constants in SaveSchema.
                script.CommandText = migrations[version];
#pragma warning restore CA2100
                script.ExecuteNonQuery();

                script.CommandText = string.Create(CultureInfo.InvariantCulture, $"PRAGMA user_version = {version + 1}");
                script.ExecuteNonQuery();
                transaction.Commit();
            }
        }

        return migrations.Length;
    }
}
