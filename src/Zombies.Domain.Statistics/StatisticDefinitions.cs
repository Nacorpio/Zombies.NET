using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Zombies.Domain.Items;
using Zombies.Domain.Mods;

namespace Zombies.Domain.Statistics;

/// <summary>A Statistic, Achievement or Conduct definition is invalid, or a stored state of a player's statistics is not one they can reach.</summary>
public sealed class StatisticsException(string message) : Exception(message);

/// <summary>How long a Statistic's value lasts.</summary>
[JsonConverter(typeof(StatisticScopeJsonConverter))]
public enum StatisticScope
{
    /// <summary>The value is kept across lives, so it keeps counting after the player respawns.</summary>
    [JsonStringEnumMemberName("lifetime")]
    Lifetime,

    /// <summary>The value counts one life and starts again from zero when the player respawns. Its final value is a score.</summary>
    [JsonStringEnumMemberName("life")]
    Life,
}

public sealed class StatisticScopeJsonConverter() : JsonStringEnumConverter<StatisticScope>(namingPolicy: null, allowIntegerValues: false);

/// <summary>JSON shape of the condition an event must meet to count. This type is the source of the generated JSON Schema.</summary>
public sealed record StatisticMatchDto
{
    /// <summary>Name of a property of the event, such as <c>Type</c>.</summary>
    public required string Property { get; init; }

    /// <summary>The value the property must have, written as text, such as <c>Bite</c>.</summary>
    public required string Value { get; init; }
}

/// <summary>JSON shape of one kind of event a Statistic counts.</summary>
public sealed record StatisticCountDto
{
    /// <summary>Name of a domain event, such as <c>ZombieKilled</c>.</summary>
    public required string Event { get; init; }

    /// <summary>Name of a number property of the event to add to the Statistic. Without it every event adds 1.</summary>
    public string? Sum { get; init; }

    /// <summary>Only events with this property value count. Without it every event of the kind counts.</summary>
    public StatisticMatchDto? Match { get; init; }
}

/// <summary>JSON shape of a Statistic definition. This type is the source of the generated JSON Schema.</summary>
public sealed record StatisticDto
{
    /// <summary>Content ID in the form <c>namespace:statistic/name</c>, such as <c>base:statistic/zombies_killed</c>.</summary>
    public required string Id { get; init; }

    /// <summary>Whether the value lasts for the player's lifetime or one life. Defaults to lifetime.</summary>
    public StatisticScope Scope { get; init; }

    public required IReadOnlyList<StatisticCountDto> Counts { get; init; }
}

/// <summary>The domain events a Statistic can name, by the name of their type.</summary>
public sealed class StatisticEventCatalog
{
    private readonly Dictionary<string, Type> _events = [];

    public StatisticEventCatalog(IEnumerable<Type> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        foreach (var type in events)
        {
            if (!typeof(IDomainEvent).IsAssignableFrom(type) || type.IsAbstract || type.IsInterface)
            {
                throw new ArgumentException($"{type.Name} is not a concrete domain event.", nameof(events));
            }

            if (!_events.TryAdd(type.Name, type))
            {
                throw new ArgumentException($"Two domain events are named {type.Name}.", nameof(events));
            }
        }
    }

    public IReadOnlyList<string> Names => [.. _events.Keys.Order(StringComparer.Ordinal)];

    public bool TryGet(string name, out Type type) => _events.TryGetValue(name, out type!);
}

/// <summary>One kind of event a Statistic counts, and how much each such event adds.</summary>
public sealed class StatisticCount
{
    private readonly Func<IDomainEvent, double>? _amount;
    private readonly Func<IDomainEvent, bool>? _match;

    internal StatisticCount(Type eventType, Func<IDomainEvent, double>? amount, Func<IDomainEvent, bool>? match)
    {
        EventType = eventType;
        _amount = amount;
        _match = match;
    }

    public Type EventType { get; }

    /// <summary>What <paramref name="domainEvent"/> adds, or false when it does not count.</summary>
    public bool TryAmount(IDomainEvent domainEvent, out double amount)
    {
        amount = 0;
        if (_match is not null && !_match(domainEvent))
        {
            return false;
        }

        amount = _amount?.Invoke(domainEvent) ?? 1;
        return true;
    }
}

