using System.Text.Json;
using System.Text.Json.Serialization;
using Zombies.Domain.Items;
using Zombies.Domain.Mods;

namespace Zombies.Domain.Statistics;

/// <summary>How a Statistic's value is compared with a target.</summary>
[JsonConverter(typeof(ComparisonJsonConverter))]
public enum Comparison
{
    [JsonStringEnumMemberName("atLeast")]
    AtLeast,

    [JsonStringEnumMemberName("atMost")]
    AtMost,

    [JsonStringEnumMemberName("exactly")]
    Exactly,
}

public sealed class ComparisonJsonConverter() : JsonStringEnumConverter<Comparison>(namingPolicy: null, allowIntegerValues: false);

/// <summary>JSON shape of an Achievement or a Conduct. This type is the source of the generated JSON Schema for both.</summary>
public sealed record GoalDto
{
    /// <summary>Content ID in the form <c>namespace:achievement/name</c> or <c>namespace:conduct/name</c>.</summary>
    public required string Id { get; init; }

    /// <summary>Content ID of the Statistic that is compared, such as <c>base:statistic/zombies_killed</c>.</summary>
    public required string Statistic { get; init; }

    public required Comparison Comparison { get; init; }

    public required double Target { get; init; }
}

/// <summary>
/// What an Achievement or a Conduct asks of a Statistic: its value compared with a target. An Achievement is completed the
/// first time its goal is met. A Conduct is kept when its goal is still met as the run ends.
/// </summary>
public sealed record Goal
{
    public Goal(string id, string kind, string statistic, Comparison comparison, double target)
    {
        RequireKind(id, kind, nameof(id));
        RequireKind(statistic, "statistic", nameof(statistic));
        if (!Enum.IsDefined(comparison))
        {
            throw new ArgumentOutOfRangeException(nameof(comparison), comparison, "Unknown comparison.");
        }

        if (!double.IsFinite(target))
        {
            throw new ArgumentOutOfRangeException(nameof(target), target, "A target must be a finite number.");
        }

        Id = id;
        Statistic = statistic;
        Comparison = comparison;
        Target = target;
    }

    public string Id { get; }

    /// <summary>Content ID of the Statistic that is compared.</summary>
    public string Statistic { get; }

    public Comparison Comparison { get; }

    public double Target { get; }

    public bool IsMet(double value) => Comparison switch
    {
        Comparison.AtLeast => value >= Target,
        Comparison.AtMost => value <= Target,
        _ => value == Target,
    };

    internal static void RequireKind(string id, string kind, string parameter)
    {
        if (!ContentId.TryParse(id, out var parsed) || parsed.Kind != kind)
        {
            throw new ArgumentException($"'{id}' must be a Content ID of the form namespace:{kind}/name.", parameter);
        }
    }
}

/// <summary>Parses an Achievement or a Conduct from JSON.</summary>
public static class GoalJson
{
    /// <param name="kind">The definition kind the Content ID must have, <c>achievement</c> or <c>conduct</c>.</param>
    public static Goal Parse(string json, string kind)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(kind);

        GoalDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<GoalDto>(json, DefinitionJson.Options);
        }
        catch (JsonException ex)
        {
            throw new StatisticsException($"Invalid {kind} definition: {ex.Message}");
        }

        if (dto is null)
        {
            throw new StatisticsException($"A {kind} definition must be a JSON object.");
        }

        try
        {
            return new Goal(dto.Id, kind, dto.Statistic, dto.Comparison, dto.Target);
        }
        catch (ArgumentException ex)
        {
            throw new StatisticsException($"The {kind} '{dto.Id}' is invalid: {ex.Message}");
        }
    }
}
