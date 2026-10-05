using System.Numerics;
using Zombies.Domain.Combat;
using Zombies.Domain.Death;
using Zombies.Domain.Inventory;

namespace Zombies.Persistence.Sqlite;

/// <summary>Stores the Corpses in the save. A Corpse's Container is stored by <see cref="SqliteContainerRepository"/>.</summary>
public sealed class SqliteCorpseRepository(SaveDatabase database) : ICorpseRepository
{
    public void Save(Corpse corpse)
    {
        ArgumentNullException.ThrowIfNull(corpse);
        database.Transact(transaction => database.Command(
            transaction,
            """
            INSERT INTO corpses (container_id, owner, x, y, z) VALUES ($container, $owner, $x, $y, $z)
            ON CONFLICT (container_id) DO UPDATE SET owner = excluded.owner, x = excluded.x, y = excluded.y, z = excluded.z
            """,
            ("$container", corpse.Container.Value),
            ("$owner", corpse.Owner),
            ("$x", (double)corpse.Position.X),
            ("$y", (double)corpse.Position.Y),
            ("$z", (double)corpse.Position.Z)).ExecuteNonQuery());
    }

    public void Remove(ContainerId container) =>
        database.Transact(transaction => database.Command(transaction, "DELETE FROM corpses WHERE container_id = $container", ("$container", container.Value)).ExecuteNonQuery());

    public IReadOnlyList<Corpse> All()
    {
        var corpses = new List<Corpse>();
        using var command = database.Command(null, "SELECT container_id, owner, x, y, z FROM corpses ORDER BY container_id");
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            corpses.Add(new Corpse(new ContainerId(reader.GetInt64(0)), reader.GetString(1), new Vector3((float)reader.GetDouble(2), (float)reader.GetDouble(3), (float)reader.GetDouble(4))));
        }

        return corpses;
    }
}

/// <summary>Stores the Memorials in the save, oldest first.</summary>
public sealed class SqliteMemorialRepository(SaveDatabase database) : IMemorialRepository
{
    public void Add(Memorial memorial)
    {
        ArgumentNullException.ThrowIfNull(memorial);
        database.Transact(transaction => database.Command(
            transaction,
            "INSERT INTO memorials (player, days_survived, kills, cause) VALUES ($player, $days, $kills, $cause)",
            ("$player", memorial.Player),
            ("$days", memorial.DaysSurvived),
            ("$kills", memorial.Kills),
            ("$cause", (int)memorial.Cause)).ExecuteNonQuery());
    }

    public IReadOnlyList<Memorial> All()
    {
        var memorials = new List<Memorial>();
        using var command = database.Command(null, "SELECT player, days_survived, kills, cause FROM memorials ORDER BY id");
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var cause = reader.GetInt32(3);
            if (!Enum.IsDefined((DeathCause)cause))
            {
                throw new SaveCorruptException($"A Memorial names an unknown cause of death, {cause}.");
            }

            memorials.Add(new Memorial(reader.GetString(0), reader.GetInt32(1), reader.GetInt32(2), (DeathCause)cause));
        }

        return memorials;
    }
}
