using System.Text.Json;
using Zombies.Domain.Items;

namespace Zombies.Domain.Combat;

public sealed class LimbScoreDefinitionException(string message) : Exception(message);

/// <summary>A Body part that contributes to a Limb score. A part with more weight counts for more of the score.</summary>
public sealed record LimbScorePart
{
    public LimbScorePart(BodyPart part, double weight = 1, bool required = false)
    {
        if (!Enum.IsDefined(part))
        {
            throw new ArgumentOutOfRangeException(nameof(part), part, "Unknown body part.");
        }

        if (!double.IsFinite(weight) || weight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(weight), weight, "A weight must be a finite number above zero.");
        }

        Part = part;
        Weight = weight;
        Required = required;
    }

    public BodyPart Part { get; }

    public double Weight { get; }

    /// <summary>Whether losing this part drops the whole score to its floor, rather than only taking its share away.</summary>
    public bool Required { get; }
}

/// <summary>
/// Definition of a Limb score: how well a capability such as movement or grip works, from 0 to 1, given the condition of the
/// Body parts that perform it. The score is exposed as a multiplying Modifier on <see cref="Stat"/>.
/// </summary>
public sealed record LimbScoreDefinition
{
    public LimbScoreDefinition(string id, StatName stat, IEnumerable<LimbScorePart> parts, double floor = 0, bool woundsReduce = false, bool encumbranceReduces = false)
    {
        ArgumentNullException.ThrowIfNull(parts);
        WeaponValidation.ContentId(id, nameof(id));
        if (!double.IsFinite(floor) || floor is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(floor), floor, "A floor must be between 0 and 1.");
        }

        Parts = [.. parts];
        if (Parts.Count == 0)
        {
            throw new ArgumentException("A Limb score needs at least one contributing body part.", nameof(parts));
        }

        if (Parts.Select(p => p.Part).Distinct().Count() != Parts.Count)
        {
            throw new ArgumentException("A body part can contribute to a Limb score only once.", nameof(parts));
        }

        Id = id;
        Stat = stat;
        Floor = floor;
        WoundsReduce = woundsReduce;
        EncumbranceReduces = encumbranceReduces;
    }

    public string Id { get; }

    /// <summary>The Stat the score multiplies, such as <c>move_speed</c>.</summary>
    public StatName Stat { get; }

    public IReadOnlyList<LimbScorePart> Parts { get; }

    /// <summary>The lowest the score can fall, and the score when a required part is a Missing part.</summary>
    public double Floor { get; }

    /// <summary>Whether Wounds on a contributing part reduce the score.</summary>
    public bool WoundsReduce { get; }

    /// <summary>Whether the encumbrance of worn items covering a contributing part reduces the score.</summary>
    public bool EncumbranceReduces { get; }
}

/// <summary>JSON shape of one contributing Body part of a Limb score.</summary>
public sealed record LimbScorePartDto
{
    /// <summary>Body part, such as <c>leftLeg</c>.</summary>
    public required string Part { get; init; }

    /// <summary>How much of the score this part counts for. Defaults to 1.</summary>
    public double Weight { get; init; } = 1;

    /// <summary>Whether losing this part drops the score to its floor. Defaults to false.</summary>
    public bool Required { get; init; }
}

/// <summary>JSON shape of a Limb score definition. This type is the source of the generated JSON Schema.</summary>
public sealed record LimbScoreDto
{
    /// <summary>Content ID in the form <c>namespace:limb_score/name</c>, such as <c>base:limb_score/movement</c>.</summary>
    public required string Id { get; init; }

    /// <summary>Name of the Stat the score multiplies, such as <c>move_speed</c>.</summary>
    public required string Stat { get; init; }

    public required IReadOnlyList<LimbScorePartDto> Parts { get; init; }

    /// <summary>The lowest the score can fall, from 0 to 1. Defaults to 0.</summary>
    public double Floor { get; init; }

    /// <summary>Whether Wounds on the parts reduce the score. Defaults to false.</summary>
    public bool WoundsReduce { get; init; }

    /// <summary>Whether the encumbrance of worn items on the parts reduces the score. Defaults to false.</summary>
    public bool EncumbranceReduces { get; init; }
}

