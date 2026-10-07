namespace Zombies.Modding.Api;

/// <summary>Adds the components of a Trait to one zombie as it spawns.</summary>
/// <param name="zombie">The zombie the Trait is being given.</param>
/// <param name="values">The numbers the Zombie type set for this Trait, by name. A number the type left out is absent.</param>
public delegate void TraitSetup(ITraitComponents zombie, IReadOnlyDictionary<string, double> values);

/// <summary>The components of one zombie, while a Trait is being given to it. A component is a plain struct the mod defines.</summary>
public interface ITraitComponents
{
    /// <summary>Adds a component, or replaces the one of the same type the zombie already has.</summary>
    void Add<TComponent>(TComponent component)
        where TComponent : struct;
}
