using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Zombies.Domain.Items;

namespace Zombies.Domain.Survival;

public sealed class ScenarioException(string message) : Exception(message);

/// <summary>What kind of place a Scenario starts the player in.</summary>
[JsonConverter(typeof(StartLocationKindJsonConverter))]
public enum StartLocationKind
{
    /// <summary>Inside a Settlement.</summary>
    [JsonStringEnumMemberName("settlement")]
    Settlement,

    /// <summary>Out in the open, away from any Settlement.</summary>
    [JsonStringEnumMemberName("wilderness")]
    Wilderness,
}

public sealed class StartLocationKindJsonConverter() : JsonStringEnumConverter<StartLocationKind>(namingPolicy: null, allowIntegerValues: false);

public sealed record StartingWoundDto
{
    /// <summary>The Body part the Wound is on, such as <c>leftArm</c>.</summary>
    public required string Part { get; init; }

    /// <summary>What caused it: <c>blunt</c>, <c>cut</c>, <c>pierce</c> or <c>bite</c>.</summary>
    public required string DamageType { get; init; }

    /// <summary>The damage of the hit that made it.</summary>
    public required double Damage { get; init; }
}

public sealed record StartingConditionDto
{
    /// <summary>How full the player starts, from 0 starving to 1 satiated. Defaults to 1.</summary>
    public double Satiety { get; init; } = 1;

    /// <summary>How hydrated the player starts, from 0 to 1. Defaults to 1.</summary>
    public double Hydration { get; init; } = 1;

    /// <summary>Wounds the player starts with. Defaults to none.</summary>
    public IReadOnlyList<StartingWoundDto> Wounds { get; init; } = [];
}

/// <summary>
/// JSON shape of a Scenario. This type is the source of the generated JSON Schema,
/// so keep it in step with <see cref="ScenarioJson"/>.
/// </summary>
public sealed record ScenarioDto
{
    /// <summary>Content ID in the form <c>namespace:scenario/name</c>.</summary>
    public required string Id { get; init; }

    public required StartLocationKind StartLocation { get; init; }

    /// <summary>The time of day the world starts at: 0 is midnight, 0.25 sunrise, 0.5 noon, 0.75 sunset.</summary>
    public required double TimeOfDay { get; init; }

    /// <summary>The state the player's character starts in. Defaults to well fed, hydrated and unhurt.</summary>
    public StartingConditionDto StartingCondition { get; init; } = new();
}

/// <summary>A Wound a character starts with, made by one hit that nothing wearable absorbs.</summary>
public sealed record StartingWound(BodyPart Part, DamageType DamageType, double Damage);

/// <summary>The Needs and Wounds a character starts with.</summary>
public sealed record StartingCondition(double Satiety, double Hydration, IReadOnlyList<StartingWound> Wounds);

/// <summary>Where, when and in what state a world's players begin. The host picks one when the world is created.</summary>
public sealed record Scenario(string Id, StartLocationKind StartLocation, double TimeOfDay, StartingCondition Condition);

/// <summary>Parses a Scenario from JSON.</summary>
public static partial class ScenarioJson
{
    public static Scenario Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        ScenarioDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<ScenarioDto>(json, DefinitionJson.Options);
        }
        catch (JsonException ex)
        {
            throw new ScenarioException($"Invalid Scenario: {ex.Message}");
        }

        if (dto is null)
        {
            throw new ScenarioException("A Scenario must be a JSON object.");
        }

        if (!IdPattern().IsMatch(dto.Id))
        {
            throw new ScenarioException($"'id' value '{dto.Id}' is not a Content ID of the form namespace:scenario/name.");
        }

        if (!double.IsFinite(dto.TimeOfDay) || dto.TimeOfDay is < 0 or >= 1)
        {
            throw new ScenarioException($"Scenario '{dto.Id}' has a 'timeOfDay' of {dto.TimeOfDay}; it must be from 0 up to but not including 1.");
        }

        var condition = dto.StartingCondition;
        if (!double.IsFinite(condition.Satiety) || condition.Satiety is < 0 or > 1 || !double.IsFinite(condition.Hydration) || condition.Hydration is < 0 or > 1)
        {
            throw new ScenarioException($"Scenario '{dto.Id}' needs a satiety and a hydration from 0 to 1.");
        }

        var wounds = condition.Wounds.Select(w => ParseWound(w, dto.Id)).ToList();
        return new Scenario(dto.Id, dto.StartLocation, dto.TimeOfDay, new StartingCondition(condition.Satiety, condition.Hydration, wounds));
    }

    private static StartingWound ParseWound(StartingWoundDto dto, string scenario)
    {
        var part = Named<BodyPart>(dto.Part, scenario, "part");
        var type = Named<DamageType>(dto.DamageType, scenario, "damageType");
        return double.IsFinite(dto.Damage) && dto.Damage > 0
            ? new StartingWound(part, type, dto.Damage)
            : throw new ScenarioException($"Scenario '{scenario}' has a Wound with damage {dto.Damage}; it must be more than 0.");
    }

    private static T Named<T>(string text, string scenario, string field)
        where T : struct, Enum =>
        DefinitionJson.TryParseName<T>(text, out var value)
            ? value
            : throw new ScenarioException($"Scenario '{scenario}' has an unknown '{field}' value '{text}'.");

    [GeneratedRegex("^[a-z0-9_]+:scenario/[a-z0-9_]+(/[a-z0-9_]+)*$")]
    private static partial Regex IdPattern();
}

/// <summary>Every Scenario the loaded mods declare.</summary>
public sealed class ScenarioCatalog
{
    private readonly Dictionary<string, Scenario> _scenarios = new(StringComparer.Ordinal);

    /// <exception cref="ArgumentException">A Scenario is defined twice.</exception>
    public ScenarioCatalog(IEnumerable<Scenario> scenarios)
    {
        ArgumentNullException.ThrowIfNull(scenarios);
        foreach (var scenario in scenarios)
        {
            if (!_scenarios.TryAdd(scenario.Id, scenario))
            {
                throw new ArgumentException($"Duplicate Scenario '{scenario.Id}'.", nameof(scenarios));
            }
        }
    }

    /// <summary>Every Scenario, ordered by Content ID.</summary>
    public IReadOnlyList<Scenario> All => [.. _scenarios.Values.OrderBy(s => s.Id, StringComparer.Ordinal)];

    public bool TryGet(string id, out Scenario scenario) => _scenarios.TryGetValue(id, out scenario!);
}
