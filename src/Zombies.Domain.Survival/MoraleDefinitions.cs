using System.Text.Json;
using Zombies.Domain.Items;

namespace Zombies.Domain.Survival;

public sealed class MoraleDefinitionException(string message) : Exception(message);

/// <summary>What gives a Morale source: a named game event, such as a teammate dying, or a use of an Item, such as eating a comfort food.</summary>
public sealed record MoraleTrigger
{
    private MoraleTrigger(StatName? gameEvent, ItemId? item)
    {
        Event = gameEvent;
        Item = item;
    }

    public StatName? Event { get; }

    public ItemId? Item { get; }

    public static MoraleTrigger OnEvent(StatName gameEvent) => new(gameEvent, null);

    public static MoraleTrigger OnItem(ItemId item) => new(null, item);
}

/// <summary>A change to one Stat while Morale is in a band.</summary>
public sealed record MoraleModifier
{
    public MoraleModifier(StatName stat, ModifierOperation operation, double value)
    {
        _ = new Modifier(stat, operation, value, new ModifierSource("validation"));
        Stat = stat;
        Operation = operation;
        Value = value;
    }

    public StatName Stat { get; }

    public ModifierOperation Operation { get; }

    public double Value { get; }
}

/// <summary>
/// Definition of a Morale source: what triggers it, how much it moves Morale, and how long it lasts. The effect fades
/// in a straight line from <see cref="Size"/> to nothing over <see cref="Duration"/>.
/// </summary>
public sealed record MoraleSourceDefinition
{
    public MoraleSourceDefinition(string id, MoraleTrigger trigger, double size, TimeSpan duration, int maxStacks = 1)
    {
        ArgumentNullException.ThrowIfNull(trigger);
        if (!ItemId.TryParse(id, out _))
        {
            throw new ArgumentException($"'{id}' is not a valid Content ID.", nameof(id));
        }

        if (!double.IsFinite(size) || size == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(size), size, "A size must be a finite number other than 0.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(duration, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxStacks, 1);
        Id = id;
        Trigger = trigger;
        Size = size;
        Duration = duration;
        MaxStacks = maxStacks;
    }

    public string Id { get; }

    public MoraleTrigger Trigger { get; }

    /// <summary>How much one stack moves Morale when it has just been triggered. Negative lowers it.</summary>
    public double Size { get; }

    public TimeSpan Duration { get; }

    /// <summary>How many times the source can add up. Triggering it again at the limit only starts its duration over.</summary>
    public int MaxStacks { get; }
}

/// <summary>Definition of a Morale band: a range of Morale, starting at <see cref="Min"/>, that changes Stats while Morale is in it.</summary>
public sealed record MoraleBandDefinition
{
    public MoraleBandDefinition(string id, double min, IEnumerable<MoraleModifier>? modifiers = null)
    {
        if (!ItemId.TryParse(id, out _))
        {
            throw new ArgumentException($"'{id}' is not a valid Content ID.", nameof(id));
        }

        if (!double.IsFinite(min))
        {
            throw new ArgumentOutOfRangeException(nameof(min), min, "A minimum must be a finite number.");
        }

        Id = id;
        Min = min;
        Modifiers = [.. modifiers ?? []];
    }

    public string Id { get; }

    /// <summary>The lowest Morale in the band. The band lasts until the next band's minimum.</summary>
    public double Min { get; }

    public IReadOnlyList<MoraleModifier> Modifiers { get; }
}

/// <summary>JSON shape of what triggers a Morale source. Give exactly one of <c>event</c> and <c>item</c>.</summary>
public sealed record MoraleTriggerDto
{
    /// <summary>Name of a game event, such as <c>teammate_died</c>.</summary>
    public string? Event { get; init; }

    /// <summary>Content ID of an Item whose use triggers the source, such as <c>base:item/chocolate_bar</c>.</summary>
    public string? Item { get; init; }
}

/// <summary>JSON shape of a Morale source definition. This type is the source of the generated JSON Schema.</summary>
public sealed record MoraleSourceDto
{
    /// <summary>Content ID in the form <c>namespace:morale_source/name</c>, such as <c>base:morale_source/teammate_death</c>.</summary>
    public required string Id { get; init; }

    public required MoraleTriggerDto Trigger { get; init; }

    /// <summary>How much one stack moves Morale at first. Negative lowers it.</summary>
    public required double Size { get; init; }

    /// <summary>Seconds the effect takes to fade away.</summary>
    public required double Duration { get; init; }

    /// <summary>Most stacks the source can add up to. Defaults to 1.</summary>
    public int MaxStacks { get; init; } = 1;
}

/// <summary>JSON shape of a change to one Stat while Morale is in a band.</summary>
public sealed record MoraleModifierDto
{
    /// <summary>Name of the Stat to change, such as <c>aim_spread</c>.</summary>
    public required string Stat { get; init; }

    /// <summary><c>add</c> to add the value, or <c>multiply</c> to scale the Stat by it.</summary>
    public required ModifierOperation Operation { get; init; }

