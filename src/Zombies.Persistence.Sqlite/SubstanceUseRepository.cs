using Zombies.Domain.StatusEffects;

namespace Zombies.Persistence.Sqlite;

/// <summary>Stores each creature's tolerance and addiction to substances in the save.</summary>
public sealed class SqliteSubstanceUseRepository(SaveDatabase database) : ISubstanceUseRepository
{
    public IReadOnlyList<SubstanceUseSnapshot> Load(long owner)
    {
        var use = new List<SubstanceUseSnapshot>();
        using var command = database.Command(
            null,
            "SELECT substance, tolerance, is_addicted, since_last_use_seconds FROM substance_use WHERE owner_id = $owner ORDER BY substance",
            ("$owner", owner));
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var seconds = reader.GetDouble(3);
            if (!double.IsFinite(seconds) || Math.Abs(seconds) >= TimeSpan.MaxValue.TotalSeconds)
            {
                throw new SaveCorruptException($"The use of substance '{reader.GetString(0)}' by {owner} has an impossible time since last use.");
            }

            use.Add(new SubstanceUseSnapshot(reader.GetString(0), reader.GetDouble(1), reader.GetInt32(2) != 0, TimeSpan.FromSeconds(seconds)));
        }

        return use;
    }

    public void Save(long owner, IReadOnlyList<SubstanceUseSnapshot> use)
    {
        ArgumentNullException.ThrowIfNull(use);
        database.Transact(transaction =>
        {
            database.Command(transaction, "DELETE FROM substance_use WHERE owner_id = $owner", ("$owner", owner)).ExecuteNonQuery();
            foreach (var snapshot in use)
            {
                database.Command(
                    transaction,
                    "INSERT INTO substance_use (owner_id, substance, tolerance, is_addicted, since_last_use_seconds) VALUES ($owner, $substance, $tolerance, $addicted, $since)",
                    ("$owner", owner),
                    ("$substance", snapshot.Substance),
                    ("$tolerance", snapshot.Tolerance),
                    ("$addicted", snapshot.IsAddicted ? 1 : 0),
                    ("$since", snapshot.SinceLastUse.TotalSeconds)).ExecuteNonQuery();
            }
        });
    }
}
