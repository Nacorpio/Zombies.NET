using Microsoft.Data.Sqlite;
using Zombies.Domain.Combat;
using Zombies.Domain.Inventory;
using Zombies.Domain.Items;
using Zombies.Domain.Survival;

namespace Zombies.Persistence.Sqlite;

/// <summary>
/// Stores Containers in the save. <see cref="TryGet"/> returns a fresh copy each time, so keep the Container you loaded,
/// change it through the Inventory commands, and <see cref="Save"/> it when its state should be stored.
/// </summary>
public sealed class SqliteContainerRepository(SaveDatabase database, IItemCatalog catalog) : IContainerRepository
{
    public bool TryGet(ContainerId id, out Container container)
    {
        container = null!;
        ContainerSnapshot? header = null;
        using (var command = database.Command(null, "SELECT mass_limit_kg, volume_limit_m3, next_stack_id FROM containers WHERE id = $id", ("$id", id.Value)))
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read())
            {
                return false;
            }

            header = new ContainerSnapshot(id.Value, reader.GetDouble(0), reader.GetDouble(1), reader.GetInt32(2), []);
        }

        var values = new Dictionary<int, List<KeyValuePair<string, int>>>();
        using (var command = database.Command(null, "SELECT stack_id, name, value FROM stack_state_values WHERE container_id = $id ORDER BY stack_id, name", ("$id", id.Value)))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                Group(values, reader.GetInt32(0)).Add(new(reader.GetString(1), reader.GetInt32(2)));
            }
        }

        var attached = new Dictionary<int, List<KeyValuePair<string, int>>>();
        using (var command = database.Command(null, "SELECT stack_id, item, count FROM stack_state_attached WHERE container_id = $id ORDER BY stack_id, item", ("$id", id.Value)))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                Group(attached, reader.GetInt32(0)).Add(new(reader.GetString(1), reader.GetInt32(2)));
            }
        }

        var stacks = new List<StackSnapshot>();
        using (var command = database.Command(null, "SELECT stack_id, item, count FROM container_stacks WHERE container_id = $id ORDER BY stack_id", ("$id", id.Value)))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var stackId = reader.GetInt32(0);
                var hasState = values.ContainsKey(stackId) || attached.ContainsKey(stackId);
                var state = hasState ? new ItemStateSnapshot(values.GetValueOrDefault(stackId) ?? [], attached.GetValueOrDefault(stackId) ?? []) : null;
                stacks.Add(new StackSnapshot(stackId, reader.GetString(1), reader.GetInt32(2), state));
            }
        }

        try
        {
            container = Container.Restore(header with { Stacks = stacks }, catalog);
            return true;
        }
        catch (ArgumentException ex)
        {
            throw new SaveCorruptException($"{id} is not a valid Container: {ex.Message}", ex);
        }
    }

    private static List<KeyValuePair<string, int>> Group(Dictionary<int, List<KeyValuePair<string, int>>> groups, int stack)
    {
        if (!groups.TryGetValue(stack, out var list))
        {
            groups[stack] = list = [];
        }

        return list;
    }

    public bool TryAdd(Container container)
    {
        ArgumentNullException.ThrowIfNull(container);
        using var command = database.Command(null, "SELECT 1 FROM containers WHERE id = $id", ("$id", container.Id.Value));
        if (command.ExecuteScalar() is not null)
        {
            return false;
        }

        Save(container);
        return true;
    }

    public void Save(Container container)
    {
        ArgumentNullException.ThrowIfNull(container);
        var snapshot = container.ToSnapshot();
        database.Transact(transaction =>
        {
            database.Command(
                transaction,
                """
                INSERT INTO containers (id, mass_limit_kg, volume_limit_m3, next_stack_id) VALUES ($id, $mass, $volume, $next)
                ON CONFLICT (id) DO UPDATE SET mass_limit_kg = excluded.mass_limit_kg, volume_limit_m3 = excluded.volume_limit_m3, next_stack_id = excluded.next_stack_id
                """,
                ("$id", snapshot.Id),
                ("$mass", snapshot.MassLimitKg),
                ("$volume", snapshot.VolumeLimitM3),
                ("$next", snapshot.NextStackId)).ExecuteNonQuery();
            database.Command(transaction, "DELETE FROM container_stacks WHERE container_id = $id", ("$id", snapshot.Id)).ExecuteNonQuery();
            foreach (var stack in snapshot.Stacks)
            {
                database.Command(
                    transaction,
                    "INSERT INTO container_stacks (container_id, stack_id, item, count) VALUES ($id, $stack, $item, $count)",
                    ("$id", snapshot.Id),
                    ("$stack", stack.Id),
                    ("$item", stack.Item),
                    ("$count", stack.Count)).ExecuteNonQuery();

                foreach (var (name, value) in stack.State?.Values ?? [])
                {
                    database.Command(
                        transaction,
                        "INSERT INTO stack_state_values (container_id, stack_id, name, value) VALUES ($id, $stack, $name, $value)",
                        ("$id", snapshot.Id),
                        ("$stack", stack.Id),
                        ("$name", name),
                        ("$value", value)).ExecuteNonQuery();
                }

                foreach (var (item, count) in stack.State?.Attached ?? [])
                {
                    database.Command(
                        transaction,
                        "INSERT INTO stack_state_attached (container_id, stack_id, item, count) VALUES ($id, $stack, $item, $count)",
                        ("$id", snapshot.Id),
                        ("$stack", stack.Id),
                        ("$item", item),
                        ("$count", count)).ExecuteNonQuery();
                }
            }
        });
    }

    public IReadOnlyList<ContainerId> Ids()
    {
        var ids = new List<ContainerId>();
        using var command = database.Command(null, "SELECT id FROM containers ORDER BY id");
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            ids.Add(new ContainerId(reader.GetInt64(0)));
        }

        return ids;
    }
}

