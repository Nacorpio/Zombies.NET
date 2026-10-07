using Zombies.Domain.Combat;
using Zombies.Domain.Mods;
using Zombies.Domain.Zombies;

namespace Zombies.Engine.Core.Modding;

/// <summary>Builds the Zombie types of the loaded mods, so a new kind of zombie is only data.</summary>
public static class ZombieContentLoader
{
    /// <exception cref="ZombieTypeDefinitionException">A Zombie type or Weakpoint set definition is invalid.</exception>
    /// <exception cref="ArgumentException">A Zombie type names a Weakpoint set that does not exist, or holds something that is not a melee Weapon.</exception>
    public static ZombieCatalog Load(DefinitionRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return new ZombieCatalog(
            registry.OfKind("zombie").Select(d => ZombieTypeJson.Parse(d.Json)),
            registry.OfKind("weakpoint_set").Select(d => WeakpointSetJson.Parse(d.Json)),
            new WeaponCatalog(
                registry.OfKind("weapon_category").Select(d => WeaponDefinitionJson.ParseCategory(d.Json)),
                registry.OfKind("weapon").Select(d => WeaponDefinitionJson.ParseWeapon(d.Json)),
                registry.OfKind("attachment").Select(d => WeaponDefinitionJson.ParseAttachment(d.Json))));
    }
}
