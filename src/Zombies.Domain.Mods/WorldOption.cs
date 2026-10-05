using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Zombies.Domain.Items;

namespace Zombies.Domain.Mods;

/// <summary>What kind of value a World option holds.</summary>
[JsonConverter(typeof(WorldOptionTypeJsonConverter))]
public enum WorldOptionType
{
    /// <summary>Off or on, held as 0 or 1.</summary>
    [JsonStringEnumMemberName("boolean")]
    Toggle,

    /// <summary>A whole number within the range.</summary>
    [JsonStringEnumMemberName("integer")]
    WholeNumber,

    /// <summary>Any number within the range, such as a multiplier.</summary>
    [JsonStringEnumMemberName("number")]
    Number,
}

public sealed class WorldOptionTypeJsonConverter() : JsonStringEnumConverter<WorldOptionType>(namingPolicy: null, allowIntegerValues: false);

public sealed class WorldOptionException(string message) : Exception(message);

/// <summary>
/// JSON shape of a World option. This type is the source of the generated JSON Schema,
/// so keep it in step with <see cref="WorldOptionJson"/>.
/// </summary>
public sealed record WorldOptionDto
{
    /// <summary>Content ID in the form <c>namespace:world_option/name</c>.</summary>
    public required string Id { get; init; }

    public required WorldOptionType Type { get; init; }

    /// <summary>Smallest value allowed. Required for integer and number options, and left out for boolean ones.</summary>
    public double? Min { get; init; }

    /// <summary>Largest value allowed. Required for integer and number options, and left out for boolean ones.</summary>
    public double? Max { get; init; }

    /// <summary>The value used when the host chooses none: 0 or 1 for a boolean option.</summary>
    public required double Default { get; init; }

    /// <summary>One sentence telling the host what the option changes.</summary>
    public required string Description { get; init; }

    /// <summary>
    /// Whether the option changes the simulation. Defaults to true. Those options must match between a client and the Server
    /// at join; one that only changes what a client shows would set this to false.
    /// </summary>
    public bool AffectsSimulation { get; init; } = true;
}

/// <summary>A tunable rule of one world that a mod declares and the host sets when the world is created.</summary>
public sealed record WorldOption
{
    public WorldOption(ContentId id, WorldOptionType type, double min, double max, double defaultValue, string description, bool affectsSimulation = true)
    {
        if (id.Kind != "world_option")
        {
            throw new ArgumentException($"World option '{id}' must be a Content ID of the form namespace:world_option/name.", nameof(id));
        }

        if (!double.IsFinite(min) || !double.IsFinite(max) || min > max)
        {
            throw new ArgumentException($"World option '{id}' needs a finite range with min no larger than max.", nameof(min));
        }

        Id = id;
        Type = type;
        Min = min;
        Max = max;
        Default = defaultValue;
        Description = description;
        AffectsSimulation = affectsSimulation;
        if (Validate(defaultValue) is { } problem)
        {
            throw new ArgumentException($"The default of '{id}' is invalid: {problem}", nameof(defaultValue));
        }
    }

    public ContentId Id { get; }

    public WorldOptionType Type { get; }

    public double Min { get; }

    public double Max { get; }

    public double Default { get; }

    public string Description { get; }

    public bool AffectsSimulation { get; }

    /// <summary>Says why <paramref name="value"/> is not allowed for this option, or null when it is.</summary>
    public string? Validate(double value)
    {
        if (!double.IsFinite(value))
        {
            return "the value must be a real number.";
        }

        if (Type == WorldOptionType.Toggle && value is not (0 or 1))
        {
            return "a boolean option is 0 or 1.";
        }

        if (Type == WorldOptionType.WholeNumber && value != Math.Floor(value))
        {
            return "an integer option takes a whole number.";
        }

        return value < Min || value > Max
            ? string.Create(CultureInfo.InvariantCulture, $"the value {value} is outside {Min} to {Max}.")
            : null;
    }
}

