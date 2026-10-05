using Zombies.Domain.Actions;
using Zombies.Domain.Combat;
using Zombies.Domain.Crafting;
using Zombies.Domain.Items;
using Zombies.Domain.Mods;
using Zombies.Domain.StatusEffects;
using Zombies.Domain.Survival;
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
        ["fault"] = typeof(FaultDto),
        ["item"] = typeof(ItemDefinitionDto),
        ["limb_score"] = typeof(LimbScoreDto),
        ["movement_mode"] = typeof(MovementModeDto),
        ["loot"] = typeof(LootTableDto),
        ["modifier"] = typeof(ModifierDefinitionDto),
        ["morale_band"] = typeof(MoraleBandDto),
        ["morale_source"] = typeof(MoraleSourceDto),
        ["settlement_type"] = typeof(SettlementTypeDto),
        ["structure"] = typeof(StructureDto),
        ["attachment"] = typeof(AttachmentDto),
        ["item_action"] = typeof(ItemActionDto),
        ["status_effect"] = typeof(StatusEffectDto),
        ["substance"] = typeof(SubstanceDto),
        ["weapon"] = typeof(WeaponDto),
        ["weapon_category"] = typeof(WeaponCategoryDto),
        ["treatment"] = typeof(TreatmentDto),
        ["weakpoint_set"] = typeof(WeakpointSetDto),
        ["world_option"] = typeof(WorldOptionDto),
        ["wound_kind"] = typeof(WoundKindDto),
        ["zombie"] = typeof(ZombieTypeDto),
        [ContentIdMigrations.Kind] = typeof(MigrationDto),
    };
}
