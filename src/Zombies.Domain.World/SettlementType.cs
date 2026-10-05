using System.Text.Json;
using Zombies.Domain.Items;

namespace Zombies.Domain.World;

public sealed class SettlementTypeException(string message) : Exception(message);

/// <summary>How many copies of a Structure a Settlement of some type holds, as a range.</summary>
public sealed record StructureCount(string Structure, int MinCount, int MaxCount);

/// <summary>An Area type a Settlement type can give to an Area that its Structure leaves open, and its chance against the others.</summary>
public sealed record WeightedAreaType(string AreaType, int Weight);

/// <summary>A kind of zombie that appears in a Settlement, and how many.</summary>
public sealed record ZombieSpawnRule(string ZombieType, int MinCount, int MaxCount);

/// <summary>
/// A kind of Settlement: which Structures it holds, which Area types its open Areas can become, which zombies live there,
/// how dangerous a Region has to be to hold one, how common it is, and whether anyone lives there.
/// </summary>
public sealed record SettlementType
{
    public const int MaxStructures = 64;
    public const int MaxZombies = 64;

    public SettlementType(
        string id,
        bool inhabited,
        int rarity,
        int minDanger,
        int maxDanger,
        IEnumerable<StructureCount> structures,
        IEnumerable<WeightedAreaType> areaTypes,
        IEnumerable<ZombieSpawnRule> zombieSpawns)
    {
        ArgumentNullException.ThrowIfNull(structures);
        ArgumentNullException.ThrowIfNull(areaTypes);
        ArgumentNullException.ThrowIfNull(zombieSpawns);
        if (!ItemId.TryParse(id, out _))
        {
            throw new ArgumentException($"'{id}' is not a valid Content ID.", nameof(id));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(rarity, 1);
        if (minDanger < RegionGrid.MinDanger || maxDanger > RegionGrid.MaxDanger || minDanger > maxDanger)
        {
            throw new ArgumentOutOfRangeException(nameof(minDanger), $"The danger range must satisfy {RegionGrid.MinDanger} <= min <= max <= {RegionGrid.MaxDanger}.");
        }

        var structureList = structures.ToList();
        if (structureList.Count == 0)
        {
            throw new ArgumentException($"Settlement type '{id}' needs at least one Structure.", nameof(structures));
        }

        foreach (var entry in structureList)
        {
            if (!ItemId.TryParse(entry.Structure, out _))
            {
                throw new ArgumentException($"'{entry.Structure}' is not a valid Content ID.", nameof(structures));
            }

            CheckCount(entry.MinCount, entry.MaxCount, nameof(structures));
            if (entry.MaxCount < 1)
            {
                throw new ArgumentException($"Structure '{entry.Structure}' in '{id}' is never placed; its maximum count must be at least 1.", nameof(structures));
            }
        }

        if (structureList.Sum(s => s.MaxCount) > MaxStructures)
        {
            throw new ArgumentException($"Settlement type '{id}' can hold at most {MaxStructures} Structures.", nameof(structures));
        }

        var areaList = areaTypes.ToList();
        foreach (var entry in areaList)
        {
            if (!ItemId.TryParse(entry.AreaType, out _))
            {
                throw new ArgumentException($"'{entry.AreaType}' is not a valid Content ID.", nameof(areaTypes));
            }

            ArgumentOutOfRangeException.ThrowIfLessThan(entry.Weight, 1);
        }

        var zombieList = zombieSpawns.ToList();
        foreach (var entry in zombieList)
        {
            if (!ItemId.TryParse(entry.ZombieType, out _))
            {
                throw new ArgumentException($"'{entry.ZombieType}' is not a valid Content ID.", nameof(zombieSpawns));
            }

            CheckCount(entry.MinCount, entry.MaxCount, nameof(zombieSpawns));
        }

        if (zombieList.Sum(z => z.MaxCount) > MaxZombies)
        {
            throw new ArgumentException($"Settlement type '{id}' can hold at most {MaxZombies} zombies.", nameof(zombieSpawns));
        }

        Id = id;
        Inhabited = inhabited;
        Rarity = rarity;
        MinDanger = minDanger;
        MaxDanger = maxDanger;
        Structures = structureList;
        AreaTypes = areaList;
        ZombieSpawns = zombieList;
    }

    public string Id { get; }

    /// <summary>True when people live here. The faction, population, and trade stock of an inhabited Settlement come with later work.</summary>
    public bool Inhabited { get; }

    /// <summary>Chance of this type against the other types that fit a Region, as a weight.</summary>
    public int Rarity { get; }

    public int MinDanger { get; }

    public int MaxDanger { get; }

    public IReadOnlyList<StructureCount> Structures { get; }

    public IReadOnlyList<WeightedAreaType> AreaTypes { get; }

    public IReadOnlyList<ZombieSpawnRule> ZombieSpawns { get; }

    public bool AllowsDanger(int dangerLevel) => dangerLevel >= MinDanger && dangerLevel <= MaxDanger;

    private static void CheckCount(int min, int max, string parameter)
    {
        if (min < 0 || max < min)
        {
            throw new ArgumentException("A count range must satisfy 0 <= min <= max.", parameter);
        }
    }
}

public sealed record StructureCountDto
{
    /// <summary>Content ID of the Structure, such as <c>base:structure/house_small</c>.</summary>
    public required string Structure { get; init; }

