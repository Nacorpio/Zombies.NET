using System.Text.Json;
using System.Text.Json.Serialization;
using Zombies.Domain.Items;

namespace Zombies.Domain.Combat;

public sealed class WeaponDefinitionException(string message) : Exception(message);

/// <summary>JSON shape of a Weapon category. This type is the source of the generated JSON Schema.</summary>
public sealed record WeaponCategoryDto
{
    /// <summary>Content ID in the form <c>namespace:weapon_category/name</c>, such as <c>base:weapon_category/pistol</c>.</summary>
    public required string Id { get; init; }

    /// <summary>Default handling of weapons in this category.</summary>
    public required double Handling { get; init; }

    /// <summary>Names of the Mounts every weapon in this category offers, such as <c>muzzle</c> or <c>optic</c>.</summary>
    public IReadOnlyList<string> Mounts { get; init; } = [];
}

/// <summary>JSON shape of a Weapon definition. This type is the source of the generated JSON Schema.</summary>
public sealed record WeaponDto
{
    /// <summary>Content ID in the form <c>namespace:weapon/name</c>.</summary>
    public required string Id { get; init; }

    /// <summary>Content ID of the Item this weapon is, such as <c>base:item/pistol</c>.</summary>
    public required string Item { get; init; }

    /// <summary>Content ID of the Weapon category.</summary>
    public required string Category { get; init; }

    public required double Damage { get; init; }

    /// <summary>One of <c>blunt</c>, <c>cut</c>, <c>pierce</c>, <c>bite</c>.</summary>
    public required string DamageType { get; init; }

    /// <summary>Uses per second.</summary>
    public required double RateOfFire { get; init; }

    /// <summary>Reach for melee or range for ranged weapons, in meters.</summary>
    public required double Reach { get; init; }

    public required double Noise { get; init; }

    /// <summary>Hands needed to hold it, 1 or 2. Defaults to 1.</summary>
    public int HandsNeeded { get; init; } = 1;

    /// <summary>Handling that overrides the category's.</summary>
    public double? Handling { get; init; }

    /// <summary>Content ID of the Item whose rounds this weapon spends. Leave out for a weapon that needs no ammo.</summary>
    public string? AmmoItem { get; init; }

    /// <summary>Condition lost per use. Defaults to 1.</summary>
    public int WearPerUse { get; init; } = 1;

    /// <summary>Names of Mounts this weapon offers beyond its category's.</summary>
    public IReadOnlyList<string> Mounts { get; init; } = [];
}

/// <summary>One change an Attachment makes to a Stat.</summary>
public sealed record AttachmentEffectDto
{
    /// <summary>Name of the Stat to change, such as <c>noise</c>.</summary>
    public required string Stat { get; init; }

    /// <summary><c>add</c> to add the value, or <c>multiply</c> to scale the Stat by it.</summary>
    public required ModifierOperation Operation { get; init; }

    public required double Value { get; init; }
}

/// <summary>JSON shape of an Attachment definition. This type is the source of the generated JSON Schema.</summary>
public sealed record AttachmentDto
{
    /// <summary>Content ID in the form <c>namespace:attachment/name</c>.</summary>
    public required string Id { get; init; }

    /// <summary>Content ID of the Item that is fitted.</summary>
    public required string Item { get; init; }

    /// <summary>Name of the Mount it fits, such as <c>muzzle</c>.</summary>
    public required string Mount { get; init; }

    public IReadOnlyList<AttachmentEffectDto> Effects { get; init; } = [];
}

/// <summary>Parses Weapon category, Weapon, and Attachment definitions from JSON.</summary>
public static class WeaponDefinitionJson
{
    public static WeaponCategory ParseCategory(string json)
    {
        var dto = Read<WeaponCategoryDto>(json, "Weapon category");
        return Build(dto.Id, () => new WeaponCategory(Checked(dto.Id), dto.Handling, dto.Mounts));
    }

    public static WeaponDefinition ParseWeapon(string json)
    {
        var dto = Read<WeaponDto>(json, "Weapon");
        Checked(dto.Id);
        if (!Enum.TryParse<DamageType>(dto.DamageType, ignoreCase: true, out var damageType) || !Enum.IsDefined(damageType))
        {
            throw new WeaponDefinitionException($"Weapon '{dto.Id}' has an invalid 'damageType' value '{dto.DamageType}'.");
        }

        var ammo = dto.AmmoItem is null ? (ItemId?)null : ItemOf(dto.Id, "ammoItem", dto.AmmoItem);
        return Build(dto.Id, () => new WeaponDefinition(
            ItemOf(dto.Id, "item", dto.Item),
            Checked(dto.Category),
            dto.Damage,
            damageType,
            dto.RateOfFire,
            dto.Reach,
            dto.Noise,
            dto.HandsNeeded,
            dto.Handling,
            ammo,
            dto.WearPerUse,
            dto.Mounts));
    }

    public static AttachmentDefinition ParseAttachment(string json)
    {
        var dto = Read<AttachmentDto>(json, "Attachment");
        Checked(dto.Id);
        var effects = dto.Effects.Select(e => StatName.TryParse(e.Stat, out var stat)
            ? new AttachmentEffect(stat, e.Operation, e.Value)
            : throw new WeaponDefinitionException($"Attachment '{dto.Id}' has an invalid 'stat' value '{e.Stat}'.")).ToList();
        return Build(dto.Id, () => new AttachmentDefinition(ItemOf(dto.Id, "item", dto.Item), dto.Mount, effects));
    }

    private static T Read<T>(string json, string what)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            return JsonSerializer.Deserialize<T>(json, DefinitionJson.Options)
                ?? throw new WeaponDefinitionException($"A {what} definition must be a JSON object.");
        }
        catch (JsonException ex)
        {
            throw new WeaponDefinitionException($"Invalid {what} definition: {ex.Message}");
        }
    }

    private static string Checked(string contentId) =>
        ItemId.TryParse(contentId, out _) ? contentId : throw new WeaponDefinitionException($"'{contentId}' is not a valid Content ID.");

    private static ItemId ItemOf(string owner, string property, string value) =>
        ItemId.TryParse(value, out var id) ? id : throw new WeaponDefinitionException($"'{owner}' has an invalid '{property}' value '{value}'.");

    private static T Build<T>(string id, Func<T> create)
    {
        try
        {
            return create();
        }
        catch (ArgumentException ex)
        {
            throw new WeaponDefinitionException($"'{id}' has an invalid value: {ex.Message}");
        }
    }
}
