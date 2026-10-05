using Zombies.Domain.Actions;
using Zombies.Domain.Combat;
using Zombies.Domain.Crafting;
using Zombies.Domain.Items;
using Zombies.Domain.Mods;
using Zombies.Domain.StatusEffects;
using Zombies.Domain.World;
using Zombies.Domain.Zombies;

namespace Zombies.Engine.Core.Modding;

/// <summary>
/// The definition kinds the game knows, mapped to the C# type that describes each.
/// This is the one place that sees every context, so contexts stay independent of each other.
/// </summary>
public static class BaseDefinitionKinds
{
    public static IReadOnlyDictionary<string, Type> All { get; } = new Dictionary<string, Type>
    {
        ["area_type"] = typeof(AreaTypeDto),
        ["biome"] = typeof(BiomeDto),
        ["item"] = typeof(ItemDefinitionDto),
        ["loot"] = typeof(LootTableDto),
        ["modifier"] = typeof(ModifierDefinitionDto),
        ["settlement_type"] = typeof(SettlementTypeDto),
        ["structure"] = typeof(StructureDto),
        ["attachment"] = typeof(AttachmentDto),
        ["item_action"] = typeof(ItemActionDto),
        ["status_effect"] = typeof(StatusEffectDto),
        ["weapon"] = typeof(WeaponDto),
        ["weapon_category"] = typeof(WeaponCategoryDto),
        ["weakpoint_set"] = typeof(WeakpointSetDto),
        ["zombie"] = typeof(ZombieTypeDto),
        [ContentIdMigrations.Kind] = typeof(MigrationDto),
    };
}
