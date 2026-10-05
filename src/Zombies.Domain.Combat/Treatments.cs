using System.Text.Json;
using Zombies.Domain.Items;

namespace Zombies.Domain.Combat;

public sealed class TreatmentDefinitionException(string message) : Exception(message);

/// <summary>
/// Definition of a Treatment: what applying it to a Body part does to the Wounds there, how long it takes, and the Item it
/// consumes. Combat applies the effect; the caller takes the time and consumes the Item.
/// </summary>
public sealed record Treatment
{
    public Treatment(string id, IEnumerable<string> removes, IEnumerable<string> adds, bool stopsBleeding, TimeSpan time, ItemId consumes)
    {
        ArgumentNullException.ThrowIfNull(removes);
        ArgumentNullException.ThrowIfNull(adds);
        WeaponValidation.ContentId(id, nameof(id));
        ArgumentOutOfRangeException.ThrowIfLessThan(time, TimeSpan.Zero);
        Removes = [.. removes.Select(k => Kind(k, nameof(removes))).Distinct()];
        Adds = [.. adds.Select(k => Kind(k, nameof(adds)))];
        if (Removes.Count == 0 && !stopsBleeding)
        {
            throw new ArgumentException("A Treatment must remove Wounds or stop bleeding.", nameof(removes));
        }

        Id = id;
        StopsBleeding = stopsBleeding;
        Time = time;
        Consumes = consumes;
    }

    public string Id { get; }

    /// <summary>Wound kinds the Treatment removes from the Body part.</summary>
    public IReadOnlyList<string> Removes { get; }

    /// <summary>Wound kinds the Treatment leaves on the Body part, once, when it removes or stops something.</summary>
    public IReadOnlyList<string> Adds { get; }

    /// <summary>Whether the Treatment stops every bleeding Wound on the part, whatever its kind.</summary>
    public bool StopsBleeding { get; }

    public TimeSpan Time { get; }

    /// <summary>The Item one Treatment uses up.</summary>
    public ItemId Consumes { get; }

    private static string Kind(string kind, string name)
    {
        WeaponValidation.ContentId(kind, name);
        return kind;
    }
}

/// <summary>JSON shape of a Treatment definition. This type is the source of the generated JSON Schema.</summary>
public sealed record TreatmentDto
{
    /// <summary>Content ID in the form <c>namespace:treatment/name</c>, such as <c>base:treatment/bandage</c>.</summary>
    public required string Id { get; init; }

    /// <summary>Content IDs of the Wound kinds it removes.</summary>
    public IReadOnlyList<string> Removes { get; init; } = [];

    /// <summary>Content IDs of the Wound kinds it leaves behind.</summary>
    public IReadOnlyList<string> Adds { get; init; } = [];

    /// <summary>Whether it stops every bleeding Wound on the part. Defaults to false.</summary>
    public bool StopsBleeding { get; init; }

    /// <summary>Seconds it takes.</summary>
    public required double Time { get; init; }

    /// <summary>Content ID of the Item it consumes, such as <c>base:item/bandage</c>.</summary>
    public required string Consumes { get; init; }
}

/// <summary>Parses a Treatment definition from JSON.</summary>
public static class TreatmentJson
{
    public static Treatment Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        TreatmentDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<TreatmentDto>(json, DefinitionJson.Options);
        }
        catch (JsonException ex)
        {
            throw new TreatmentDefinitionException($"Invalid Treatment definition: {ex.Message}");
        }

        if (dto is null)
        {
            throw new TreatmentDefinitionException("A Treatment definition must be a JSON object.");
        }

        if (!ItemId.TryParse(dto.Consumes, out var consumes))
        {
            throw new TreatmentDefinitionException($"Treatment '{dto.Id}' has an invalid 'consumes' value '{dto.Consumes}'.");
        }

        try
        {
            return new Treatment(dto.Id, dto.Removes, dto.Adds, dto.StopsBleeding, Seconds(dto.Time), consumes);
        }
        catch (ArgumentException ex)
        {
            throw new TreatmentDefinitionException($"Treatment '{dto.Id}' is invalid: {ex.Message}");
        }
    }

    private static TimeSpan Seconds(double value) =>
        double.IsFinite(value) && Math.Abs(value) < TimeSpan.MaxValue.TotalSeconds
            ? TimeSpan.FromSeconds(value)
            : throw new ArgumentOutOfRangeException(nameof(value), value, "Not a usable number of seconds.");
}

/// <summary>The Treatments the game knows.</summary>
public sealed class TreatmentCatalog
{
    private readonly Dictionary<string, Treatment> _treatments = [];

    /// <param name="kinds">The Wound kinds the Treatments may name.</param>
    public TreatmentCatalog(IEnumerable<Treatment> treatments, WoundKindCatalog kinds)
    {
        ArgumentNullException.ThrowIfNull(treatments);
        ArgumentNullException.ThrowIfNull(kinds);
        foreach (var treatment in treatments)
        {
            if (!_treatments.TryAdd(treatment.Id, treatment))
            {
                throw new ArgumentException($"Treatment '{treatment.Id}' is defined more than once.", nameof(treatments));
            }

            foreach (var kind in treatment.Removes.Concat(treatment.Adds).Where(k => !kinds.TryGet(k, out _)))
            {
                throw new ArgumentException($"Treatment '{treatment.Id}' names unknown Wound kind '{kind}'.", nameof(treatments));
            }
        }
    }

    public IReadOnlyCollection<Treatment> All => _treatments.Values;

    public bool TryGet(string id, out Treatment treatment) => _treatments.TryGetValue(id, out treatment!);

    /// <summary>The Treatments that would do something to a Body part right now, in Content ID order.</summary>
    public IReadOnlyList<Treatment> Available(Body body, BodyPart part)
    {
        ArgumentNullException.ThrowIfNull(body);
        return [.. _treatments.Values.Where(t => body.CanTreat(part, t)).OrderBy(t => t.Id, StringComparer.Ordinal)];
    }
}
