namespace Zombies.Domain.Zombies;

/// <summary>
/// Every Zombie type of the loaded mods. Types are numbered by Content ID order, which is the same on a Server and on a client
/// that passed the join check, so a zombie can name its type with a small number.
/// </summary>
public sealed class ZombieCatalog
{
    private readonly List<ZombieTypeDefinition> _ordered;
    private readonly Dictionary<string, int> _index;

    public ZombieCatalog(IEnumerable<ZombieTypeDefinition> types)
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
    }

    public IReadOnlyList<ZombieTypeDefinition> Types => _ordered;

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

    /// <summary>The number that names a type on the network, or -1.</summary>
    public int IndexOf(string id) => _index.GetValueOrDefault(id, -1);

    public ZombieTypeDefinition At(int index) => _ordered[index];
}
