using System.Globalization;

namespace Zombies.Persistence.Sqlite;

/// <summary>A mod as recorded in a save: its id, version, and content hash.</summary>
public sealed record SavedMod(string Id, string Version, string ContentHash);

/// <summary>What a save was made with: the world seed, the world generator version, and every mod that ran on the Server.</summary>
public sealed record SaveHeader(ulong WorldSeed, int GeneratorVersion, IReadOnlyList<SavedMod> Mods)
{
    /// <summary>
    /// Says why this save cannot be loaded by a build with <paramref name="generatorVersion"/> and <paramref name="mods"/>,
    /// one problem per entry. An empty list means the save loads. The seed is not compared: the save decides it.
    /// </summary>
    public IReadOnlyList<string> CheckAgainst(int generatorVersion, IReadOnlyList<SavedMod> mods)
    {
        ArgumentNullException.ThrowIfNull(mods);
        var problems = new List<string>();
        if (generatorVersion != GeneratorVersion)
        {
            problems.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"The save was made with world generator {GeneratorVersion}, but this build runs {generatorVersion}, so the saved chunk edits would land on different terrain."));
        }

        var running = mods.ToDictionary(m => m.Id, StringComparer.Ordinal);
        foreach (var saved in Mods.OrderBy(m => m.Id, StringComparer.Ordinal))
        {
            if (!running.TryGetValue(saved.Id, out var now))
            {
                problems.Add($"The save was made with mod '{saved.Id}' {saved.Version}, which is not loaded.");
            }
            else if (now.Version != saved.Version)
            {
                problems.Add($"Mod '{saved.Id}' was {saved.Version} when the save was made but is {now.Version} now.");
            }
            else if (!string.Equals(now.ContentHash, saved.ContentHash, StringComparison.Ordinal))
            {
                problems.Add($"Mod '{saved.Id}' {saved.Version} has different content than when the save was made.");
            }
        }

        var known = Mods.Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var now in mods.OrderBy(m => m.Id, StringComparer.Ordinal).Where(m => !known.Contains(m.Id)))
        {
            problems.Add($"Mod '{now.Id}' {now.Version} is loaded but was not part of the save.");
        }

        return problems;
    }
}

/// <summary>Stores the header of a save.</summary>
public interface ISaveHeaderRepository
{
    /// <summary>The stored header, or null for a save nothing has been written to yet.</summary>
    SaveHeader? Load();

    /// <summary>Stores the header, replacing any earlier one.</summary>
    void Write(SaveHeader header);
}

public sealed class SqliteSaveHeaderRepository(SaveDatabase database) : ISaveHeaderRepository
{
    private const string SeedKey = "world_seed";
    private const string GeneratorKey = "generator_version";

    public SaveHeader? Load()
    {
        string? seed = null;
        string? generator = null;
        using (var command = database.Command(null, "SELECT key, value FROM meta WHERE key IN ($seed, $generator)", ("$seed", SeedKey), ("$generator", GeneratorKey)))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                if (reader.GetString(0) == SeedKey)
                {
                    seed = reader.GetString(1);
                }
                else
                {
                    generator = reader.GetString(1);
                }
            }
        }

        if (seed is null && generator is null)
        {
            return null;
        }

        if (!ulong.TryParse(seed, NumberStyles.None, CultureInfo.InvariantCulture, out var worldSeed)
            || !int.TryParse(generator, NumberStyles.None, CultureInfo.InvariantCulture, out var generatorVersion))
        {
            throw new SaveCorruptException("The save header does not hold a world seed and a generator version.");
        }

        var mods = new List<SavedMod>();
        using var modCommand = database.Command(null, "SELECT id, version, content_hash FROM mods ORDER BY position");
        using var modReader = modCommand.ExecuteReader();
        while (modReader.Read())
        {
            mods.Add(new SavedMod(modReader.GetString(0), modReader.GetString(1), modReader.GetString(2)));
        }

        return new SaveHeader(worldSeed, generatorVersion, mods);
    }

    public void Write(SaveHeader header)
    {
        ArgumentNullException.ThrowIfNull(header);
        database.Transact(transaction =>
        {
            const string Upsert = "INSERT INTO meta (key, value) VALUES ($key, $value) ON CONFLICT (key) DO UPDATE SET value = excluded.value";
            database.Command(transaction, Upsert, ("$key", SeedKey), ("$value", header.WorldSeed.ToString(CultureInfo.InvariantCulture))).ExecuteNonQuery();
            database.Command(transaction, Upsert, ("$key", GeneratorKey), ("$value", header.GeneratorVersion.ToString(CultureInfo.InvariantCulture))).ExecuteNonQuery();
            database.Command(transaction, "DELETE FROM mods").ExecuteNonQuery();

            var position = 0;
            foreach (var mod in header.Mods)
            {
                database.Command(
                    transaction,
                    "INSERT INTO mods (position, id, version, content_hash) VALUES ($position, $id, $version, $hash)",
                    ("$position", position++),
                    ("$id", mod.Id),
                    ("$version", mod.Version),
                    ("$hash", mod.ContentHash)).ExecuteNonQuery();
            }
        });
    }
}
