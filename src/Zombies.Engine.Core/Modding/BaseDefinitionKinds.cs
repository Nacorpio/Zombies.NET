using Zombies.Domain.Combat;
using Zombies.Domain.Crafting;
using Zombies.Domain.Items;
using Zombies.Domain.World;

namespace Zombies.Engine.Core.Modding;

/// <summary>
/// The definition kinds the game knows, mapped to the C# type that describes each.
/// This is the one place that sees every context, so contexts stay independent of each other.
/// </summary>
public static class BaseDefinitionKinds
{
    public static IReadOnlyDictionary<string, Type> All { get; } = new Dictionary<string, Type>
    {
        ["biome"] = typeof(BiomeDto),
        ["item"] = typeof(ItemDefinitionDto),
        ["loot"] = typeof(LootTableDto),
        ["modifier"] = typeof(ModifierDefinitionDto),
        ["attachment"] = typeof(AttachmentDto),
        ["weapon"] = typeof(WeaponDto),
        ["weapon_category"] = typeof(WeaponCategoryDto),
    };
}
