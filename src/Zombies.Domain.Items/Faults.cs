using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zombies.Domain.Items;

public sealed class FaultDefinitionException(string message) : Exception(message);

/// <summary>What kind of Item a Fault can be on.</summary>
[JsonConverter(typeof(FaultTargetJsonConverter))]
public enum FaultTarget
{
    /// <summary>A weapon. Its effects change weapon Stats such as <c>damage</c>, <c>handling</c> or <c>jam_chance</c>.</summary>
    [JsonStringEnumMemberName("weapon")]
    Weapon,

    /// <summary>Something worn. Its effects change <c>protection</c>.</summary>
    [JsonStringEnumMemberName("armor")]
    Armor,
}

public sealed class FaultTargetJsonConverter() : JsonStringEnumConverter<FaultTarget>(namingPolicy: null, allowIntegerValues: false);

/// <summary>What can give an Item a Fault.</summary>
[JsonConverter(typeof(FaultCauseJsonConverter))]
public enum FaultCause
{
    /// <summary>The weapon was used.</summary>
    [JsonStringEnumMemberName("weapon_use")]
    WeaponUse,

    /// <summary>The worn item took a hit.</summary>
    [JsonStringEnumMemberName("armor_hit")]
    ArmorHit,
}

public sealed class FaultCauseJsonConverter() : JsonStringEnumConverter<FaultCause>(namingPolicy: null, allowIntegerValues: false);

/// <summary>A change a Fault makes to one Stat of the Item it is on.</summary>
public sealed record FaultEffect(StatName Stat, ModifierOperation Operation, double Value);

/// <summary>How an Item gets a Fault: each time the cause happens, the Fault is gained with this chance.</summary>
public sealed record FaultGain(FaultCause Cause, double Chance);

/// <summary>The repair that removes a Fault: the Item it uses up, and how long it takes. The caller takes the time.</summary>
public sealed record FaultRepair(ItemId Consumes, TimeSpan Time);

/// <summary>
/// Definition of a Fault, a specific kind of wear such as a chipped blade or a jammed action: the Stat changes it causes,
/// how an Item gains it, and the repair that removes it.
/// </summary>
public sealed record FaultDefinition
{
    /// <summary>The Stat that scales how much a worn item absorbs. Only armor Faults change it.</summary>
    public static readonly StatName Protection = new("protection");

    /// <summary>The chance, from 0 to 1, that using a weapon fails because it jams. Only weapon Faults change it.</summary>
    public static readonly StatName JamChance = new("jam_chance");