/// <summary>Stores Bodies in the save. Tuning comes from the <see cref="BodyConfig"/> given here, not from the file.</summary>
public sealed class SqliteBodyRepository(SaveDatabase database, BodyConfig? config = null) : IBodyRepository
{
    public bool TryGet(BodyId id, out Body body)
    {
        body = null!;
        bool isAlive;
        double blood;
        int nextWound;
        using (var command = database.Command(null, "SELECT is_alive, blood_liters, next_wound_id FROM bodies WHERE id = $id", ("$id", id.Value)))
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read())
            {
                return false;
            }

            isAlive = reader.GetInt64(0) != 0;
            blood = reader.GetDouble(1);
            nextWound = reader.GetInt32(2);
        }

        var parts = new List<PartSnapshot>();
        using (var command = database.Command(null, "SELECT part, health, is_missing FROM body_parts WHERE body_id = $id ORDER BY part", ("$id", id.Value)))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                parts.Add(new PartSnapshot((BodyPart)reader.GetInt32(0), reader.GetDouble(1), reader.GetInt64(2) != 0));
            }
        }

        var wounds = new List<WoundSnapshot>();
        using (var command = database.Command(
            null,
            "SELECT wound_id, part, type, severity, bleed_ml_per_min, is_bandaged, is_stump FROM body_wounds WHERE body_id = $id ORDER BY wound_id",
            ("$id", id.Value)))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                wounds.Add(new WoundSnapshot(
                    reader.GetInt32(0),
                    (BodyPart)reader.GetInt32(1),
                    (DamageType)reader.GetInt32(2),
                    reader.GetDouble(3),
                    reader.GetDouble(4),
                    reader.GetInt64(5) != 0,
                    reader.GetInt64(6) != 0));
            }
        }

        try
        {
            body = Body.Restore(new BodySnapshot(id.Value, isAlive, blood, nextWound, parts, wounds), config);
            return true;
        }
        catch (ArgumentException ex)
        {
            throw new SaveCorruptException($"{id} is not a valid Body: {ex.Message}", ex);
        }
    }

    public void Save(Body body)
    {
        ArgumentNullException.ThrowIfNull(body);
        var snapshot = body.ToSnapshot();
        database.Transact(transaction =>
        {
            database.Command(
                transaction,
                """
                INSERT INTO bodies (id, is_alive, blood_liters, next_wound_id) VALUES ($id, $alive, $blood, $next)
                ON CONFLICT (id) DO UPDATE SET is_alive = excluded.is_alive, blood_liters = excluded.blood_liters, next_wound_id = excluded.next_wound_id
                """,
                ("$id", snapshot.Id),
                ("$alive", snapshot.IsAlive ? 1 : 0),
                ("$blood", snapshot.BloodLiters),
                ("$next", snapshot.NextWoundId)).ExecuteNonQuery();
            database.Command(transaction, "DELETE FROM body_parts WHERE body_id = $id", ("$id", snapshot.Id)).ExecuteNonQuery();
            database.Command(transaction, "DELETE FROM body_wounds WHERE body_id = $id", ("$id", snapshot.Id)).ExecuteNonQuery();

            foreach (var part in snapshot.Parts)
            {
                database.Command(
                    transaction,
                    "INSERT INTO body_parts (body_id, part, health, is_missing) VALUES ($id, $part, $health, $missing)",
                    ("$id", snapshot.Id),
                    ("$part", (int)part.Part),
                    ("$health", part.Health),
                    ("$missing", part.IsMissing ? 1 : 0)).ExecuteNonQuery();
            }

            foreach (var wound in snapshot.Wounds)
            {
                database.Command(
                    transaction,
                    "INSERT INTO body_wounds (body_id, wound_id, part, type, severity, bleed_ml_per_min, is_bandaged, is_stump) VALUES ($id, $wound, $part, $type, $severity, $bleed, $bandaged, $stump)",
                    ("$id", snapshot.Id),
                    ("$wound", wound.Id),
                    ("$part", (int)wound.Part),
                    ("$type", (int)wound.Type),
                    ("$severity", wound.Severity),
                    ("$bleed", wound.BleedMillilitersPerMinute),
                    ("$bandaged", wound.IsBandaged ? 1 : 0),
                    ("$stump", wound.IsStump ? 1 : 0)).ExecuteNonQuery();
            }
        });
    }

    public IReadOnlyList<BodyId> Ids()
    {
        var ids = new List<BodyId>();
        using var command = database.Command(null, "SELECT id FROM bodies ORDER BY id");
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            ids.Add(new BodyId(reader.GetInt64(0)));
        }

        return ids;
    }
}

