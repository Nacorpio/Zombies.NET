using System.Text.Json;
using Zombies.Domain.Items;

namespace Zombies.Domain.Zombies;

public sealed class ZombieTypeDefinitionException(string message) : Exception(message);

public sealed record TraitDto
{
    /// <summary>Content ID of the Trait, such as <c>base:trait/runner</c>. Code registers what a Trait does; this only names it.</summary>
    public required string Trait { get; init; }

    /// <summary>Numbers the Trait reads, by name, such as <c>speedMultiplier</c>.</summary>
    public IReadOnlyDictionary<string, double> Values { get; init; } = new Dictionary<string, double>();
}

public sealed record WeightedWearableDto
{
    /// <summary>Content ID of the Item worn. Leave out for a chance of wearing nothing.</summary>
    public string? Item { get; init; }

    /// <summary>Chance of this entry relative to the others. Defaults to 1.</summary>
    public int Weight { get; init; } = 1;
}

public sealed record WeightedHeldWeaponDto
{
    /// <summary>Content ID of the melee weapon Item held, such as <c>base:item/crowbar</c>. Leave out for a chance of holding nothing.</summary>
    public string? Item { get; init; }

    /// <summary>Chance of this entry relative to the others. Defaults to 1.</summary>
    public int Weight { get; init; } = 1;
}

public sealed record CountRangeDto
{
    public required int Min { get; init; }

    public required int Max { get; init; }
}

public sealed record OutfitTableDto
{
    /// <summary>Clothing to choose from. Each piece is worn at most once.</summary>
    public IReadOnlyList<WeightedWearableDto> Clothing { get; init; } = [];

    /// <summary>How many pieces of clothing are chosen. Defaults to none.</summary>
    public CountRangeDto ClothingCount { get; init; } = new() { Min = 0, Max = 0 };

    public IReadOnlyList<WeightedWearableDto> Headwear { get; init; } = [];

    public IReadOnlyList<WeightedWearableDto> Backpacks { get; init; } = [];
}

public sealed record ScaleRangeDto
{
    /// <summary>Smallest scale, where 1 is the skeleton's own size.</summary>
    public required double Min { get; init; }

    public required double Max { get; init; }
}

public sealed record SensesDto
{
    /// <summary>How far the zombie sees, in meters.</summary>
    public required double Sight { get; init; }

    /// <summary>How far the zombie hears, in meters.</summary>
    public required double Hearing { get; init; }
}

public sealed record StatsDto
{
    /// <summary>Health of each Body part at Level 1.</summary>
    public required double PartHealth { get; init; }

    /// <summary>Damage of one attack at Level 1.</summary>
    public required double Damage { get; init; }

    /// <summary>Walking speed in meters per second.</summary>
    public required double Speed { get; init; }

    /// <summary>The highest Level. Defaults to 10.</summary>
    public int MaxLevel { get; init; } = 10;

    /// <summary>Fraction of health and damage added per Level above 1. Defaults to 0.1.</summary>
    public double PerLevelBonus { get; init; } = 0.1;
}

public sealed record AppearanceDto
{
    /// <summary>Range of body height as a scale of the skeleton. Defaults to 0.95 to 1.05.</summary>
    public ScaleRangeDto Height { get; init; } = new() { Min = 0.95, Max = 1.05 };

    /// <summary>Range of body width and depth as a scale of the skeleton. Defaults to 0.9 to 1.1.</summary>
    public ScaleRangeDto Build { get; init; } = new() { Min = 0.9, Max = 1.1 };

    /// <summary>Skin colors as <c>#rrggbb</c>.</summary>
    public required IReadOnlyList<string> SkinTones { get; init; }
}

public sealed record MissingPartChanceDto
{
    /// <summary>Name of the Body part, such as <c>leftArm</c>. The torso cannot be missing.</summary>
    public required string Part { get; init; }

    /// <summary>Chance from 0 to 1 that the part is missing from spawn.</summary>
    public required double Chance { get; init; }
}

public sealed record UpgradeDto
{
    /// <summary>Content ID of the Zombie type this one upgrades into, such as <c>base:zombie/runner</c>.</summary>
    public required string ZombieType { get; init; }

    /// <summary>World days after which the upgrade happens, at least 1. A chain of upgrades adds its days up.</summary>
    public required int AfterDays { get; init; }
}

/// <summary>
/// JSON shape of a Zombie type definition. This type is the source of the generated JSON Schema,
/// so keep it in step with <see cref="ZombieTypeJson"/>.
/// </summary>
public sealed record ZombieTypeDto
{
    /// <summary>Content ID in the form <c>namespace:zombie/name</c>, such as <c>base:zombie/walker</c>.</summary>
    public required string Id { get; init; }

    public required StatsDto Stats { get; init; }