    public required double Value { get; init; }
}

/// <summary>JSON shape of a Morale band definition. This type is the source of the generated JSON Schema.</summary>
public sealed record MoraleBandDto
{
    /// <summary>Content ID in the form <c>namespace:morale_band/name</c>, such as <c>base:morale_band/low</c>.</summary>
    public required string Id { get; init; }

    /// <summary>The lowest Morale in the band. It lasts until the next band's minimum.</summary>
    public required double Min { get; init; }

    public IReadOnlyList<MoraleModifierDto> Modifiers { get; init; } = [];
}

/// <summary>Parses a Morale source definition from JSON.</summary>
public static class MoraleSourceJson
{
    public static MoraleSourceDefinition Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        MoraleSourceDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<MoraleSourceDto>(json, DefinitionJson.Options);
        }
        catch (JsonException ex)
        {
            throw new MoraleDefinitionException($"Invalid Morale source definition: {ex.Message}");
        }

        if (dto is null)
        {
            throw new MoraleDefinitionException("A Morale source definition must be a JSON object.");
        }

        try
        {
            return new MoraleSourceDefinition(dto.Id, ToTrigger(dto.Trigger), dto.Size, Seconds(dto.Duration), dto.MaxStacks);
        }
        catch (ArgumentException ex)
        {
            throw new MoraleDefinitionException($"Morale source '{dto.Id}' is invalid: {ex.Message}");
        }
    }

    private static MoraleTrigger ToTrigger(MoraleTriggerDto dto) => (dto.Event, dto.Item) switch
    {
        ({ } name, null) => MoraleTrigger.OnEvent(StatName.TryParse(name, out var gameEvent) ? gameEvent : throw new ArgumentException($"'{name}' is not a valid event name.")),
        (null, { } item) => MoraleTrigger.OnItem(ItemId.TryParse(item, out var id) ? id : throw new ArgumentException($"'{item}' is not a valid Content ID.")),
        _ => throw new ArgumentException("A trigger names exactly one of 'event' and 'item'."),
    };

    private static TimeSpan Seconds(double value) =>
        double.IsFinite(value) && Math.Abs(value) < TimeSpan.MaxValue.TotalSeconds
            ? TimeSpan.FromSeconds(value)
            : throw new ArgumentOutOfRangeException(nameof(value), value, "Not a usable number of seconds.");
}

/// <summary>Parses a Morale band definition from JSON.</summary>
public static class MoraleBandJson
{
    public static MoraleBandDefinition Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        MoraleBandDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<MoraleBandDto>(json, DefinitionJson.Options);
        }
        catch (JsonException ex)
        {
            throw new MoraleDefinitionException($"Invalid Morale band definition: {ex.Message}");
        }

        if (dto is null)
        {
            throw new MoraleDefinitionException("A Morale band definition must be a JSON object.");
        }

        try
        {
            return new MoraleBandDefinition(
                dto.Id,
                dto.Min,
                dto.Modifiers.Select(m => new MoraleModifier(StatName.TryParse(m.Stat, out var stat) ? stat : throw new ArgumentException($"'{m.Stat}' is not a valid Stat name."), m.Operation, m.Value)));
        }
        catch (ArgumentException ex)
        {
            throw new MoraleDefinitionException($"Morale band '{dto.Id}' is invalid: {ex.Message}");
        }
    }
}

/// <summary>The Morale sources and bands the game knows.</summary>
public sealed class MoraleCatalog
{
    private readonly Dictionary<string, MoraleSourceDefinition> _sources = [];

    public MoraleCatalog(IEnumerable<MoraleSourceDefinition> sources, IEnumerable<MoraleBandDefinition>? bands = null)
    {
        ArgumentNullException.ThrowIfNull(sources);
        foreach (var source in sources)
        {
            if (!_sources.TryAdd(source.Id, source))
            {
                throw new ArgumentException($"Morale source '{source.Id}' is defined more than once.", nameof(sources));
            }
        }

        Bands = [.. (bands ?? []).OrderBy(b => b.Min)];
        for (var i = 0; i < Bands.Count; i++)
        {
            if (Bands.Take(i).Any(b => b.Id == Bands[i].Id))
            {
                throw new ArgumentException($"Morale band '{Bands[i].Id}' is defined more than once.", nameof(bands));
            }

            if (i > 0 && Bands[i].Min == Bands[i - 1].Min)
            {
                throw new ArgumentException($"Morale bands '{Bands[i - 1].Id}' and '{Bands[i].Id}' start at the same Morale.", nameof(bands));
            }
        }
    }

    public IReadOnlyCollection<MoraleSourceDefinition> Sources => _sources.Values;

    /// <summary>The bands from the lowest Morale to the highest.</summary>
    public IReadOnlyList<MoraleBandDefinition> Bands { get; }

    public bool TryGetSource(string id, out MoraleSourceDefinition source) => _sources.TryGetValue(id, out source!);

    /// <summary>The band that holds this Morale, or null when it is below the lowest band.</summary>
    public MoraleBandDefinition? BandAt(double morale) => Bands.LastOrDefault(b => b.Min <= morale);
}