/// <summary>Definition of a Statistic: a number the events a player causes add to.</summary>
public sealed record StatisticDefinition
{
    public StatisticDefinition(string id, StatisticScope scope, IEnumerable<StatisticCount> counts)
    {
        ArgumentNullException.ThrowIfNull(counts);
        Goal.RequireKind(id, "statistic", nameof(id));
        if (!Enum.IsDefined(scope))
        {
            throw new ArgumentOutOfRangeException(nameof(scope), scope, "Unknown scope.");
        }

        Counts = [.. counts];
        if (Counts.Count == 0)
        {
            throw new ArgumentException("A Statistic needs at least one event to count.", nameof(counts));
        }

        Id = id;
        Scope = scope;
    }

    public string Id { get; }

    public StatisticScope Scope { get; }

    public IReadOnlyList<StatisticCount> Counts { get; }
}

/// <summary>Parses a Statistic definition from JSON, resolving the events it names.</summary>
public static class StatisticJson
{
    public static StatisticDefinition Parse(string json, StatisticEventCatalog events)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(events);

        StatisticDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<StatisticDto>(json, DefinitionJson.Options);
        }
        catch (JsonException ex)
        {
            throw new StatisticsException($"Invalid Statistic definition: {ex.Message}");
        }

        if (dto is null)
        {
            throw new StatisticsException("A Statistic definition must be a JSON object.");
        }

        try
        {
            return new StatisticDefinition(dto.Id, dto.Scope, dto.Counts.Select(c => Count(c, events)));
        }
        catch (ArgumentException ex)
        {
            throw new StatisticsException($"Statistic '{dto.Id}' is invalid: {ex.Message}");
        }
    }

    private static StatisticCount Count(StatisticCountDto dto, StatisticEventCatalog events)
    {
        if (!events.TryGet(dto.Event, out var type))
        {
            throw new ArgumentException($"'{dto.Event}' is not a domain event a Statistic can count. Known events: {string.Join(", ", events.Names)}.");
        }

        var parameter = Expression.Parameter(typeof(IDomainEvent), "e");
        var typed = Expression.Convert(parameter, type);
        Func<IDomainEvent, double>? amount = null;
        if (dto.Sum is not null)
        {
            var property = Property(type, dto.Sum);
            if (!IsNumber(property.PropertyType))
            {
                throw new ArgumentException($"'{dto.Sum}' of {type.Name} is not a number, so it cannot be summed.");
            }

            amount = Expression.Lambda<Func<IDomainEvent, double>>(Expression.Convert(Expression.Property(typed, property), typeof(double)), parameter).Compile();
        }

        Func<IDomainEvent, bool>? match = null;
        if (dto.Match is { } condition)
        {
            var property = Property(type, condition.Property);
            var expected = Expected(property.PropertyType, condition.Value, type.Name, property.Name);
            match = Expression.Lambda<Func<IDomainEvent, bool>>(Expression.Equal(Expression.Property(typed, property), Expression.Constant(expected, property.PropertyType)), parameter).Compile();
        }

        return new StatisticCount(type, amount, match);
    }

    private static PropertyInfo Property(Type type, string name) =>
        type.GetProperty(name) ?? throw new ArgumentException($"{type.Name} has no property '{name}'.");

    private static bool IsNumber(Type type) => type == typeof(int) || type == typeof(long) || type == typeof(float) || type == typeof(double);

    private static object Expected(Type type, string text, string eventName, string property)
    {
        if (type.IsEnum)
        {
            return Enum.TryParse(type, text, ignoreCase: true, out var value) && Enum.IsDefined(type, value)
                ? value
                : throw new ArgumentException($"'{text}' is not a value of {property} of {eventName}.");
        }

        if (type == typeof(string))
        {
            return text;
        }

        if (type == typeof(bool) && bool.TryParse(text, out var flag))
        {
            return flag;
        }

        if (type == typeof(int) && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
        {
            return number;
        }

        throw new ArgumentException($"{property} of {eventName} cannot be matched against '{text}'. Only text, true or false, whole numbers and enum names can be matched.");
    }
}
