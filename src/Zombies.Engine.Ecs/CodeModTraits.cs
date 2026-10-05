using Zombies.Engine.Core.Modding;
using Zombies.Modding.Api;

namespace Zombies.Engine.Ecs;

/// <summary>Gives the Traits that Code mods registered through the modding API to a <see cref="TraitRegistry"/>.</summary>
public static class CodeModTraits
{
    /// <exception cref="InvalidOperationException">A Trait with one of the ids is already registered.</exception>
    public static void Register(TraitRegistry registry, CodeModLoadResult codeMods)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(codeMods);
        foreach (var trait in codeMods.Traits)
        {
            var setup = trait.Setup;
            registry.Register(trait.Id, (world, entity, values) => setup(new Components(world, entity), values));
        }
    }

    private sealed class Components(EcsWorld world, Entity entity) : ITraitComponents
    {
        public void Add<TComponent>(TComponent component)
            where TComponent : struct => world.Set(entity, component);
    }
}
