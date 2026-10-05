using Zombies.Domain.Mods;
using Zombies.Domain.Zombies;

namespace Zombies.Engine.Core.Modding;

/// <summary>Builds the Zombie types of the loaded mods, so a new kind of zombie is only data.</summary>
public static class ZombieContentLoader
{
    /// <exception cref="ZombieTypeDefinitionException">A Zombie type definition is invalid.</exception>
    public static ZombieCatalog Load(DefinitionRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return new ZombieCatalog(registry.OfKind("zombie").Select(d => ZombieTypeJson.Parse(d.Json)));
    }
}
