namespace Zombies.Engine.Ecs;

/// <summary>Adds the components of a Trait to a zombie's entity, reading the numbers its Zombie type gave it.</summary>
public delegate void TraitFactory(EcsWorld world, Entity entity, IReadOnlyDictionary<string, double> values);

/// <summary>
/// What each Trait does, by Content ID. A Trait is a name in a Zombie type's JSON and one or more ECS components added when a zombie
/// of that type spawns. The base game registers its own through <see cref="BaseTraits"/>; a Code mod registers more here with
/// a new component type, and Zombie types in any mod can then list them.
/// </summary>
public sealed class TraitRegistry
{
    private readonly Dictionary<string, TraitFactory> _factories = new(StringComparer.Ordinal);

    public IEnumerable<string> Traits => _factories.Keys.Order(StringComparer.Ordinal);

    /// <exception cref="InvalidOperationException">A Trait with that id is already registered.</exception>
    public void Register(string trait, TraitFactory factory)
    {
        ArgumentException.ThrowIfNullOrEmpty(trait);
        ArgumentNullException.ThrowIfNull(factory);
        if (!_factories.TryAdd(trait, factory))
        {
            throw new InvalidOperationException($"The Trait '{trait}' is already registered.");
        }
    }

    public bool IsRegistered(string trait) => _factories.ContainsKey(trait);

    /// <summary>Applies a Trait to an entity. False when no Trait has that id.</summary>
    public bool TryApply(string trait, EcsWorld world, Entity entity, IReadOnlyDictionary<string, double> values)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(values);
        if (!_factories.TryGetValue(trait, out var factory))
        {
            return false;
        }

        factory(world, entity, values);
        return true;
    }
}
