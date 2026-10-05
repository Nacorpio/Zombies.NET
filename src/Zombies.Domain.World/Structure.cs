using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Zombies.Domain.Items;

namespace Zombies.Domain.World;

public sealed class StructureException(string message) : Exception(message);

/// <summary>A cell of a Structure, counted from its west, bottom, north corner.</summary>
public readonly record struct StructurePos(int X, int Y, int Z);

/// <summary>
/// A room or labelled part of a Structure. Its Area type decides what its Containers hold. When <see cref="AreaType"/> is
/// null the Settlement type picks one, so the same Structure can be a kitchen in one Settlement and a bedroom in another.
/// </summary>
public sealed record StructureArea(string Name, string? AreaType, StructurePos Min, StructurePos Max)
{
    public bool Contains(StructurePos pos) =>
        pos.X >= Min.X && pos.X <= Max.X && pos.Y >= Min.Y && pos.Y <= Max.Y && pos.Z >= Min.Z && pos.Z <= Max.Z;

    public bool Overlaps(StructureArea other) =>
        Min.X <= other.Max.X && Max.X >= other.Min.X
        && Min.Y <= other.Max.Y && Max.Y >= other.Min.Y
        && Min.Z <= other.Max.Z && Max.Z >= other.Min.Z;
}

/// <summary>A place in a Structure that holds loot. Its loot comes from the Area it is in, never from the Structure.</summary>
public sealed record StructureContainer(string ContainerKind, StructurePos Position, string Area);

/// <summary>
/// A prefab voxel construction stamped into the world during generation: a grid of block names, the Areas it is divided
/// into, and the Containers in them. The grid is described by block names, not ids, so this context does not depend on the voxel engine.
/// </summary>
[SuppressMessage("Naming", "CA1716", Justification = "Structure is the domain term in GLOSSARY.md; the project is C# only.")]
public sealed class Structure
{
    public const int MaxWidth = 32;
    public const int MaxDepth = 32;
    public const int MaxHeight = 16;

    /// <summary>The block name that means empty space.</summary>
    public const string AirBlock = "air";

    private readonly string[] _cells;

