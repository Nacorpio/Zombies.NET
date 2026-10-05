namespace Zombies.Domain.Zombies;

/// <summary>The seed, type, and Level from which a zombie's appearance, outfit, and Missing parts are derived.</summary>
public readonly record struct ZombieSpec(ulong Seed, string Type, int Level)
{
    /// <summary>
    /// The spec of this zombie once the world is <paramref name="worldDay"/> days old: the same seed, the type its upgrades have
    /// made it by then, and its Level held to what that type allows. Server and clients derive the same spec from the same day,
    /// so evolution needs no saved state.
    /// </summary>
    /// <exception cref="ArgumentException">The catalog has no type of this spec.</exception>
    public ZombieSpec At(ZombieCatalog catalog, int worldDay)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (!catalog.TryGet(Type, out var type))
        {
            throw new ArgumentException($"There is no Zombie type '{Type}'.", nameof(catalog));
        }

        var evolved = catalog.TypeAt(type, worldDay);
        return this with { Type = evolved.Id, Level = Math.Min(Level, evolved.TopLevel) };
    }
}
