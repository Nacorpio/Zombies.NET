using System.Collections.Concurrent;
using Zombies.Domain.World;

namespace Zombies.Engine.Voxel;

/// <summary>
/// Builds chunks from a world seed and the biome catalog. A chunk depends only on its own coordinates, never on which
/// chunks were built before it, and all arithmetic is integer, so any machine rebuilds the same blocks.
/// Each stage carries a version; change a stage's output and bump its version.
/// </summary>
public sealed class WorldGenerator
{
    public const int TerrainStageVersion = 1;
    public const int SurfaceStageVersion = 1;
    public const int VegetationStageVersion = 1;
    public const int StructureStageVersion = 1;

    /// <summary>All stage versions in one number, stored with saves so a changed generator is noticed.</summary>
    public const int GeneratorVersion = (TerrainStageVersion << 24) | (SurfaceStageVersion << 16) | (VegetationStageVersion << 8) | StructureStageVersion;

    private const int Margin = 2;
    private const int Window = ChunkConstants.Size + (2 * Margin);

    private const int SaltTemperature = 11;
    private const int SaltHumidity = 12;
    private const int SaltTree = 21;
    private const int SaltTreeHeight = 22;

    private readonly ulong _seed;
    private readonly BiomeCatalog _biomes;
    private readonly SettlementContent? _settlements;
    private readonly int _zombieDensityPercent;
    private readonly RegionGrid _regions;
    private readonly Dictionary<string, ushort[]> _blueprints = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<RegionCoord, SettlementPlan?> _plans = new();

    /// <param name="settlements">The Structures and Settlement types to stamp at region sites. Null builds a world with none.</param>
    /// <param name="zombieDensityPercent">Scales the zombies each Settlement spawns with: 100 as declared, 0 for none.</param>
    public WorldGenerator(ulong seed, BiomeCatalog biomes, SettlementContent? settlements = null, int zombieDensityPercent = 100)
    {
        ArgumentNullException.ThrowIfNull(biomes);
        _seed = seed;
        _zombieDensityPercent = zombieDensityPercent;
        _biomes = biomes;
        _settlements = settlements;
        _regions = new RegionGrid(seed);
        if (settlements is not null)
        {
            foreach (var structure in settlements.Structures)
            {
                _blueprints[structure.Id] = Blueprint(structure);
            }
        }
    }

    public ulong Seed => _seed;

    /// <summary>The Settlement planned for a Region, or null when it has none. Pure in the seed and the Region, and cached.</summary>
    public SettlementPlan? SettlementIn(RegionCoord region)
    {
        if (_settlements is not { } content)
        {
            return null;
        }

        return _plans.GetOrAdd(region, r => SettlementPlanner.Plan(content, _regions, r, _zombieDensityPercent));
    }

    /// <summary>Height of the floor of a placed Structure: the ground level at the middle of its footprint.</summary>
    public int FloorY(PlacedStructure placed)
    {
        ArgumentNullException.ThrowIfNull(placed);
        return ColumnAt(placed.CenterX, placed.CenterZ).Height;
    }

    /// <summary>Every Container of the Settlement in a Region, in world coordinates. Empty when the Region has no Settlement.</summary>
    public IReadOnlyList<WorldContainer> ContainersIn(RegionCoord region) =>
        SettlementIn(region)?.ContainersAt(FloorY) ?? [];

    /// <summary>The Containers inside a chunk's columns.</summary>
    public IReadOnlyList<WorldContainer> ContainersIn(ChunkCoord coord) =>
        [.. ContainersIn(RegionGrid.RegionOf(coord.WorldX, coord.WorldZ))
            .Where(c => c.X >= coord.WorldX && c.X < coord.WorldX + ChunkConstants.Size && c.Z >= coord.WorldZ && c.Z < coord.WorldZ + ChunkConstants.Size)];

    public Chunk Generate(ChunkCoord coord)
    {
        var chunk = new Chunk(coord);
        var columns = new Column[Window * Window];
        for (var dz = 0; dz < Window; dz++)
        {
            for (var dx = 0; dx < Window; dx++)
            {
                columns[(dz * Window) + dx] = ColumnAt(coord.WorldX + dx - Margin, coord.WorldZ + dz - Margin);
            }
        }

        ShapeTerrain(chunk, columns);
        GrowTrees(chunk, columns);
        StampSettlement(chunk);
        chunk.RecomputeHeights();
        return chunk;
    }