    public required IntervalDto Count { get; init; }
}

public sealed record WeightedAreaTypeDto
{
    /// <summary>Content ID of the Area type, such as <c>base:area_type/kitchen</c>.</summary>
    public required string AreaType { get; init; }

    /// <summary>Chance of this Area type relative to the others. Defaults to 1.</summary>
    public int Weight { get; init; } = 1;
}

public sealed record ZombieSpawnDto
{
    /// <summary>Content ID of the zombie type, such as <c>base:zombie/walker</c>.</summary>
    public required string ZombieType { get; init; }

    public required IntervalDto Count { get; init; }
}

/// <summary>
/// JSON shape of a Settlement type. This type is the source of the generated JSON Schema,
/// so keep it in step with <see cref="SettlementTypeJson"/>.
/// </summary>
public sealed record SettlementTypeDto
{
    /// <summary>Content ID in the form <c>namespace:settlement_type/name</c>.</summary>
    public required string Id { get; init; }

    /// <summary>True when people live in the Settlement, false when it is abandoned.</summary>
    public required bool Inhabited { get; init; }

    /// <summary>Chance of this type against the others that fit a Region, as a weight of at least 1.</summary>
    public required int Rarity { get; init; }

    /// <summary>The danger levels, 1 to 10, of the Regions where this type can appear.</summary>
    public required IntervalDto Danger { get; init; }

    public required IReadOnlyList<StructureCountDto> Structures { get; init; }

    /// <summary>The Area types given to Areas that their Structure leaves open. Needed when any Structure does.</summary>
    public required IReadOnlyList<WeightedAreaTypeDto> AreaTypes { get; init; }

    public required IReadOnlyList<ZombieSpawnDto> ZombieSpawns { get; init; }
}

public static class SettlementTypeJson
{
    public static SettlementType Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        SettlementTypeDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<SettlementTypeDto>(json, DefinitionJson.Options);
        }
        catch (JsonException ex)
        {
            throw new SettlementTypeException($"Invalid Settlement type: {ex.Message}");
        }

        if (dto is null)
        {
            throw new SettlementTypeException("A Settlement type must be a JSON object.");
        }

        try
        {
            return new SettlementType(
                dto.Id,
                dto.Inhabited,
                dto.Rarity,
                dto.Danger.Min,
                dto.Danger.Max,
                dto.Structures.Select(s => new StructureCount(s.Structure, s.Count.Min, s.Count.Max)),
                dto.AreaTypes.Select(a => new WeightedAreaType(a.AreaType, a.Weight)),
                dto.ZombieSpawns.Select(z => new ZombieSpawnRule(z.ZombieType, z.Count.Min, z.Count.Max)));
        }
        catch (ArgumentException ex)
        {
            throw new SettlementTypeException($"Settlement type '{dto.Id}' is invalid: {ex.Message}");
        }
    }
}