/// <summary>Parses a World option from JSON.</summary>
public static class WorldOptionJson
{
    public static WorldOption Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        WorldOptionDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<WorldOptionDto>(json, DefinitionJson.Options);
        }
        catch (JsonException ex)
        {
            throw new WorldOptionException($"Invalid World option: {ex.Message}");
        }

        if (dto is null)
        {
            throw new WorldOptionException("A World option must be a JSON object.");
        }

        if (!ContentId.TryParse(dto.Id, out var id))
        {
            throw new WorldOptionException($"'id' value '{dto.Id}' is not a valid Content ID.");
        }

        double min;
        double max;
        if (dto.Type == WorldOptionType.Toggle)
        {
            if (dto.Min is not null || dto.Max is not null)
            {
                throw new WorldOptionException($"World option '{dto.Id}' is boolean, so it takes no 'min' or 'max'.");
            }

            (min, max) = (0, 1);
        }
        else if (dto.Min is { } low && dto.Max is { } high)
        {
            (min, max) = (low, high);
        }
        else
        {
            throw new WorldOptionException($"World option '{dto.Id}' needs both 'min' and 'max'.");
        }

        try
        {
            return new WorldOption(id, dto.Type, min, max, dto.Default, dto.Description, dto.AffectsSimulation);
        }
        catch (ArgumentException ex)
        {
            throw new WorldOptionException(ex.Message);
        }
    }
}

/// <summary>Every World option the loaded mods declare.</summary>
public sealed class WorldOptionCatalog
{
    private readonly Dictionary<ContentId, WorldOption> _options;

    public WorldOptionCatalog(IEnumerable<WorldOption> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.ToDictionary(o => o.Id);
    }

    /// <summary>Every option, ordered by Content ID.</summary>
    public IReadOnlyList<WorldOption> All => [.. _options.Values.OrderBy(o => o.Id.Value, StringComparer.Ordinal)];

    public bool TryGet(ContentId id, out WorldOption option) => _options.TryGetValue(id, out option!);

    /// <exception cref="WorldOptionException">A World option definition is invalid.</exception>
    public static WorldOptionCatalog From(DefinitionRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return new WorldOptionCatalog(registry.OfKind("world_option").Select(d => WorldOptionJson.Parse(d.Json)));
    }
}

/// <summary>
/// The one place systems read World options. A world holds the values its host chose; an option the host did not set
/// reads as the default its mod declared.
/// </summary>
public sealed class WorldOptions
{
    private readonly WorldOptionCatalog _catalog;
    private readonly SortedDictionary<string, double> _chosen = new(StringComparer.Ordinal);

    /// <param name="chosen">Values the host set, by Content ID.</param>
    /// <exception cref="WorldOptionException">A chosen value names no declared option or is not allowed for it.</exception>
    public WorldOptions(WorldOptionCatalog catalog, IEnumerable<KeyValuePair<string, double>>? chosen = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _catalog = catalog;
        foreach (var (id, value) in chosen ?? [])
        {
            if (!ContentId.TryParse(id, out var contentId) || !catalog.TryGet(contentId, out var option))
            {
                throw new WorldOptionException($"'{id}' is not a World option any loaded mod declares.");
            }

            if (option.Validate(value) is { } problem)
            {
                throw new WorldOptionException($"World option '{id}': {problem}");
            }

            _chosen[id] = value;
        }
    }

    /// <summary>The values the host set, ordered by Content ID. This is what a save records.</summary>
    public IReadOnlyList<KeyValuePair<string, double>> Chosen => [.. _chosen];

    /// <summary>The chosen value, or the declared default when unset.</summary>
    /// <exception cref="WorldOptionException">No loaded mod declares <paramref name="id"/>.</exception>
    public double Get(string id)
    {
        if (_chosen.TryGetValue(id, out var value))
        {
            return value;
        }

        return ContentId.TryParse(id, out var contentId) && _catalog.TryGet(contentId, out var option)
            ? option.Default
            : throw new WorldOptionException($"'{id}' is not a World option any loaded mod declares.");
    }

    /// <summary>
    /// The value of every option that changes the simulation, set or not, ordered by Content ID. A client and the Server
    /// must hold the same list to play together.
    /// </summary>
    public IReadOnlyList<KeyValuePair<string, double>> SimulationValues =>
        [.. _catalog.All.Where(o => o.AffectsSimulation).Select(o => KeyValuePair.Create(o.Id.Value, Get(o.Id.Value)))];
}