    private readonly record struct Column(Biome Biome, int Height);

    private Column ColumnAt(int worldX, int worldZ)
    {
        var temperature = (IntNoise.Fractal2D(_seed ^ SaltTemperature, worldX, worldZ, 8, 2) * 101) >> 16;
        var humidity = (IntNoise.Fractal2D(_seed ^ SaltHumidity, worldX, worldZ, 8, 2) * 101) >> 16;
        var biome = _biomes.Select(temperature, humidity);

        var noise = IntNoise.Fractal2D(_seed, worldX, worldZ, 6, 4);
        var height = biome.BaseHeight + (((noise - 32768) * biome.Amplitude) >> 15);
        return new Column(biome, Math.Clamp(height, 4, ChunkConstants.Height - 12));
    }

    /// <summary>Terrain and surface stages: bedrock, stone, a few layers of dirt, and grass on top.</summary>
    private static void ShapeTerrain(Chunk chunk, Column[] columns)
    {
        for (var z = 0; z < ChunkConstants.Size; z++)
        {
            for (var x = 0; x < ChunkConstants.Size; x++)
            {
                var height = columns[((z + Margin) * Window) + x + Margin].Height;
                chunk.Set(x, 0, z, Blocks.Bedrock);
                for (var y = 1; y <= height; y++)
                {
                    chunk.Set(x, y, z, y == height ? Blocks.Grass : y >= height - 3 ? Blocks.Dirt : Blocks.Stone);
                }
            }
        }
    }

    /// <summary>
    /// Vegetation stage. Every column in a two-block margin around the chunk is checked, because a tree rooted just
    /// outside still drops leaves inside. A log always wins over leaves, and leaves only fill air, so the result
    /// does not depend on the order trees are placed in.
    /// </summary>
    private void GrowTrees(Chunk chunk, Column[] columns)
    {
        for (var dz = 0; dz < Window; dz++)
        {
            for (var dx = 0; dx < Window; dx++)
            {
                var column = columns[(dz * Window) + dx];
                var worldX = chunk.Coord.WorldX + dx - Margin;
                var worldZ = chunk.Coord.WorldZ + dz - Margin;
                if (column.Biome.TreesPerThousand == 0
                    || (int)(WorldHash.Mix(_seed, worldX, worldZ, SaltTree) % 1000) >= column.Biome.TreesPerThousand)
                {
                    continue;
                }

                var trunk = 4 + (int)(WorldHash.Mix(_seed, worldX, worldZ, SaltTreeHeight) % 3);
                PlaceTree(chunk, dx - Margin, dz - Margin, column.Height, trunk);
            }
        }
    }

    /// <summary>The block ids of a Structure, laid out as its cells are, so stamping needs no name lookups.</summary>
    private static ushort[] Blueprint(Structure structure)
    {
        var blocks = new ushort[structure.Width * structure.Height * structure.Depth];
        for (var y = 0; y < structure.Height; y++)
        {
            for (var z = 0; z < structure.Depth; z++)
            {
                for (var x = 0; x < structure.Width; x++)
                {
                    var name = structure.BlockAt(x, y, z);
                    if (!Blocks.TryFromName(name, out var id))
                    {
                        throw new ArgumentException($"Structure '{structure.Id}' uses block '{name}', which does not exist.", nameof(structure));
                    }

                    blocks[(((y * structure.Depth) + z) * structure.Width) + x] = id;
                }
            }
        }

        return blocks;
    }