    public FaultDefinition(string id, FaultTarget target, IEnumerable<FaultEffect> effects, FaultRepair repair, FaultGain? gain = null, IEnumerable<string>? weaponCategories = null)
    {
        ArgumentNullException.ThrowIfNull(effects);
        ArgumentNullException.ThrowIfNull(repair);
        if (!ItemId.TryParse(id, out _))
        {
            throw new ArgumentException($"'{id}' is not a valid Content ID.", nameof(id));
        }

        if (!Enum.IsDefined(target))
        {
            throw new ArgumentOutOfRangeException(nameof(target), target, "Unknown Fault target.");
        }

        Effects = [.. effects];
        if (Effects.Count == 0)
        {
            throw new ArgumentException("A Fault must have at least one effect.", nameof(effects));
        }

        foreach (var effect in Effects)
        {
            if (!Enum.IsDefined(effect.Operation) || !double.IsFinite(effect.Value))
            {
                throw new ArgumentException($"The effect on '{effect.Stat}' is not a usable change.", nameof(effects));
            }

            if ((effect.Stat == Protection) != (target == FaultTarget.Armor))
            {
                throw new ArgumentException($"A {target} Fault cannot change '{effect.Stat}'.", nameof(effects));
            }
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(repair.Time, TimeSpan.Zero);
        if (gain is not null)
        {
            if (!Enum.IsDefined(gain.Cause) || (gain.Cause == FaultCause.ArmorHit) != (target == FaultTarget.Armor))
            {
                throw new ArgumentException($"A {target} Fault cannot be gained from '{gain.Cause}'.", nameof(gain));
            }

            if (!double.IsFinite(gain.Chance) || gain.Chance is <= 0 or > 1)
            {
                throw new ArgumentOutOfRangeException(nameof(gain), gain.Chance, "A chance must be above 0 and at most 1.");
            }
        }

        WeaponCategories = [.. (weaponCategories ?? []).Distinct()];
        foreach (var category in WeaponCategories)
        {
            if (!ItemId.TryParse(category, out _))
            {
                throw new ArgumentException($"'{category}' is not a valid Content ID.", nameof(weaponCategories));
            }
        }

        if (target == FaultTarget.Armor && WeaponCategories.Count > 0)
        {
            throw new ArgumentException("An armor Fault has no weapon categories.", nameof(weaponCategories));
        }

        Id = id;
        Target = target;
        Repair = repair;
        Gain = gain;
    }

    public string Id { get; }

    public FaultTarget Target { get; }

    public IReadOnlyList<FaultEffect> Effects { get; }

    public FaultRepair Repair { get; }

    /// <summary>Content IDs of the Weapon categories a weapon Fault is limited to. Empty for a Fault any weapon can have.</summary>
    public IReadOnlyList<string> WeaponCategories { get; }

    /// <summary>How an Item gets the Fault, or null when it is only ever put there directly, such as by a loot table.</summary>
    public FaultGain? Gain { get; }

    /// <summary>Whether a weapon of this Weapon category can have the Fault.</summary>
    public bool AppliesToCategory(string category) =>
        Target == FaultTarget.Weapon && (WeaponCategories.Count == 0 || WeaponCategories.Contains(category));

    /// <summary>The Modifiers this Fault grants when applied by <paramref name="source"/>.</summary>
    public IEnumerable<Modifier> ModifiersFrom(ModifierSource source) =>
        Effects.Select(e => new Modifier(e.Stat, e.Operation, e.Value, source));
}

/// <summary>JSON shape of one Fault effect.</summary>
public sealed record FaultEffectDto
{
    /// <summary>Name of the Stat to change: <c>protection</c> for an armor Fault, a weapon Stat such as <c>damage</c> or <c>jam_chance</c> for a weapon Fault.</summary>
    public required string Stat { get; init; }

    /// <summary><c>add</c> to add the value, or <c>multiply</c> to scale the Stat by it.</summary>
    public required ModifierOperation Operation { get; init; }

    public required double Value { get; init; }
}

/// <summary>JSON shape of how a Fault is gained.</summary>
public sealed record FaultGainDto
{
    /// <summary><c>weapon_use</c> for a weapon Fault, <c>armor_hit</c> for an armor Fault.</summary>
    public required FaultCause Cause { get; init; }

    /// <summary>Chance from above 0 to 1 that the cause gives the Fault.</summary>
    public required double Chance { get; init; }
}

/// <summary>JSON shape of a Fault's repair.</summary>
public sealed record FaultRepairDto
{
    /// <summary>Content ID of the Item the repair uses up, such as <c>base:item/whetstone</c>.</summary>
    public required string Consumes { get; init; }

    /// <summary>Seconds the repair takes.</summary>
    public required double Time { get; init; }
}

/// <summary>JSON shape of a Fault definition. This type is the source of the generated JSON Schema.</summary>
public sealed record FaultDto
{
    /// <summary>Content ID in the form <c>namespace:fault/name</c>, such as <c>base:fault/chipped_blade</c>.</summary>
    public required string Id { get; init; }

    public required FaultTarget Target { get; init; }

    public required IReadOnlyList<FaultEffectDto> Effects { get; init; }

    /// <summary>How an Item gains the Fault. Leave out for a Fault that is only ever put there directly.</summary>
    public FaultGainDto? Gained { get; init; }

    /// <summary>Content IDs of the Weapon categories a weapon Fault is limited to. Leave out for a Fault any weapon can have.</summary>
    public IReadOnlyList<string> WeaponCategories { get; init; } = [];

    public required FaultRepairDto Repair { get; init; }
}

/// <summary>Parses a Fault definition from JSON.</summary>
public static class FaultJson
{
    public static FaultDefinition Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        FaultDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<FaultDto>(json, DefinitionJson.Options);
        }
        catch (JsonException ex)
        {
            throw new FaultDefinitionException($"Invalid Fault definition: {ex.Message}");
        }

        if (dto is null)
        {
            throw new FaultDefinitionException("A Fault definition must be a JSON object.");
        }

