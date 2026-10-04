namespace Zombies.Engine.Voxel;

public static class ChunkConstants
{
    public const int Size = 16;
    public const int Height = 128;
    public const int SectionHeight = 16;
    public const int SectionCount = Height / SectionHeight;
    public const int Volume = Size * Size * Height;
}

public readonly record struct ChunkCoord(int X, int Z)
{
    public int WorldX => X * ChunkConstants.Size;

    public int WorldZ => Z * ChunkConstants.Size;

    public override string ToString() => $"chunk({X},{Z})";
}

/// <summary>A 16 by 16 column of blocks, 128 high, split into eight 16-high sections for meshing.</summary>
public sealed class Chunk
{
    private readonly ushort[] _blocks = new ushort[ChunkConstants.Volume];
    private readonly short[] _top = new short[ChunkConstants.Size * ChunkConstants.Size];

    public Chunk(ChunkCoord coord)
    {
        Coord = coord;
        Array.Fill(_top, (short)-1);
    }

    public ChunkCoord Coord { get; }

    public static int Index(int x, int y, int z) => (((y * ChunkConstants.Size) + z) * ChunkConstants.Size) + x;

    public ushort Get(int x, int y, int z) => _blocks[Index(x, y, z)];

    public void Set(int x, int y, int z, ushort block) => _blocks[Index(x, y, z)] = block;

    public ReadOnlySpan<ushort> BlockData => _blocks;

    internal ushort[] RawBlocks => _blocks;

    /// <summary>Y of the highest opaque block in a column, or -1 when the column has none. Call <see cref="RecomputeHeights"/> after editing.</summary>
    public int TopOpaque(int x, int z) => _top[(z * ChunkConstants.Size) + x];

    public void RecomputeHeights()
    {
        for (var z = 0; z < ChunkConstants.Size; z++)
        {
            for (var x = 0; x < ChunkConstants.Size; x++)
            {
                var y = ChunkConstants.Height - 1;
                while (y >= 0 && !Blocks.IsOpaque(_blocks[Index(x, y, z)]))
                {
                    y--;
                }

                _top[(z * ChunkConstants.Size) + x] = (short)y;
            }
        }
    }

    public bool IsSectionEmpty(int section)
    {
        var start = section * ChunkConstants.SectionHeight * ChunkConstants.Size * ChunkConstants.Size;
        var length = ChunkConstants.SectionHeight * ChunkConstants.Size * ChunkConstants.Size;
        return !_blocks.AsSpan(start, length).ContainsAnyExcept(Blocks.Air);
    }

    /// <summary>FNV-1a 64 over the coordinates and every block as little-endian bytes, so the value is the same on every platform.</summary>
    public ulong Hash()
    {
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        var hash = offset;

        hash = Mix(hash, (uint)Coord.X, 4);
        hash = Mix(hash, (uint)Coord.Z, 4);
        foreach (var block in _blocks)
        {
            hash = Mix(hash, block, 2);
        }

        return hash;

        static ulong Mix(ulong h, uint value, int bytes)
        {
            for (var i = 0; i < bytes; i++)
            {
                h ^= (byte)(value >> (8 * i));
                h *= prime;
            }

            return h;
        }
    }
}

/// <summary>The four chunks that share an edge with a chunk. A missing neighbor is treated as open air.</summary>
public sealed record ChunkNeighbors(Chunk? PosX, Chunk? NegX, Chunk? PosZ, Chunk? NegZ)
{
    public static readonly ChunkNeighbors None = new(null, null, null, null);
}