    /// <summary>
    /// Structure stage. Runs last, so a Structure always wins over trees. Each Structure stands on the ground level at the middle
    /// of its footprint: the ground in its yard is levelled to that height, everything above it is cleared, and then the
    /// prefab and its Containers are stamped. Only world coordinates and terrain columns are read, so the result does not
    /// depend on which chunks were built before.
    /// </summary>
    private void StampSettlement(Chunk chunk)
    {
        var region = RegionGrid.RegionOf(chunk.Coord.WorldX, chunk.Coord.WorldZ);
        if (SettlementIn(region) is not { } plan)
        {
            return;
        }

        var chunkMinX = chunk.Coord.WorldX;
        var chunkMinZ = chunk.Coord.WorldZ;
        var touched = false;
        foreach (var placed in plan.Structures)
        {
            if (!placed.YardTouches(chunkMinX, chunkMinZ, chunkMinX + ChunkConstants.Size - 1, chunkMinZ + ChunkConstants.Size - 1))
            {
                continue;
            }

            touched = true;

            var floor = FloorY(placed);
            var blueprint = _blueprints[placed.Structure.Id];
            var yard = SettlementContent.YardMargin;
            for (var z = Math.Max(chunkMinZ, placed.Z - yard); z <= Math.Min(chunkMinZ + ChunkConstants.Size - 1, placed.MaxZ + yard); z++)
            {
                for (var x = Math.Max(chunkMinX, placed.X - yard); x <= Math.Min(chunkMinX + ChunkConstants.Size - 1, placed.MaxX + yard); x++)
                {
                    LevelColumn(chunk, x - chunkMinX, z - chunkMinZ, ColumnAt(x, z).Height, floor);
                    if (x < placed.X || x > placed.MaxX || z < placed.Z || z > placed.MaxZ)
                    {
                        continue;
                    }

                    for (var y = 0; y < placed.Structure.Height && floor + y < ChunkConstants.Height; y++)
                    {
                        chunk.Set(x - chunkMinX, floor + y, z - chunkMinZ, blueprint[(((y * placed.Structure.Depth) + (z - placed.Z)) * placed.Structure.Width) + (x - placed.X)]);
                    }
                }
            }
        }

        if (!touched)
        {
            return;
        }

        foreach (var container in ContainersIn(region))
        {
            if (container.X >= chunkMinX && container.X < chunkMinX + ChunkConstants.Size
                && container.Z >= chunkMinZ && container.Z < chunkMinZ + ChunkConstants.Size
                && container.Y < ChunkConstants.Height)
            {
                chunk.Set(container.X - chunkMinX, container.Y, container.Z - chunkMinZ, Blocks.Crate);
            }
        }
    }

    /// <summary>Makes a column's ground end at <paramref name="floor"/> with grass on top and nothing above it.</summary>
    private static void LevelColumn(Chunk chunk, int localX, int localZ, int groundY, int floor)
    {
        for (var y = Math.Min(groundY, floor); y < floor; y++)
        {
            chunk.Set(localX, y, localZ, Blocks.Dirt);
        }

        chunk.Set(localX, floor, localZ, Blocks.Grass);
        for (var y = floor + 1; y < ChunkConstants.Height; y++)
        {
            chunk.Set(localX, y, localZ, Blocks.Air);
        }
    }

    private static void PlaceTree(Chunk chunk, int localX, int localZ, int groundY, int trunkHeight)
    {
        var top = groundY + trunkHeight;
        if (top + 1 >= ChunkConstants.Height)
        {
            return;
        }

        for (var y = top - 2; y <= top + 1; y++)
        {
            var radius = y >= top ? 1 : 2;
            for (var ox = -radius; ox <= radius; ox++)
            {
                for (var oz = -radius; oz <= radius; oz++)
                {
                    var corner = Math.Abs(ox) == 2 && Math.Abs(oz) == 2;
                    if (!corner)
                    {
                        PutLeaf(chunk, localX + ox, y, localZ + oz);
                    }
                }
            }
        }

        for (var y = groundY + 1; y <= top; y++)
        {
            if (InChunk(localX, localZ))
            {
                chunk.Set(localX, y, localZ, Blocks.Log);
            }
        }
    }

    private static void PutLeaf(Chunk chunk, int x, int y, int z)
    {
        if (InChunk(x, z) && chunk.Get(x, y, z) == Blocks.Air)
        {
            chunk.Set(x, y, z, Blocks.Leaves);
        }
    }

    private static bool InChunk(int x, int z) => (uint)x < ChunkConstants.Size && (uint)z < ChunkConstants.Size;
}
