using System.Text.Json;
using UnitsNet;
using Zombies.Domain.Items;

namespace Zombies.Domain.Combat;

public sealed class WoundKindDefinitionException(string message) : Exception(message);

/// <summary>What an untreated Wound of one kind can turn into once its healing time has passed without it healing.</summary>
public sealed record WoundWorsening
{
    public WoundWorsening(string kind, double chance)
    {
        WeaponValidation.ContentId(kind, nameof(kind));
        if (!double.IsFinite(chance) || chance is <= 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(chance), chance, "A chance must be above 0 and at most 1.");
        }

        Kind = kind;
        Chance = chance;
    }

    /// <summary>Content ID of the Wound kind it becomes.</summary>
    public string Kind { get; }

    public double Chance { get; }
}

/// <summary>
/// Definition of a Wound kind, such as a scratch or a deep cut: which damage causes it, how it bleeds, how long it takes to
/// heal, and whether it can worsen into another kind.
/// </summary>
public sealed record WoundKindDefinition
{
    public WoundKindDefinition(
        string id,
        IEnumerable<DamageType> damageTypes,
        double minDamage,
        double? maxDamage,
        VolumeFlow bleedRate,
        TimeSpan healingTime,
        WoundWorsening? worsening = null)
    {
        ArgumentNullException.ThrowIfNull(damageTypes);
        WeaponValidation.ContentId(id, nameof(id));
        WeaponValidation.NonNegative(minDamage, nameof(minDamage));
        if (maxDamage is { } max && (!double.IsFinite(max) || max <= minDamage))
        {
            throw new ArgumentOutOfRangeException(nameof(maxDamage), maxDamage, "The largest damage must be above the smallest.");
        }

        if (!double.IsFinite(bleedRate.MillilitersPerMinute) || bleedRate < VolumeFlow.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(bleedRate), bleedRate, "A bleed rate must be finite and not negative.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(healingTime, TimeSpan.Zero);
        DamageTypes = new HashSet<DamageType>(damageTypes.Select(t => Enum.IsDefined(t) ? t : throw new ArgumentOutOfRangeException(nameof(damageTypes), t, "Unknown damage type.")));
        Id = id;
        MinDamage = minDamage;
        MaxDamage = maxDamage;
        BleedRate = bleedRate;
        HealingTime = healingTime;
        Worsening = worsening;
    }

    public string Id { get; }

    /// <summary>The damage types that cause this kind. A kind with none is only ever reached by another kind worsening.</summary>
    public IReadOnlySet<DamageType> DamageTypes { get; }

    /// <summary>Smallest damage, after protection, that causes this kind.</summary>
    public double MinDamage { get; }

    /// <summary>Damage at which this kind stops being caused, or null when there is no upper limit.</summary>
    public double? MaxDamage { get; }

    public VolumeFlow BleedRate { get; }

    /// <summary>How long the Wound takes to heal on its own.</summary>
    public TimeSpan HealingTime { get; }

    public WoundWorsening? Worsening { get; }

    /// <summary>Whether damage of this type and size causes this kind.</summary>
    public bool Causes(DamageType type, double damage) =>
        DamageTypes.Contains(type) && damage >= MinDamage && (MaxDamage is not { } max || damage < max);
}

/// <summary>JSON shape of the damage a Wound kind is caused by.</summary>
public sealed record DamageRangeDto
{
    /// <summary>Smallest damage that causes the kind. Defaults to 0.</summary>
    public double Min { get; init; }

    /// <summary>Damage at which the kind stops being caused. Leave out for no upper limit.</summary>
    public double? Max { get; init; }
}

/// <summary>JSON shape of how a Wound kind worsens.</summary>
public sealed record WoundWorseningDto
{
    /// <summary>Content ID of the Wound kind it becomes, such as <c>base:wound_kind/infected</c>.</summary>
    public required string Kind { get; init; }

