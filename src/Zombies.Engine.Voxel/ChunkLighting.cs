namespace Zombies.Engine.Voxel;

/// <summary>Sky light and block light for every cell of a chunk, packed as <c>sky &lt;&lt; 4 | block</c>.</summary>
public sealed class LightMap
{
    internal LightMap() => Data = new byte[ChunkConstants.Volume];

    internal byte[] Data { get; }

    public int Sky(int x, int y, int z) => Data[Chunk.Index(x, y, z)] >> 4;

    public int Block(int x, int y, int z) => Data[Chunk.Index(x, y, z)] & 15;
}

/// <summary>
/// Flood-fill light. Sky light falls straight down open columns at full strength and spreads sideways losing one level per cell;
/// block light spreads from emitting blocks the same way. Light entering from a neighbor chunk is approximated from that neighbor's
/// column heights, which is exact for open sky and cheap; emitters in neighbor chunks do not light this chunk.
/// </summary>
public static class ChunkLighting
{
    private const int NeighborSky = Blocks.MaxLight - 1;

    public static LightMap Compute(Chunk chunk, ChunkNeighbors neighbors)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        ArgumentNullException.ThrowIfNull(neighbors);

        var map = new LightMap();
        var queue = new IntQueue();
        SeedSky(chunk, neighbors, map.Data, queue);
        Spread(chunk, map.Data, queue, shift: 4);

        SeedBlockLight(chunk, map.Data, queue);
        Spread(chunk, map.Data, queue, shift: 0);
        return map;
    }

    private static void SeedSky(Chunk chunk, ChunkNeighbors neighbors, byte[] light, IntQueue queue)
    {
        var size = ChunkConstants.Size;
        for (var z = 0; z < size; z++)
        {
            for (var x = 0; x < size; x++)
            {
                var top = chunk.TopOpaque(x, z);
                for (var y = ChunkConstants.Height - 1; y > top; y--)
                {
                    light[Chunk.Index(x, y, z)] = Blocks.MaxLight << 4;
                }

                var reach = Math.Max(top, Math.Max(
                    Math.Max(HeightBeside(chunk, neighbors, x + 1, z), HeightBeside(chunk, neighbors, x - 1, z)),
                    Math.Max(HeightBeside(chunk, neighbors, x, z + 1), HeightBeside(chunk, neighbors, x, z - 1))));

                // Only lit cells at or below a neighbouring column's top can still spread to something darker.
                for (var y = top + 1; y <= reach; y++)
                {
                    queue.Enqueue(Chunk.Index(x, y, z));
                }

                SeedFromNeighborEdge(chunk, neighbors, light, queue, x, z, top);
            }
        }
    }

    /// <summary>Light enters a border cell from an open-sky column in the neighbor chunk, one level dimmer.</summary>
    private static void SeedFromNeighborEdge(Chunk chunk, ChunkNeighbors neighbors, byte[] light, IntQueue queue, int x, int z, int top)
    {
        var last = ChunkConstants.Size - 1;
        if (x != 0 && x != last && z != 0 && z != last)
        {
            return;
        }

        for (var y = 0; y <= top; y++)
        {
            if (Blocks.IsOpaque(chunk.Get(x, y, z)))
            {
                continue;
            }

            var open = (x == 0 && y > OutsideTop(neighbors.NegX, last, z))
                || (x == last && y > OutsideTop(neighbors.PosX, 0, z))
                || (z == 0 && y > OutsideTop(neighbors.NegZ, x, last))
                || (z == last && y > OutsideTop(neighbors.PosZ, x, 0));
            var index = Chunk.Index(x, y, z);
            if (open && (light[index] >> 4) < NeighborSky)
            {
                light[index] = (byte)((light[index] & 15) | (NeighborSky << 4));
                queue.Enqueue(index);
            }
        }
    }

    private static int OutsideTop(Chunk? neighbor, int x, int z) => neighbor is null ? -1 : neighbor.TopOpaque(x, z);

    private static int HeightBeside(Chunk chunk, ChunkNeighbors neighbors, int x, int z)
    {
        var size = ChunkConstants.Size;
        if (x < 0)
        {
            return OutsideTop(neighbors.NegX, size - 1, z);
        }

        if (x >= size)
        {
            return OutsideTop(neighbors.PosX, 0, z);
        }

        if (z < 0)
        {
            return OutsideTop(neighbors.NegZ, x, size - 1);
        }

        return z >= size ? OutsideTop(neighbors.PosZ, x, 0) : chunk.TopOpaque(x, z);
    }

    private static void SeedBlockLight(Chunk chunk, byte[] light, IntQueue queue)
    {
        var blocks = chunk.BlockData;
        for (var i = 0; i < blocks.Length; i++)
        {
            var emission = Blocks.Emission(blocks[i]);
            if (emission > 0)
            {
                light[i] = (byte)((light[i] & 0xF0) | emission);
                queue.Enqueue(i);
            }
        }
    }

    /// <summary>Breadth-first spread of one light channel: a cell passes its level minus one to every open neighbor that is darker.</summary>
    private static void Spread(Chunk chunk, byte[] light, IntQueue queue, int shift)
    {
        var size = ChunkConstants.Size;
        var layer = size * size;
        var blocks = chunk.RawBlocks;
        while (queue.TryDequeue(out var index))
        {
            var level = (light[index] >> shift) & 15;
            if (level <= 1)
            {
                continue;
            }

            var y = index / layer;
            var z = (index / size) % size;
            var x = index % size;
            var next = level - 1;

            if (x > 0)
            {
                Push(index - 1);
            }

            if (x < size - 1)
            {
                Push(index + 1);
            }

            if (z > 0)
            {
                Push(index - size);
            }

            if (z < size - 1)
            {
                Push(index + size);
            }

            if (y > 0)
            {
                Push(index - layer);
            }

            if (y < ChunkConstants.Height - 1)
            {
                Push(index + layer);
            }

            void Push(int target)
            {
                if (Blocks.IsOpaque(blocks[target]) || ((light[target] >> shift) & 15) >= next)
                {
                    return;
                }

                light[target] = (byte)((light[target] & ~(15 << shift)) | (next << shift));
                queue.Enqueue(target);
            }
        }
    }
}

/// <summary>Minimal growable FIFO of ints, avoiding the per-item cost of <see cref="Queue{T}"/>.</summary>
internal sealed class IntQueue
{
    private int[] _items = new int[4096];
    private int _head;
    private int _tail;

    public void Enqueue(int value)
    {
        if (_tail == _items.Length)
        {
            if (_head > 0)
            {
                Array.Copy(_items, _head, _items, 0, _tail - _head);
                _tail -= _head;
                _head = 0;
            }
            else
            {
                Array.Resize(ref _items, _items.Length * 2);
            }
        }

        _items[_tail++] = value;
    }

    public bool TryDequeue(out int value)
    {
        if (_head == _tail)
        {
            _head = 0;
            _tail = 0;
            value = 0;
            return false;
        }

        value = _items[_head++];
        return true;
    }
}
