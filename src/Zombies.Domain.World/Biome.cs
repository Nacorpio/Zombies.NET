using System.Text.Json;
using System.Text.RegularExpressions;
using Zombies.Domain.Items;

namespace Zombies.Domain.World;

public sealed class BiomeException(string message) : Exception(message);

/// <summary>A named climate and terrain type. Habitat tags say which animals and other content may live there.</summary>
public sealed partial class Biome
{
    public Biome(
        string id,
        IEnumerable<string> habitats,
        (int Min, int Max) temperature,
        (int Min, int Max) humidity,
        int baseHeight,
        int amplitude,
        int treesPerThousand)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentNullException.ThrowIfNull(habitats);
        CheckRange(temperature, nameof(temperature));
        CheckRange(humidity, nameof(humidity));
        ArgumentOutOfRangeException.ThrowIfLessThan(baseHeight, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(baseHeight, 100);
        ArgumentOutOfRangeException.ThrowIfNegative(amplitude);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(amplitude, 30);
        ArgumentOutOfRangeException.ThrowIfNegative(treesPerThousand);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(treesPerThousand, 1000);

        var tags = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var tag in habitats)
        {
            if (!HabitatPattern().IsMatch(tag))
            {
                throw new ArgumentException($"Habitat tag '{tag}' must use only a-z, 0-9 and _.", nameof(habitats));
            }

            tags.Add(tag);
        }

        Id = id;
        Habitats = tags;
        Temperature = temperature;
        Humidity = humidity;
        BaseHeight = baseHeight;
        Amplitude = amplitude;
        TreesPerThousand = treesPerThousand;
    }

    public string Id { get; }

    public IReadOnlySet<string> Habitats { get; }

    /// <summary>Temperature range, 0 (coldest) to 100 (hottest), in which this biome appears.</summary>
    public (int Min, int Max) Temperature { get; }

    /// <summary>Humidity range, 0 (driest) to 100 (wettest), in which this biome appears.</summary>
    public (int Min, int Max) Humidity { get; }

    /// <summary>Average ground height in blocks.</summary>
    public int BaseHeight { get; }

    /// <summary>How far hills rise above and fall below <see cref="BaseHeight"/>, in blocks.</summary>
    public int Amplitude { get; }

    /// <summary>Trees per thousand grass columns.</summary>
    public int TreesPerThousand { get; }

    public bool Contains(int temperature, int humidity) =>
        temperature >= Temperature.Min && temperature <= Temperature.Max
        && humidity >= Humidity.Min && humidity <= Humidity.Max;

    private static void CheckRange((int Min, int Max) range, string name)
    {
        if (range.Min < 0 || range.Max > 100 || range.Min > range.Max)
        {
            throw new ArgumentOutOfRangeException(name, "A climate range must satisfy 0 <= min <= max <= 100.");
        }
    }

    [GeneratedRegex("^[a-z0-9_]+$")]
    private static partial Regex HabitatPattern();
}

public interface IBiomeCatalog
{
    /// <summary>Biomes ordered by id.</summary>
    IReadOnlyList<Biome> Biomes { get; }
}

public sealed class BiomeCatalog : IBiomeCatalog
{
    public BiomeCatalog(IEnumerable<Biome> biomes)
    {
        ArgumentNullException.ThrowIfNull(biomes);
        var list = biomes.OrderBy(b => b.Id, StringComparer.Ordinal).ToList();
        if (list.Count == 0)
        {
            throw new ArgumentException("A world needs at least one biome.", nameof(biomes));
        }

        for (var i = 1; i < list.Count; i++)
        {
            if (list[i].Id == list[i - 1].Id)
            {
                throw new ArgumentException($"Duplicate biome '{list[i].Id}'.", nameof(biomes));
            }
        }

        Biomes = list;
    }

    public IReadOnlyList<Biome> Biomes { get; }

    /// <summary>The first biome, by id, whose climate contains the sample. If none does, the first biome.</summary>
    public Biome Select(int temperature, int humidity)
    {
        foreach (var biome in Biomes)
        {
            if (biome.Contains(temperature, humidity))
            {
                return biome;
            }
        }

        return Biomes[0];
    }
}

/// <summary>Inclusive range of whole numbers, as <c>{ "min": 0, "max": 100 }</c>.</summary>
public sealed record IntervalDto
{
    public required int Min { get; init; }

    public required int Max { get; init; }
}

public sealed record ClimateDto
{
    /// <summary>Temperature range, 0 (coldest) to 100 (hottest).</summary>
    public required IntervalDto Temperature { get; init; }

    /// <summary>Humidity range, 0 (driest) to 100 (wettest).</summary>
    public required IntervalDto Humidity { get; init; }
}

public sealed record TerrainDto
{
    /// <summary>Average ground height in blocks.</summary>
    public required int BaseHeight { get; init; }

    /// <summary>How far hills rise above and fall below the base height, in blocks.</summary>
    public required int Amplitude { get; init; }

    /// <summary>Trees per thousand grass columns. Defaults to none.</summary>
    public int TreesPerThousand { get; init; }
}

/// <summary>
/// JSON shape of a biome. This type is the source of the generated JSON Schema,
/// so keep it in step with <see cref="BiomeJson"/>.
/// </summary>
public sealed record BiomeDto
{
    /// <summary>Content ID in the form <c>namespace:biome/name</c>.</summary>
    public required string Id { get; init; }

    /// <summary>Habitat tags such as <c>forest</c>. Animals and other content use them to decide where they may live.</summary>
    public required IReadOnlyList<string> Habitats { get; init; }

    public required ClimateDto Climate { get; init; }

    public required TerrainDto Terrain { get; init; }
}

public static class BiomeJson
{
    public static Biome Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        BiomeDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<BiomeDto>(json, DefinitionJson.Options);
        }
        catch (JsonException ex)
        {
            throw new BiomeException($"Invalid biome: {ex.Message}");
        }

        if (dto is null)
        {
            throw new BiomeException("A biome must be a JSON object.");
        }

        try
        {
            return new Biome(
                dto.Id,
                dto.Habitats,
                (dto.Climate.Temperature.Min, dto.Climate.Temperature.Max),
                (dto.Climate.Humidity.Min, dto.Climate.Humidity.Max),
                dto.Terrain.BaseHeight,
                dto.Terrain.Amplitude,
                dto.Terrain.TreesPerThousand);
        }
        catch (ArgumentException ex)
        {
            throw new BiomeException($"Biome '{dto.Id}': {ex.Message}");
        }
    }
}