    public required SensesDto Senses { get; init; }

    public IReadOnlyList<TraitDto> Traits { get; init; } = [];

    public required AppearanceDto Appearance { get; init; }

    public OutfitTableDto Outfit { get; init; } = new();

    public IReadOnlyList<MissingPartChanceDto> MissingParts { get; init; } = [];

    /// <summary>Content ID of the Weakpoint set, such as <c>base:weakpoint_set/humanoid</c>. Leave out for a zombie with no weak spots.</summary>
    public string? WeakpointSet { get; init; }

    /// <summary>Melee weapons a zombie of this type may spawn holding, by weight. Leave out for a type that never holds one.</summary>
    public IReadOnlyList<WeightedHeldWeaponDto> HeldWeapons { get; init; } = [];

    /// <summary>What the zombies of this type become as the world ages. Leave out for a type that never changes.</summary>
    public UpgradeDto? Upgrade { get; init; }
}

/// <summary>Parses a Zombie type definition from JSON.</summary>
public static class ZombieTypeJson
{
    public static ZombieTypeDefinition Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        ZombieTypeDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<ZombieTypeDto>(json, DefinitionJson.Options);
        }
        catch (JsonException ex)
        {
            throw new ZombieTypeDefinitionException($"Invalid Zombie type definition: {ex.Message}");
        }

        if (dto is null)
        {
            throw new ZombieTypeDefinitionException("A Zombie type definition must be a JSON object.");
        }

        try
        {
            return new ZombieTypeDefinition(
                dto.Id,
                dto.Stats.PartHealth,
                dto.Stats.Damage,
                dto.Stats.Speed,
                dto.Senses.Sight,
                dto.Senses.Hearing,
                dto.Stats.MaxLevel,
                dto.Stats.PerLevelBonus,
                dto.Traits.Select(t => new TraitReference(t.Trait, new Dictionary<string, double>(t.Values))),
                Permille(dto.Appearance.Height),
                Permille(dto.Appearance.Build),
                dto.Appearance.SkinTones.Select(ParseColor),
                new OutfitTable(
                    dto.Outfit.Clothing.Select(ToWearable).ToList(),
                    dto.Outfit.ClothingCount.Min,
                    dto.Outfit.ClothingCount.Max,
                    dto.Outfit.Headwear.Select(ToWearable).ToList(),
                    dto.Outfit.Backpacks.Select(ToWearable).ToList()),
                dto.MissingParts.Select(m => new MissingPartChance(ParsePart(m.Part), Basis(m.Chance))),
                dto.WeakpointSet,
                dto.Upgrade is null ? null : new ZombieUpgrade(dto.Upgrade.ZombieType, dto.Upgrade.AfterDays),
                dto.HeldWeapons.Select(ToHeldWeapon).ToList());
        }
        catch (ArgumentException ex)
        {
            throw new ZombieTypeDefinitionException($"Zombie type '{dto.Id}' is invalid: {ex.Message}");
        }
    }

    private static BodyPart ParsePart(string name) =>
        Enum.TryParse<BodyPart>(name, ignoreCase: true, out var part) && Enum.IsDefined(part)
            ? part
            : throw new ArgumentException($"'{name}' is not a Body part.");

    private static (int Min, int Max) Permille(ScaleRangeDto range) =>
        double.IsFinite(range.Min) && double.IsFinite(range.Max)
            ? ((int)Math.Round(range.Min * 1000), (int)Math.Round(range.Max * 1000))
            : throw new ArgumentException("A proportion range must be finite numbers.");

    private static int Basis(double chance) =>
        double.IsFinite(chance) && chance is >= 0 and <= 1
            ? (int)Math.Round(chance * ZombieTypeDefinition.BasisPoints)
            : throw new ArgumentException("A chance must be between 0 and 1.");

    private static WeightedWearable ToWearable(WeightedWearableDto dto) =>
        new(dto.Item is null ? null : ItemId.TryParse(dto.Item, out var id) ? id : throw new ArgumentException($"'{dto.Item}' is not a valid Content ID."), dto.Weight);

    private static WeightedHeldWeapon ToHeldWeapon(WeightedHeldWeaponDto dto) =>
        new(dto.Item is null ? null : ItemId.TryParse(dto.Item, out var id) ? id : throw new ArgumentException($"'{dto.Item}' is not a valid Content ID."), dto.Weight);

    private static uint ParseColor(string text)
    {
        if (text.Length == 7 && text[0] == '#' && uint.TryParse(text.AsSpan(1), System.Globalization.NumberStyles.AllowHexSpecifier, System.Globalization.CultureInfo.InvariantCulture, out var color))
        {
            return color;
        }

        throw new ArgumentException($"'{text}' is not a color like #8fa07a.");
    }
}
