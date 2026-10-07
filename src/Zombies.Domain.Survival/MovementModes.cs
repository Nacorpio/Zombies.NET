using System.Text.Json;
using System.Text.Json.Serialization;
using Zombies.Domain.Items;

namespace Zombies.Domain.Survival;

public sealed class MovementModeDefinitionException(string message) : Exception(message);

/// <summary>The input that asks for a Movement mode.</summary>
[JsonConverter(typeof(MovementTriggerJsonConverter))]
public enum MovementTrigger
{
    [JsonStringEnumMemberName("walk")]
    Walk,

    [JsonStringEnumMemberName("sprint")]
    Sprint,

    [JsonStringEnumMemberName("crouch")]
    Crouch,
}

public sealed class MovementTriggerJsonConverter() : JsonStringEnumConverter<MovementTrigger>(namingPolicy: null, allowIntegerValues: false);

/// <summary>
/// Definition of a Movement mode: how fast the player moves, how much noise that makes and how much Stamina it costs,
/// each as a multiplier on the base the movement model uses. A mode is chosen by the input that asks for it; when several
/// modes answer the same input, the one with the highest priority is used.
/// </summary>
public sealed record MovementModeDefinition
{
    public MovementModeDefinition(string id, MovementTrigger trigger, double speed, double noise, double stamina, int priority = 0)
    {
        if (!ItemId.TryParse(id, out _))
        {
            throw new ArgumentException($"'{id}' is not a valid Content ID.", nameof(id));
        }

        if (!Enum.IsDefined(trigger))
        {
            throw new ArgumentOutOfRangeException(nameof(trigger), trigger, "Unknown trigger.");
        }

        Id = id;
        Trigger = trigger;
        Speed = NotNegative(speed, nameof(speed));
        Noise = NotNegative(noise, nameof(noise));
        Stamina = NotNegative(stamina, nameof(stamina));
        Priority = priority;
    }

    public string Id { get; }

    public MovementTrigger Trigger { get; }

    /// <summary>Multiplies the walking speed.</summary>
    public double Speed { get; }

    /// <summary>Multiplies the noise of walking. 0 is silent.</summary>
    public double Noise { get; }

    /// <summary>Multiplies the Stamina drain while moving. 0 costs nothing, and the player recovers while moving.</summary>
    public double Stamina { get; }

    public int Priority { get; }

    private static double NotNegative(double value, string name) =>
        double.IsFinite(value) && value >= 0 ? value : throw new ArgumentOutOfRangeException(name, value, "A multiplier must be a finite number, zero or more.");
}

/// <summary>JSON shape of a Movement mode definition. This type is the source of the generated JSON Schema.</summary>
public sealed record MovementModeDto
{
    /// <summary>Content ID in the form <c>namespace:movement_mode/name</c>, such as <c>base:movement_mode/sprint</c>.</summary>
    public required string Id { get; init; }

    /// <summary>The input that asks for this mode: walk when no other is held, sprint, or crouch.</summary>
    public required MovementTrigger Trigger { get; init; }

    /// <summary>Multiplies the walking speed. 1 is walking pace.</summary>
    public required double Speed { get; init; }

    /// <summary>Multiplies the noise of walking. 0 is silent.</summary>
    public required double Noise { get; init; }

    /// <summary>Multiplies the Stamina drain while moving. 0 costs nothing.</summary>
    public required double Stamina { get; init; }

    /// <summary>Which mode wins when several answer the same trigger. Defaults to 0.</summary>
    public int Priority { get; init; }
}

/// <summary>Parses a Movement mode definition from JSON.</summary>
public static class MovementModeJson
{
    public static MovementModeDefinition Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        MovementModeDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<MovementModeDto>(json, DefinitionJson.Options);
        }
        catch (JsonException ex)
        {
            throw new MovementModeDefinitionException($"Invalid Movement mode definition: {ex.Message}");
        }

        if (dto is null)
        {
            throw new MovementModeDefinitionException("A Movement mode definition must be a JSON object.");
        }

        try
        {
            return new MovementModeDefinition(dto.Id, dto.Trigger, dto.Speed, dto.Noise, dto.Stamina, dto.Priority);
        }
        catch (ArgumentException ex)
        {
            throw new MovementModeDefinitionException($"Movement mode '{dto.Id}' is invalid: {ex.Message}");
        }
    }
}

/// <summary>
/// The Movement modes in play, resolved to one per <see cref="MovementTrigger"/> so the movement model can look one up
/// without allocating. The Server and every client hold the same set, because the mods they loaded must match.
/// </summary>
public sealed class MovementModes
{
    private readonly MovementModeDefinition[] _byTrigger;

    /// <exception cref="MovementModeDefinitionException">A Movement mode is defined twice, or no mode answers a trigger.</exception>
    public MovementModes(IEnumerable<MovementModeDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        var all = definitions.ToList();
        var duplicate = all.GroupBy(d => d.Id).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new MovementModeDefinitionException($"Movement mode '{duplicate.Key}' is defined more than once.");
        }

        All = [.. all.OrderBy(d => d.Id, StringComparer.Ordinal)];
        _byTrigger = new MovementModeDefinition[Enum.GetValues<MovementTrigger>().Length];
        foreach (var trigger in Enum.GetValues<MovementTrigger>())
        {
            _byTrigger[(int)trigger] = All
                .Where(d => d.Trigger == trigger)
                .OrderByDescending(d => d.Priority)
                .FirstOrDefault()
                ?? throw new MovementModeDefinitionException($"No Movement mode answers the '{trigger}' trigger.");
        }
    }

    /// <summary>Every mode, ordered by Content ID.</summary>
    public IReadOnlyList<MovementModeDefinition> All { get; }

    /// <summary>The mode that answers a trigger.</summary>
    public MovementModeDefinition For(MovementTrigger trigger) => _byTrigger[(int)trigger];
}