    /// <summary>Chance from above 0 to 1 that an untreated Wound worsens instead of healing.</summary>
    public required double Chance { get; init; }
}

/// <summary>JSON shape of a Wound kind definition. This type is the source of the generated JSON Schema.</summary>
public sealed record WoundKindDto
{
    /// <summary>Content ID in the form <c>namespace:wound_kind/name</c>, such as <c>base:wound_kind/scratch</c>.</summary>
    public required string Id { get; init; }

    /// <summary>Damage types that cause the kind, each one of <c>blunt</c>, <c>cut</c>, <c>pierce</c>, <c>bite</c>. Leave out for a kind that is only reached by worsening.</summary>
    public IReadOnlyList<string> DamageTypes { get; init; } = [];

    public DamageRangeDto DamageRange { get; init; } = new();

    /// <summary>Millilitres of blood lost per minute. Defaults to 0.</summary>
    public double BleedRate { get; init; }

    /// <summary>Seconds the Wound takes to heal on its own.</summary>
    public required double HealingTime { get; init; }

    /// <summary>What an untreated Wound can turn into instead of healing. Leave out for a kind that always heals.</summary>
    public WoundWorseningDto? Worsens { get; init; }
}

/// <summary>Parses a Wound kind definition from JSON.</summary>
public static class WoundKindJson
{
    public static WoundKindDefinition Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        WoundKindDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<WoundKindDto>(json, DefinitionJson.Options);
        }
        catch (JsonException ex)
        {
            throw new WoundKindDefinitionException($"Invalid Wound kind definition: {ex.Message}");
        }

        if (dto is null)
        {
            throw new WoundKindDefinitionException("A Wound kind definition must be a JSON object.");
        }

        try
        {
            return new WoundKindDefinition(
                dto.Id,
                dto.DamageTypes.Select(t => Enum.TryParse<DamageType>(t, ignoreCase: true, out var type)
                    ? type
                    : throw new ArgumentException($"'{t}' is not a damage type.")),
                dto.DamageRange.Min,
                dto.DamageRange.Max,
                VolumeFlow.FromMillilitersPerMinute(dto.BleedRate),
                Seconds(dto.HealingTime),
                dto.Worsens is { } worsens ? new WoundWorsening(worsens.Kind, worsens.Chance) : null);
        }
        catch (ArgumentException ex)
        {
            throw new WoundKindDefinitionException($"Wound kind '{dto.Id}' is invalid: {ex.Message}");
        }
    }

    private static TimeSpan Seconds(double value) =>
        double.IsFinite(value) && Math.Abs(value) < TimeSpan.MaxValue.TotalSeconds
            ? TimeSpan.FromSeconds(value)
            : throw new ArgumentOutOfRangeException(nameof(value), value, "Not a usable number of seconds.");
}

/// <summary>The Wound kinds the game knows.</summary>
public sealed class WoundKindCatalog
{
    private readonly Dictionary<string, WoundKindDefinition> _definitions = [];

    public WoundKindCatalog(IEnumerable<WoundKindDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        foreach (var definition in definitions)
        {
            if (!_definitions.TryAdd(definition.Id, definition))
            {
                throw new ArgumentException($"Wound kind '{definition.Id}' is defined more than once.", nameof(definitions));
            }
        }

        foreach (var definition in _definitions.Values.Where(d => d.Worsening is { } w && !_definitions.ContainsKey(w.Kind)))
        {
            throw new ArgumentException($"Wound kind '{definition.Id}' worsens into unknown kind '{definition.Worsening!.Kind}'.", nameof(definitions));
        }
    }

    public IReadOnlyCollection<WoundKindDefinition> All => _definitions.Values;

    public bool TryGet(string id, out WoundKindDefinition definition) => _definitions.TryGetValue(id, out definition!);

    /// <summary>
    /// The kind that damage of this type and size causes, or null when none does. When several match, the one whose range
    /// starts highest wins, so a narrower range beats a wider one; a tie goes to the lowest Content ID.
    /// </summary>
    public WoundKindDefinition? Causing(DamageType type, double damage) =>
        _definitions.Values
            .Where(d => d.Causes(type, damage))
            .OrderByDescending(d => d.MinDamage)
            .ThenBy(d => d.Id, StringComparer.Ordinal)
            .FirstOrDefault();
}