        if (!ItemId.TryParse(dto.Repair.Consumes, out var consumes))
        {
            throw new FaultDefinitionException($"Fault '{dto.Id}' has an invalid repair 'consumes' value '{dto.Repair.Consumes}'.");
        }

        var effects = new List<FaultEffect>();
        foreach (var effect in dto.Effects)
        {
            if (!StatName.TryParse(effect.Stat, out var stat))
            {
                throw new FaultDefinitionException($"Fault '{dto.Id}' has an invalid effect 'stat' value '{effect.Stat}'.");
            }

            effects.Add(new FaultEffect(stat, effect.Operation, effect.Value));
        }

        try
        {
            return new FaultDefinition(
                dto.Id,
                dto.Target,
                effects,
                new FaultRepair(consumes, Seconds(dto.Repair.Time)),
                dto.Gained is { } gained ? new FaultGain(gained.Cause, gained.Chance) : null,
                dto.WeaponCategories);
        }
        catch (ArgumentException ex)
        {
            throw new FaultDefinitionException($"Fault '{dto.Id}' is invalid: {ex.Message}");
        }
    }

    private static TimeSpan Seconds(double value) =>
        double.IsFinite(value) && Math.Abs(value) < TimeSpan.MaxValue.TotalSeconds
            ? TimeSpan.FromSeconds(value)
            : throw new ArgumentOutOfRangeException(nameof(value), value, "Not a usable number of seconds.");
}

/// <summary>The Faults the game knows.</summary>
public sealed class FaultCatalog
{
    private readonly Dictionary<string, FaultDefinition> _definitions = [];

    public FaultCatalog(IEnumerable<FaultDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        foreach (var definition in definitions)
        {
            if (!_definitions.TryAdd(definition.Id, definition))
            {
                throw new ArgumentException($"Fault '{definition.Id}' is defined more than once.", nameof(definitions));
            }
        }
    }

    public IReadOnlyCollection<FaultDefinition> All => _definitions.Values;

    public bool TryGet(string id, out FaultDefinition definition) => _definitions.TryGetValue(id, out definition!);

    /// <summary>The Faults an Item state carries that this catalog knows, in Content ID order.</summary>
    public IReadOnlyList<FaultDefinition> Of(ItemState? state) =>
        [.. ItemFaults.Of(state).Select(id => _definitions.GetValueOrDefault(id)).OfType<FaultDefinition>()];

    /// <summary>
    /// The Fault, if any, that something giving the <paramref name="cause"/> leaves behind, for a <paramref name="roll"/> from 0
    /// up to but not including 1. Each Fault that has the cause takes a part of the roll as wide as its chance, in Content ID
    /// order, so at most one is gained at a time. Only Faults for which <paramref name="applicable"/> holds take part.
    /// </summary>
    public FaultDefinition? Gained(FaultCause cause, double roll, Func<FaultDefinition, bool>? applicable = null)
    {
        var bound = 0.0;
        foreach (var definition in _definitions.Values.Where(d => d.Gain?.Cause == cause && (applicable?.Invoke(d) ?? true)).OrderBy(d => d.Id, StringComparer.Ordinal))
        {
            bound += definition.Gain!.Chance;
            if (roll < bound)
            {
                return definition;
            }
        }

        return null;
    }
}

/// <summary>Reads and writes the Faults an Item carries in its Item state, as named values so equality, merging and saving carry them.</summary>
public static class ItemFaults
{
    private const string Prefix = "fault.";

    /// <summary>Content IDs of the Faults in this state, in order.</summary>
    public static IReadOnlyList<string> Of(ItemState? state) =>
        [.. (state?.Values.Keys ?? []).Where(k => k.StartsWith(Prefix, StringComparison.Ordinal)).Select(k => k[Prefix.Length..])];

    public static bool Has(ItemState? state, string fault) => state is not null && state.Values.ContainsKey(Prefix + fault);

    /// <summary>A copy of the state with the Fault added.</summary>
    public static ItemState With(ItemState? state, string fault) => (state ?? ItemState.Create()).With(Prefix + fault, 1);

    /// <summary>A copy of the state without the Fault, or null when nothing else is left.</summary>
    public static ItemState? Without(ItemState? state, string fault)
    {
        if (state is null)
        {
            return null;
        }

        var left = ItemState.Create(state.Values.Where(v => v.Key != Prefix + fault), state.Attached);
        return left.IsEmpty ? null : left;
    }
}
