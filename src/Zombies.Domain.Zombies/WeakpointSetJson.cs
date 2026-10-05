using System.Numerics;
using System.Text.Json;
using Zombies.Domain.Items;

namespace Zombies.Domain.Zombies;

public sealed record PointDto
{
    public required double X { get; init; }

    public required double Y { get; init; }

    public required double Z { get; init; }
}

public sealed record WeakpointDto
{
    /// <summary>Name of the Weakpoint, such as <c>eyes</c>. Clients are told it when it is hit.</summary>
    public required string Name { get; init; }

    /// <summary>Name of the Body part it is in, such as <c>head</c>.</summary>
    public required string Part { get; init; }

    /// <summary>Center in the Body part's box, where the box spans 0 to 1 on each axis and the skeleton faces -Z.</summary>
    public required PointDto Center { get; init; }

    /// <summary>Side of the cube, as a fraction of the Body part's box, above 0 and at most 1.</summary>
    public required double Size { get; init; }

    /// <summary>What a hit's damage is multiplied by, at least 1. Defaults to 2.</summary>
    public double CriticalMultiplier { get; init; } = 2;

    /// <summary>How much smaller a ranged hit has to aim, from 0 up to but not including 1. Defaults to 0.</summary>
    public double RangedDifficulty { get; init; }

    /// <summary>How much smaller a melee hit has to aim, from 0 up to but not including 1. Defaults to 0.</summary>
    public double MeleeDifficulty { get; init; }

    /// <summary>What may happen when enough damage lands, such as <c>stagger</c>. Leave out for none.</summary>
    public string? Effect { get; init; }

    /// <summary>Damage, after the multiplier, a hit must reach before the effect has a chance. Defaults to 0.</summary>
    public double EffectThreshold { get; init; }

    /// <summary>Chance from 0 to 1 of the effect once the threshold is reached. Defaults to 0.</summary>
    public double EffectChance { get; init; }
}

/// <summary>
/// JSON shape of a Weakpoint set definition. This type is the source of the generated JSON Schema,
/// so keep it in step with <see cref="WeakpointSetJson"/>.
/// </summary>
public sealed record WeakpointSetDto
{
    /// <summary>Content ID in the form <c>namespace:weakpoint_set/name</c>, such as <c>base:weakpoint_set/humanoid</c>.</summary>
    public required string Id { get; init; }

    public required IReadOnlyList<WeakpointDto> Weakpoints { get; init; }
}

/// <summary>Parses a Weakpoint set definition from JSON.</summary>
public static class WeakpointSetJson
{
    public static WeakpointSet Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        WeakpointSetDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<WeakpointSetDto>(json, DefinitionJson.Options);
        }
        catch (JsonException ex)
        {
            throw new ZombieTypeDefinitionException($"Invalid Weakpoint set definition: {ex.Message}");
        }

        if (dto is null)
        {
            throw new ZombieTypeDefinitionException("A Weakpoint set definition must be a JSON object.");
        }

        try
        {
            return new WeakpointSet(dto.Id, dto.Weakpoints.Select(ToWeakpoint));
        }
        catch (ArgumentException ex)
        {
            throw new ZombieTypeDefinitionException($"Weakpoint set '{dto.Id}' is invalid: {ex.Message}");
        }
    }

    private static Weakpoint ToWeakpoint(WeakpointDto dto) =>
        new(
            dto.Name,
            Enum.TryParse<BodyPart>(dto.Part, ignoreCase: true, out var part) && Enum.IsDefined(part) ? part : throw new ArgumentException($"'{dto.Part}' is not a Body part."),
            new Vector3((float)dto.Center.X, (float)dto.Center.Y, (float)dto.Center.Z),
            (float)dto.Size,
            dto.CriticalMultiplier,
            dto.RangedDifficulty,
            dto.MeleeDifficulty,
            dto.Effect is null ? null : Enum.TryParse<WeakpointEffect>(dto.Effect, ignoreCase: true, out var effect) && Enum.IsDefined(effect) ? effect : throw new ArgumentException($"'{dto.Effect}' is not a Weakpoint effect."),
            dto.EffectThreshold,
            double.IsFinite(dto.EffectChance) && dto.EffectChance is >= 0 and <= 1 ? (int)Math.Round(dto.EffectChance * ZombieTypeDefinition.BasisPoints) : throw new ArgumentException("A chance must be between 0 and 1."));
}
