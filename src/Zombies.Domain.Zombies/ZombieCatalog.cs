using Zombies.Domain.Combat;

namespace Zombies.Domain.Zombies;

/// <summary>
/// Every Zombie type of the loaded mods. Types are numbered by Content ID order, which is the same on a Server and on a client
/// that passed the join check, so a zombie can name its type with a small number.
/// </summary>
public sealed class ZombieCatalog
{
    private readonly List<ZombieTypeDefinition> _ordered;
    private readonly Dictionary<string, int> _index;
    private readonly Dictionary<string, WeakpointSet> _weakpointSets = [];

    /// <exception cref="ArgumentException">A type is defined twice, names a Weakpoint set or an upgrade target that does not exist, holds something that is not a melee Weapon, or upgrades into itself through a chain.</exception>
    public ZombieCatalog(IEnumerable<ZombieTypeDefinition> types, IEnumerable<WeakpointSet>? weakpointSets = null, WeaponCatalog? weapons = null)
    {
        ArgumentNullException.ThrowIfNull(types);
        _ordered = [.. types.OrderBy(t => t.Id, StringComparer.Ordinal)];
        _index = [];
        for (var i = 0; i < _ordered.Count; i++)
        {
            if (!_index.TryAdd(_ordered[i].Id, i))
            {
                throw new ArgumentException($"Duplicate Zombie type '{_ordered[i].Id}'.", nameof(types));
            }
        }

        if (_ordered.Count > ushort.MaxValue)
        {
            throw new ArgumentException("Too many Zombie types.", nameof(types));
        }

        foreach (var set in weakpointSets ?? [])
        {
            if (!_weakpointSets.TryAdd(set.Id, set))
            {
                throw new ArgumentException($"Duplicate Weakpoint set '{set.Id}'.", nameof(weakpointSets));
            }
        }

        if (_ordered.FirstOrDefault(t => t.WeakpointSet is not null && !_weakpointSets.ContainsKey(t.WeakpointSet)) is { } type)
        {
            throw new ArgumentException($"Zombie type '{type.Id}' names Weakpoint set '{type.WeakpointSet}', which does not exist.", nameof(types));
        }

        if (_ordered.FirstOrDefault(t => t.Upgrade is not null && !_index.ContainsKey(t.Upgrade.ZombieType)) is { } orphan)
        {
            throw new ArgumentException($"Zombie type '{orphan.Id}' upgrades into '{orphan.Upgrade!.ZombieType}', which does not exist.", nameof(types));
        }

        Weapons = weapons;
        foreach (var held in _ordered.SelectMany(t => t.HeldWeapons.Where(h => h.Item is not null).Select(h => (Type: t, Item: h.Item!.Value))))
        {
            if (weapons is null || !weapons.TryGetWeapon(held.Item, out var weapon))
            {
                throw new ArgumentException($"Zombie type '{held.Type.Id}' holds '{held.Item}', which is not a Weapon.", nameof(weapons));
            }

            if (weapon.AmmoItem is not null)
            {
                throw new ArgumentException($"Zombie type '{held.Type.Id}' holds '{held.Item}', which is not a melee weapon.", nameof(weapons));
            }
        }

        foreach (var start in _ordered)
        {
            CheckUpgradeChain(start);
        }
    }

    public IReadOnlyList<ZombieTypeDefinition> Types => _ordered;

    /// <summary>The Weapons the held weapons of the types are looked up in, or null when none were given.</summary>
    public WeaponCatalog? Weapons { get; }

    public bool TryGet(string id, out ZombieTypeDefinition type)
    {
        if (_index.TryGetValue(id, out var index))
        {
            type = _ordered[index];
            return true;
        }

        type = null!;
        return false;
    }

    /// <summary>The Weakpoints of a type, or null when it has none.</summary>
    public WeakpointSet? WeakpointsOf(ZombieTypeDefinition type) =>
        type.WeakpointSet is { } id ? _weakpointSets[id] : null;

    /// <summary>
    /// The type a zombie of <paramref name="type"/> is once the world is <paramref name="worldDay"/> days old, following the
    /// chain of upgrades as far as the days reach. The same type and day always give the same answer, so no zombie needs to save it.
    /// </summary>
    public ZombieTypeDefinition TypeAt(ZombieTypeDefinition type, int worldDay)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentOutOfRangeException.ThrowIfNegative(worldDay);
        var current = type;
        while (current.Upgrade is { } upgrade && worldDay >= upgrade.AfterDays)
        {
            worldDay -= upgrade.AfterDays;
            current = _ordered[_index[upgrade.ZombieType]];
        }

        return current;
    }

    /// <summary>The number that names a type on the network, or -1.</summary>
    public int IndexOf(string id) => _index.GetValueOrDefault(id, -1);

    public ZombieTypeDefinition At(int index) => _ordered[index];

    private void CheckUpgradeChain(ZombieTypeDefinition start)
    {
        var seen = new List<string> { start.Id };
        for (var upgrade = start.Upgrade; upgrade is not null; upgrade = _ordered[_index[upgrade.ZombieType]].Upgrade)
        {
            if (seen.Contains(upgrade.ZombieType))
            {
                throw new ArgumentException($"Zombie types upgrade in a cycle: {string.Join(" -> ", seen.Append(upgrade.ZombieType))}.");
            }

            seen.Add(upgrade.ZombieType);
        }
    }
}
