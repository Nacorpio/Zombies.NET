using Zombies.Domain.Items;
using Zombies.Domain.Mods;
using Zombies.Domain.StatusEffects;
using Zombies.Domain.Zombies;

namespace Zombies.Engine.Core.Modding;

/// <summary>
/// Builds the Status effects of the loaded mods, so a new effect is only data. Everything that names an effect, such as an Item that applies one
/// or a zombie whose bite causes one, is checked against them here, so a typo fails when the mods load instead of silently doing nothing in play.
/// </summary>
public static class StatusEffectContentLoader
{
    /// <exception cref="StatusEffectDefinitionException">A Status effect definition is invalid.</exception>
    /// <exception cref="ArgumentException">Two effects share an id, or an item, a zombie, or an effect names an effect or item that does not exist.</exception>
    public static StatusEffectCatalog Load(DefinitionRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        var catalog = new StatusEffectCatalog(registry.OfKind("status_effect").Select(d => StatusEffectJson.Parse(d.Json)));
        var items = registry.OfKind("item").Select(d => ItemDefinitionJson.Parse(d.Json)).ToList();
        var itemIds = items.Select(i => i.Id).ToHashSet();

        foreach (var effect in catalog.All)
        {
            foreach (var cure in effect.CuredByItems.Where(i => !itemIds.Contains(i)))
            {
                throw new ArgumentException($"Status effect '{effect.Id}' is cured by item '{cure}', which does not exist.");
            }
        }

        foreach (var consumed in items.SelectMany(i => i.OnConsume.Select(c => (Item: i, c.Effect))).Where(x => !catalog.TryGet(x.Effect, out _)))
        {
            throw new ArgumentException($"Item '{consumed.Item.Id}' applies status effect '{consumed.Effect}' when consumed, which does not exist.");
        }

        foreach (var zombie in registry.OfKind("zombie").Select(d => ZombieTypeJson.Parse(d.Json)))
        {
            if (zombie.Bite is { } bite && !catalog.TryGet(bite.Effect, out _))
            {
                throw new ArgumentException($"Zombie type '{zombie.Id}' bites with status effect '{bite.Effect}', which does not exist.");
            }
        }

        return catalog;
    }
}