    public Structure(
        string id,
        IReadOnlyDictionary<char, string> palette,
        IReadOnlyList<IReadOnlyList<string>> layers,
        IEnumerable<StructureArea> areas,
        IEnumerable<StructureContainer> containers)
    {
        ArgumentNullException.ThrowIfNull(palette);
        ArgumentNullException.ThrowIfNull(layers);
        ArgumentNullException.ThrowIfNull(areas);
        ArgumentNullException.ThrowIfNull(containers);
        if (!ItemId.TryParse(id, out _))
        {
            throw new ArgumentException($"'{id}' is not a valid Content ID.", nameof(id));
        }

        foreach (var (symbol, block) in palette)
        {
            if (!StatName.TryParse(block, out _))
            {
                throw new ArgumentException($"Structure '{id}': palette symbol '{symbol}' names block '{block}', which is not a valid block name.", nameof(palette));
            }
        }

        Id = id;
        Height = layers.Count;
        if (Height is < 1 or > MaxHeight)
        {
            throw new ArgumentException($"Structure '{id}' must have 1 to {MaxHeight} layers.", nameof(layers));
        }

        Depth = layers[0].Count;
        Width = Depth == 0 ? 0 : layers[0][0].Length;
        if (Depth is < 1 or > MaxDepth || Width is < 1 or > MaxWidth)
        {
            throw new ArgumentException($"Structure '{id}' must be 1 to {MaxWidth} wide and 1 to {MaxDepth} deep.", nameof(layers));
        }

        _cells = new string[Width * Height * Depth];
        for (var y = 0; y < Height; y++)
        {
            if (layers[y].Count != Depth)
            {
                throw new ArgumentException($"Structure '{id}': layer {y} has {layers[y].Count} rows, expected {Depth}.", nameof(layers));
            }

            for (var z = 0; z < Depth; z++)
            {
                var row = layers[y][z];
                if (row.Length != Width)
                {
                    throw new ArgumentException($"Structure '{id}': layer {y} row {z} is {row.Length} wide, expected {Width}.", nameof(layers));
                }

                for (var x = 0; x < Width; x++)
                {
                    if (!palette.TryGetValue(row[x], out var block))
                    {
                        throw new ArgumentException($"Structure '{id}': layer {y} row {z} uses '{row[x]}', which is not in the palette.", nameof(layers));
                    }

                    if (y == 0 && block == AirBlock)
                    {
                        throw new ArgumentException($"Structure '{id}': the bottom layer is the floor and cannot contain air (layer 0 row {z}).", nameof(layers));
                    }

                    _cells[Index(x, y, z)] = block;
                }
            }
        }

        var areaList = areas.ToList();
        for (var i = 0; i < areaList.Count; i++)
        {
            var area = areaList[i];
            if (!StatName.TryParse(area.Name, out _))
            {
                throw new ArgumentException($"Structure '{id}': '{area.Name}' is not a valid Area name.", nameof(areas));
            }

            if (area.AreaType is not null && !ItemId.TryParse(area.AreaType, out _))
            {
                throw new ArgumentException($"Structure '{id}': Area '{area.Name}' names '{area.AreaType}', which is not a valid Content ID.", nameof(areas));
            }

            if (!InBounds(area.Min) || !InBounds(area.Max) || area.Min.X > area.Max.X || area.Min.Y > area.Max.Y || area.Min.Z > area.Max.Z)
            {
                throw new ArgumentException($"Structure '{id}': Area '{area.Name}' is empty or reaches outside the Structure.", nameof(areas));
            }

            for (var j = 0; j < i; j++)
            {
                if (areaList[j].Name == area.Name)
                {
                    throw new ArgumentException($"Structure '{id}': two Areas are named '{area.Name}'.", nameof(areas));
                }

                if (areaList[j].Overlaps(area))
                {
                    throw new ArgumentException($"Structure '{id}': Areas '{areaList[j].Name}' and '{area.Name}' overlap.", nameof(areas));
                }
            }
        }

        Areas = areaList;

        var containerList = containers.ToList();
        for (var i = 0; i < containerList.Count; i++)
        {
            var container = containerList[i];
            if (!StatName.TryParse(container.ContainerKind, out _))
            {
                throw new ArgumentException($"Structure '{id}': '{container.ContainerKind}' is not a valid container kind.", nameof(containers));
            }

            var area = areaList.FirstOrDefault(a => a.Name == container.Area)
                ?? throw new ArgumentException($"Structure '{id}': a {container.ContainerKind} is in Area '{container.Area}', which does not exist.", nameof(containers));
            var pos = container.Position;
            if (!InBounds(pos) || !area.Contains(pos))
            {
                throw new ArgumentException($"Structure '{id}': a {container.ContainerKind} at ({pos.X},{pos.Y},{pos.Z}) is not inside Area '{area.Name}'.", nameof(containers));
            }

            if (pos.Y < 1 || BlockAt(pos.X, pos.Y, pos.Z) != AirBlock)
            {
                throw new ArgumentException($"Structure '{id}': a {container.ContainerKind} at ({pos.X},{pos.Y},{pos.Z}) must stand on the floor in an empty cell.", nameof(containers));
            }

            if (containerList.Take(i).Any(c => c.Position == pos))
            {
                throw new ArgumentException($"Structure '{id}': two Containers share the cell ({pos.X},{pos.Y},{pos.Z}).", nameof(containers));
            }
        }

        Containers = containerList;
    }

    public string Id { get; }

    public int Width { get; }

    public int Height { get; }

    public int Depth { get; }

    public IReadOnlyList<StructureArea> Areas { get; }

    public IReadOnlyList<StructureContainer> Containers { get; }

    /// <summary>The block name at a cell. Containers are not part of the grid; the stamper places them.</summary>
    public string BlockAt(int x, int y, int z) => _cells[Index(x, y, z)];

