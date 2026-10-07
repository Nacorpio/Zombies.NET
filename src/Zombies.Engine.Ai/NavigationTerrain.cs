using System.Numerics;
using Zombies.Engine.Voxel;

namespace Zombies.Engine.Ai;

/// <summary>The blocks a creature cannot walk or see through. Navigation and sight read the world only through this.</summary>
public interface INavigationTerrain
{
    bool IsSolid(int x, int y, int z);
}

/// <summary>
/// One block a creature can stand in: <see cref="Y"/> is the block its feet are in, so the block below it is the ground.
/// </summary>
public readonly record struct NavCell(int X, int Y, int Z)
{
    private const int HorizontalBits = 24;
    private const int VerticalBits = 12;
    private const long HorizontalMask = (1L << HorizontalBits) - 1;
    private const long VerticalMask = (1L << VerticalBits) - 1;

    /// <summary>A number that names this cell, for dictionary keys without hashing a struct.</summary>
    public long Key => ((X & HorizontalMask) << (HorizontalBits + VerticalBits)) | ((Z & HorizontalMask) << VerticalBits) | (Y & VerticalMask);

    /// <summary>Where a creature standing in this cell has its feet: the middle of the block, on its floor.</summary>
    public Vector3 Floor => new(X + 0.5f, Y, Z + 0.5f);

    public static NavCell FromKey(long key) => new(
        (int)((key >> (HorizontalBits + VerticalBits)) << (64 - HorizontalBits) >> (64 - HorizontalBits)),
        (int)((key & VerticalMask) << (64 - VerticalBits) >> (64 - VerticalBits)),
        (int)(((key >> VerticalBits) & HorizontalMask) << (64 - HorizontalBits) >> (64 - HorizontalBits)));

    /// <summary>The cell holding a point, its feet a hair above the floor so standing exactly on a block counts as above it.</summary>
    public static NavCell Containing(Vector3 position) =>
        new((int)MathF.Floor(position.X), (int)MathF.Floor(position.Y + 0.01f), (int)MathF.Floor(position.Z));
}

/// <summary>
/// Voxel terrain as navigation sees it: an opaque block is solid. A chunk that is not loaded is solid too, so nothing walks or
/// plans into a part of the world the Server does not have.
/// </summary>
public sealed class ChunkTerrain : INavigationTerrain
{
    private readonly Dictionary<ChunkCoord, Chunk> _chunks = [];
    private Chunk? _last;
    private ChunkCoord _lastCoord;
    private bool _hasLast;

    public int ChunkCount => _chunks.Count;

    public void Add(Chunk chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        _chunks[chunk.Coord] = chunk;
        _hasLast = false;
    }

    public bool Remove(ChunkCoord coord)
    {
        _hasLast = false;
        return _chunks.Remove(coord);
    }

    /// <summary>Generates and adds every chunk within <paramref name="radius"/> chunks of <paramref name="center"/>.</summary>
    public void Generate(WorldGenerator generator, ChunkCoord center, int radius)
    {
        ArgumentNullException.ThrowIfNull(generator);
        for (var z = center.Z - radius; z <= center.Z + radius; z++)
        {
            for (var x = center.X - radius; x <= center.X + radius; x++)
            {
                Add(generator.Generate(new ChunkCoord(x, z)));
            }
        }
    }

    public bool IsSolid(int x, int y, int z)
    {
        if (y < 0)
        {
            return true;
        }

        if (y >= ChunkConstants.Height)
        {
            return false;
        }

        var coord = new ChunkCoord(x >> 4, z >> 4);
        if (!_hasLast || _lastCoord != coord)
        {
            // Remember misses too: an unloaded chunk is asked about block after block when a search reaches the edge of the world.
            _last = _chunks.GetValueOrDefault(coord);
            _lastCoord = coord;
            _hasLast = true;
        }

        var chunk = _last;
        if (chunk is null)
        {
            return true;
        }

        return Blocks.IsOpaque(chunk.Get(x & (ChunkConstants.Size - 1), y, z & (ChunkConstants.Size - 1)));
    }

    /// <summary>The Y a creature stands at on top of the highest opaque block of a column, or -1 when the column is not loaded.</summary>
    public int SurfaceY(int x, int z) =>
        _chunks.TryGetValue(new ChunkCoord(x >> 4, z >> 4), out var chunk) ? chunk.TopOpaque(x & (ChunkConstants.Size - 1), z & (ChunkConstants.Size - 1)) + 1 : -1;
}
