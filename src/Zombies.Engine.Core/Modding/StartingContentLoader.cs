using Zombies.Domain.Crafting;
using Zombies.Domain.Items;
using Zombies.Domain.Mods;
using Zombies.Domain.Survival;

namespace Zombies.Engine.Core.Modding;

/// <summary>Builds what a player can start the game with from the loaded mods, so a new Profession or Scenario is only data.</summary>
public static class StartingContentLoader
{
    /// <exception cref="WearableDefinitionException">A Wearable definition is invalid.</exception>
    public static WearableCatalog LoadWearables(DefinitionRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return new WearableCatalog(registry.OfKind("wearable").Select(d => WearableJson.Parse(d.Json)));
    }

    /// <exception cref="LootTableException">A loot table definition is invalid.</exception>
    public static LootTableCatalog LoadLoot(DefinitionRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return new LootTableCatalog(registry.OfKind("loot").Select(d => LootTableJson.Parse(d.Json)));
    }

    /// <exception cref="ProfessionException">A Profession definition is invalid.</exception>
    public static ProfessionCatalog LoadProfessions(DefinitionRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return new ProfessionCatalog(registry.OfKind("profession").Select(d => ProfessionJson.Parse(d.Json)));
    }

    /// <exception cref="ScenarioException">A Scenario definition is invalid.</exception>
    public static ScenarioCatalog LoadScenarios(DefinitionRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return new ScenarioCatalog(registry.OfKind("scenario").Select(d => ScenarioJson.Parse(d.Json)));
    }
}
