using Zombies.Domain.Mods;
using Zombies.Domain.Zombies;

namespace Zombies.Engine.Core.Modding;

/// <summary>Builds the Zombie types of the loaded mods, so a new kind of zombie is only data.</summary>
public static class ZombieContentLoader
{
    /// <exception cref="ZombieTypeDefinitionException">A Zombie type or Weakpoint set definition is invalid.</exception>
    /// <exception cref="ArgumentException">A Zombie type names a Weakpoint set that does not exist.</exception>
    public static ZombieCatalog Load(DefinitionRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return new ZombieCatalog(
            registry.OfKind("zombie").Select(d => ZombieTypeJson.Parse(d.Json)),
            registry.OfKind("weakpoint_set").Select(d => WeakpointSetJson.Parse(d.Json)));
    }
}
