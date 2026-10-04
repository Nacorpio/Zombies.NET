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

    /// <summary>All stage versions in one number, stored with saves so a changed generator is noticed.</summary>
    public const int GeneratorVersion = (TerrainStageVersion << 16) | (SurfaceStageVersion << 8) | VegetationStageVersion;

    private const int Margin = 2;
    private const int Window = ChunkConstants.Size + (2 * Margin);

    private const int SaltTemperature = 11;
    private const int SaltHumidity = 12;
    private const int SaltTree = 21;
    private const int SaltTreeHeight = 22;

    private readonly ulong _seed;
    private readonly BiomeCatalog _biomes;

    public WorldGenerator(ulong seed, BiomeCatalog biomes)
    {
        ArgumentNullException.ThrowIfNull(biomes);
        _seed = seed;
        _biomes = biomes;
    }

    public ulong Seed => _seed;

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