    /// <summary>The Area a cell is in, or null when it is in none, such as a wall or the open air around the Structure.</summary>
    public StructureArea? AreaAt(StructurePos pos) => Areas.FirstOrDefault(a => a.Contains(pos));

    private int Index(int x, int y, int z) => (((y * Depth) + z) * Width) + x;

    private bool InBounds(StructurePos pos) =>
        pos.X >= 0 && pos.X < Width && pos.Y >= 0 && pos.Y < Height && pos.Z >= 0 && pos.Z < Depth;
}

public sealed record PaletteEntryDto
{
    /// <summary>The single character that stands for the block in the layers.</summary>
    public required string Symbol { get; init; }

    /// <summary>Name of the block, such as <c>planks</c> or <c>air</c>.</summary>
    public required string Block { get; init; }
}

public sealed record StructurePosDto
{
    public required int X { get; init; }

    public required int Y { get; init; }

    public required int Z { get; init; }
}

public sealed record StructureAreaDto
{
    /// <summary>Name of the Area, unique within the Structure.</summary>
    public required string Name { get; init; }

    /// <summary>Content ID of the Area type. Leave it out to let the Settlement type choose one.</summary>
    public string? AreaType { get; init; }

    /// <summary>Lowest corner of the Area, inclusive.</summary>
    public required StructurePosDto Min { get; init; }

    /// <summary>Highest corner of the Area, inclusive.</summary>
    public required StructurePosDto Max { get; init; }
}

public sealed record StructureContainerDto
{
    /// <summary>Name of the container kind, such as <c>cabinet</c>. The Area type decides what that kind holds.</summary>
    public required string ContainerKind { get; init; }

    public required StructurePosDto At { get; init; }

    /// <summary>Name of the Area the Container is in.</summary>
    public required string Area { get; init; }
}

/// <summary>
/// JSON shape of a Structure. This type is the source of the generated JSON Schema,
/// so keep it in step with <see cref="StructureJson"/>.
/// </summary>
public sealed record StructureDto
{
    /// <summary>Content ID in the form <c>namespace:structure/name</c>.</summary>
    public required string Id { get; init; }

    public required IReadOnlyList<PaletteEntryDto> Palette { get; init; }

    /// <summary>
    /// Layers from the floor up. Each layer is a list of rows from north to south, and each row is a string of palette
    /// symbols from west to east. The bottom layer is the floor and cannot hold air.
    /// </summary>
    public required IReadOnlyList<IReadOnlyList<string>> Layers { get; init; }

    public required IReadOnlyList<StructureAreaDto> Areas { get; init; }

    public required IReadOnlyList<StructureContainerDto> Containers { get; init; }
}

public static class StructureJson
{
    public static Structure Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        StructureDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<StructureDto>(json, DefinitionJson.Options);
        }
        catch (JsonException ex)
        {
            throw new StructureException($"Invalid structure: {ex.Message}");
        }

        if (dto is null)
        {
            throw new StructureException("A structure must be a JSON object.");
        }

        var palette = new Dictionary<char, string>();
        foreach (var entry in dto.Palette)
        {
            if (entry.Symbol.Length != 1)
            {
                throw new StructureException($"Structure '{dto.Id}': palette symbol '{entry.Symbol}' must be exactly one character.");
            }

            if (!palette.TryAdd(entry.Symbol[0], entry.Block))
            {
                throw new StructureException($"Structure '{dto.Id}': palette symbol '{entry.Symbol}' is defined twice.");
            }
        }

        try
        {
            return new Structure(
                dto.Id,
                palette,
                dto.Layers,
                dto.Areas.Select(a => new StructureArea(a.Name, a.AreaType, Pos(a.Min), Pos(a.Max))),
                dto.Containers.Select(c => new StructureContainer(c.ContainerKind, Pos(c.At), c.Area)));
        }
        catch (ArgumentException ex)
        {
            throw new StructureException(ex.Message);
        }
    }

    private static StructurePos Pos(StructurePosDto dto) => new(dto.X, dto.Y, dto.Z);
}