/// <summary>Parses a Limb score definition from JSON.</summary>
public static class LimbScoreJson
{
    public static LimbScoreDefinition Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        LimbScoreDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<LimbScoreDto>(json, DefinitionJson.Options);
        }
        catch (JsonException ex)
        {
            throw new LimbScoreDefinitionException($"Invalid Limb score definition: {ex.Message}");
        }

        if (dto is null)
        {
            throw new LimbScoreDefinitionException("A Limb score definition must be a JSON object.");
        }

        if (!StatName.TryParse(dto.Stat, out var stat))
        {
            throw new LimbScoreDefinitionException($"Limb score '{dto.Id}' has an invalid 'stat' value '{dto.Stat}'.");
        }

        try
        {
            return new LimbScoreDefinition(
                dto.Id,
                stat,
                dto.Parts.Select(p => Enum.TryParse<BodyPart>(p.Part, ignoreCase: true, out var part)
                    ? new LimbScorePart(part, p.Weight, p.Required)
                    : throw new ArgumentException($"'{p.Part}' is not a body part.")),
                dto.Floor,
                dto.WoundsReduce,
                dto.EncumbranceReduces);
        }
        catch (ArgumentException ex)
        {
            throw new LimbScoreDefinitionException($"Limb score '{dto.Id}' is invalid: {ex.Message}");
        }
    }
}

public enum LimbScoreCause
{
    /// <summary>The part is a Missing part.</summary>
    Missing,

    /// <summary>The part has lost health.</summary>
    Injured,

    /// <summary>The part has Wounds.</summary>
    Wounded,

    /// <summary>Worn items covering the part hinder it.</summary>
    Encumbered,
}

/// <summary>Why one Body part holds a Limb score back.</summary>
public sealed record LimbScoreReduction(BodyPart Part, LimbScoreCause Cause);

/// <summary>A computed Limb score from 0 to 1, and what holds it back.</summary>
public sealed record LimbScore(string Definition, StatName Stat, double Value, IReadOnlyList<LimbScoreReduction> Reductions)
{
    public bool IsReduced => Value < 1;

    /// <summary>The Modifier that applies this score to its Stat.</summary>
    public Modifier ToModifier(ModifierSource source) => new(Stat, ModifierOperation.Multiply, Value, source);
}

/// <summary>Computes Limb scores from the state of a Body and what it wears. Nothing here changes the Body.</summary>
public static class LimbScores
{
    /// <summary>The share of a part's capability that Wounds with a combined severity of 1 take away.</summary>
    public const double WoundPenalty = 0.5;

    /// <summary>A score is the weighted average of how well each part performs, between its floor and 1.</summary>
    public static IReadOnlyList<LimbScore> Compute(IEnumerable<LimbScoreDefinition> definitions, Body body, IEnumerable<WearableDefinition> worn)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(worn);
        var wearing = worn.ToList();
        return [.. definitions.Select(d => Compute(d, body, wearing))];
    }

    /// <summary>One Modifier per score, all granted by <paramref name="source"/>.</summary>
    public static ModifierSet ToModifiers(IEnumerable<LimbScore> scores, ModifierSource source)
    {
        ArgumentNullException.ThrowIfNull(scores);
        var modifiers = new ModifierSet();
        foreach (var score in scores)
        {
            modifiers.Add(score.ToModifier(source));
        }

        return modifiers;
    }

    private static LimbScore Compute(LimbScoreDefinition definition, Body body, List<WearableDefinition> worn)
    {
        var reductions = new List<LimbScoreReduction>();
        var dropped = false;
        var total = 0.0;
        var achieved = 0.0;

        foreach (var contribution in definition.Parts)
        {
            var part = contribution.Part;
            total += contribution.Weight;
            if (body.IsMissing(part))
            {
                reductions.Add(new LimbScoreReduction(part, LimbScoreCause.Missing));
                dropped |= contribution.Required;
                continue;
            }

            var capability = body.HealthFraction(part);
            if (capability < 1)
            {
                reductions.Add(new LimbScoreReduction(part, LimbScoreCause.Injured));
            }

            var severity = definition.WoundsReduce ? Math.Min(1, body.Wounds.Where(w => w.Part == part).Sum(w => w.Severity)) : 0;
            if (severity > 0)
            {
                capability *= 1 - (WoundPenalty * severity);
                reductions.Add(new LimbScoreReduction(part, LimbScoreCause.Wounded));
            }

            var encumbrance = definition.EncumbranceReduces ? Math.Min(1, worn.Where(w => w.Coverage.Contains(part)).Sum(w => w.Encumbrance)) : 0;
            if (encumbrance > 0)
            {
                capability *= 1 - encumbrance;
                reductions.Add(new LimbScoreReduction(part, LimbScoreCause.Encumbered));
            }

            achieved += contribution.Weight * capability;
        }

        var value = dropped ? definition.Floor : definition.Floor + ((1 - definition.Floor) * achieved / total);
        return new LimbScore(definition.Id, definition.Stat, value, reductions);
    }
}