/// <summary>Stores the Needs of each character in the save.</summary>
public sealed class SqliteNeedsRepository(SaveDatabase database, NeedsConfig? config = null) : INeedsRepository
{
    public bool TryGet(long owner, out Needs needs)
    {
        needs = null!;
        using var command = database.Command(null, "SELECT satiety, hydration, body_celsius FROM needs WHERE owner_id = $owner", ("$owner", owner));
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return false;
        }

        try
        {
            needs = Needs.Restore(new NeedsSnapshot(reader.GetDouble(0), reader.GetDouble(1), reader.GetDouble(2)), config);
            return true;
        }
        catch (ArgumentException ex)
        {
            throw new SaveCorruptException($"The Needs of {owner} are not valid: {ex.Message}", ex);
        }
    }

    public void Save(long owner, Needs needs)
    {
        ArgumentNullException.ThrowIfNull(needs);
        var snapshot = needs.ToSnapshot();
        database.Transact(transaction => database.Command(
            transaction,
            """
            INSERT INTO needs (owner_id, satiety, hydration, body_celsius) VALUES ($owner, $satiety, $hydration, $celsius)
            ON CONFLICT (owner_id) DO UPDATE SET satiety = excluded.satiety, hydration = excluded.hydration, body_celsius = excluded.body_celsius
            """,
            ("$owner", owner),
            ("$satiety", snapshot.Satiety),
            ("$hydration", snapshot.Hydration),
            ("$celsius", snapshot.BodyCelsius)).ExecuteNonQuery());
    }

    public IReadOnlyList<long> Owners() => ReadOwners(database, "SELECT owner_id FROM needs ORDER BY owner_id");

    internal static List<long> ReadOwners(SaveDatabase database, string sql)
    {
        var owners = new List<long>();
        using var command = database.Command(null, sql);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            owners.Add(reader.GetInt64(0));
        }

        return owners;
    }
}

/// <summary>Stores the Outfit of each character in the save, in the order the items were put on.</summary>
public sealed class SqliteOutfitRepository(SaveDatabase database, IWearableCatalog catalog) : IOutfitRepository
{
    public bool TryGet(long owner, out Outfit outfit)
    {
        outfit = null!;
        using (var exists = database.Command(null, "SELECT 1 FROM outfits WHERE owner_id = $owner", ("$owner", owner)))
        {
            if (exists.ExecuteScalar() is null)
            {
                return false;
            }
        }

        var worn = new List<WornSnapshot>();
        using (var command = database.Command(null, "SELECT item, wetness, condition FROM outfit_items WHERE owner_id = $owner ORDER BY position", ("$owner", owner)))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                worn.Add(new WornSnapshot(reader.GetString(0), reader.GetDouble(1), reader.GetDouble(2)));
            }
        }

        try
        {
            outfit = Outfit.Restore(new OutfitSnapshot(worn), catalog);
            return true;
        }
        catch (ArgumentException ex)
        {
            throw new SaveCorruptException($"The Outfit of {owner} is not valid: {ex.Message}", ex);
        }
    }

    public void Save(long owner, Outfit outfit)
    {
        ArgumentNullException.ThrowIfNull(outfit);
        var snapshot = outfit.ToSnapshot();
        database.Transact(transaction =>
        {
            database.Command(transaction, "INSERT OR IGNORE INTO outfits (owner_id) VALUES ($owner)", ("$owner", owner)).ExecuteNonQuery();
            database.Command(transaction, "DELETE FROM outfit_items WHERE owner_id = $owner", ("$owner", owner)).ExecuteNonQuery();
            var position = 0;
            foreach (var worn in snapshot.Worn)
            {
                database.Command(
                    transaction,
                    "INSERT INTO outfit_items (owner_id, position, item, wetness, condition) VALUES ($owner, $position, $item, $wetness, $condition)",
                    ("$owner", owner),
                    ("$position", position++),
                    ("$item", worn.Item),
                    ("$wetness", worn.Wetness),
                    ("$condition", worn.Condition)).ExecuteNonQuery();
            }
        });
    }

    public IReadOnlyList<long> Owners() => SqliteNeedsRepository.ReadOwners(database, "SELECT owner_id FROM outfits ORDER BY owner_id");
}
