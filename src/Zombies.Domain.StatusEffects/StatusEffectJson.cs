using System.Text.Json;
using Zombies.Domain.Items;

namespace Zombies.Domain.StatusEffects;

public sealed class StatusEffectDefinitionException(string message) : Exception(message);

/// <summary>A change to one Stat while the effect is active.</summary>
public sealed record EffectModifierDto
{
    /// <summary>Name of the Stat to change, such as <c>move_speed</c>.</summary>
    public required string Stat { get; init; }

    /// <summary><c>add</c> to add the value, or <c>multiply</c> to scale the Stat by it.</summary>
    public required ModifierOperation Operation { get; init; }

    public required double Value { get; init; }
}

/// <summary>A change announced on a schedule for other contexts to apply.</summary>
public sealed record PeriodicChangeDto
{
    /// <summary>Name of what changes, such as <c>infection_damage</c>.</summary>
    public required string Change { get; init; }

    /// <summary>Seconds between changes.</summary>
    public required double Every { get; init; }

    /// <summary>How much each change is, per stack.</summary>
    public required double Amount { get; init; }
}

public sealed record EffectStageDto
{
    /// <summary>Name of the stage, such as <c>severe</c>.</summary>
    public required string Name { get; init; }

    /// <summary>Seconds after the effect was applied that this stage starts.</summary>
    public required double After { get; init; }

    public IReadOnlyList<EffectModifierDto> Modifiers { get; init; } = [];

    public IReadOnlyList<PeriodicChangeDto> Periodic { get; init; } = [];
}

public sealed record EffectCuresDto
{
    /// <summary>Content IDs of Items that cure the effect.</summary>
    public IReadOnlyList<string> Items { get; init; } = [];

    /// <summary>Content IDs of Status effects that cure it when they are applied.</summary>
    public IReadOnlyList<string> Effects { get; init; } = [];
}

/// <summary>
/// JSON shape of a Status effect definition. This type is the source of the generated JSON Schema,
/// so keep it in step with <see cref="StatusEffectJson"/>.
/// </summary>
public sealed record StatusEffectDto
{
    /// <summary>Content ID in the form <c>namespace:status_effect/name</c>, such as <c>base:status_effect/infection</c>.</summary>
    public required string Id { get; init; }

    public required EffectCategory Category { get; init; }

    /// <summary>Seconds the effect lasts. Leave out for an effect that lasts until removed or cured. Running out is how time cures it.</summary>
    public double? Duration { get; init; }

    /// <summary>What happens when applied again while active. Defaults to <c>refresh</c>.</summary>
    public StackingRule Stacking { get; init; } = StackingRule.Refresh;

    /// <summary>Most stacks when stacking is <c>stack</c>. Defaults to 1.</summary>
    public int MaxStacks { get; init; } = 1;

    /// <summary>Stages in order of their <c>after</c> time.</summary>
    public IReadOnlyList<EffectStageDto> Stages { get; init; } = [];

    /// <summary>Stat changes that apply for as long as the effect is active, once per stack.</summary>
    public IReadOnlyList<EffectModifierDto> Modifiers { get; init; } = [];

    public IReadOnlyList<PeriodicChangeDto> Periodic { get; init; } = [];

    public EffectCuresDto CuredBy { get; init; } = new();
}

/// <summary>Parses a Status effect definition from JSON.</summary>
public static class StatusEffectJson
{
    public static StatusEffectDefinition Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        StatusEffectDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<StatusEffectDto>(json, DefinitionJson.Options);
        }
        catch (JsonException ex)
        {
            throw new StatusEffectDefinitionException($"Invalid Status effect definition: {ex.Message}");
        }

        if (dto is null)
        {
            throw new StatusEffectDefinitionException("A Status effect definition must be a JSON object.");
        }

        try
        {
            return new StatusEffectDefinition(
                dto.Id,
                dto.Category,
                dto.Duration is { } seconds ? Seconds(seconds) : null,
                dto.Stacking,
                dto.MaxStacks,
                dto.Stages.Select(s => new EffectStage(s.Name, Seconds(s.After), s.Modifiers.Select(ToModifier), s.Periodic.Select(ToPeriodic))),
                dto.Modifiers.Select(ToModifier),
                dto.Periodic.Select(ToPeriodic),
                dto.CuredBy.Items.Select(i => ItemId.TryParse(i, out var item) ? item : throw new ArgumentException($"'{i}' is not a valid Content ID.")),
                dto.CuredBy.Effects);
        }
        catch (ArgumentException ex)
        {
            throw new StatusEffectDefinitionException($"Status effect '{dto.Id}' is invalid: {ex.Message}");
        }
    }

    private static TimeSpan Seconds(double value) =>
        double.IsFinite(value) && Math.Abs(value) < TimeSpan.MaxValue.TotalSeconds
            ? TimeSpan.FromSeconds(value)
            : throw new ArgumentOutOfRangeException(nameof(value), value, "Not a usable number of seconds.");

    private static EffectModifier ToModifier(EffectModifierDto dto) =>
        new(StatName.TryParse(dto.Stat, out var stat) ? stat : throw new ArgumentException($"'{dto.Stat}' is not a valid Stat name."), dto.Operation, dto.Value);

    private static PeriodicChange ToPeriodic(PeriodicChangeDto dto) =>
        new(StatName.TryParse(dto.Change, out var change) ? change : throw new ArgumentException($"'{dto.Change}' is not a valid change name."), Seconds(dto.Every), dto.Amount);
}
