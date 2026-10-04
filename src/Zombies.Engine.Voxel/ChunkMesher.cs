using System.Runtime.InteropServices;

namespace Zombies.Engine.Voxel;

/// <summary>
/// One mesh vertex, packed small for the GPU. Positions are in blocks from the chunk corner; <see cref="U"/> and <see cref="V"/>
/// count blocks across a merged quad so a tile repeats; <see cref="Ao"/> is ambient occlusion from 0 (darkest) to 3.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct ChunkVertex(byte X, byte Y, byte Z, byte Face, ushort Tile, byte U, byte V, byte Ao, byte SkyLight, byte BlockLight);

/// <summary>The mesh of one 16-high section. Indices use the <c>uint</c> type so a dense section can never overflow them.</summary>
public sealed class SectionMesh
{
    internal SectionMesh(ChunkVertex[] vertices, uint[] indices)
    {
        Vertices = vertices;
        Indices = indices;
    }

    public static SectionMesh Empty { get; } = new([], []);

    public ReadOnlyMemory<ChunkVertex> Vertices { get; }

    public ReadOnlyMemory<uint> Indices { get; }

    public bool IsEmpty => Indices.IsEmpty;

    public int QuadCount => Indices.Length / 6;
}

public sealed class ChunkMeshSet
{
    internal ChunkMeshSet(ChunkCoord coord, SectionMesh[] sections)
    {
        Coord = coord;
        Sections = sections;
    }

    public ChunkCoord Coord { get; }

    public IReadOnlyList<SectionMesh> Sections { get; }

    public int VertexCount => Sections.Sum(s => s.Vertices.Length);

    public int QuadCount => Sections.Sum(s => s.QuadCount);
}

/// <summary>
/// Greedy mesher. For each of the six face directions it walks a section slice by slice, builds a mask of visible faces,
/// and merges runs of identical faces into larger quads. "Identical" includes the ambient occlusion and light at all four corners,
/// so a merged quad shades exactly like the faces it replaced. Faces next to another opaque block are never emitted.
/// Safe to call from many threads at once: it only reads its inputs.
/// </summary>
public static class ChunkMesher
{
    private const int Size = ChunkConstants.Size;
    private const ulong Present = 1UL << 63;

    // The opacity grid is padded by two cells so a face on the section edge can still look at its neighbors and their neighbors.
    private const int Pad = 2;
    private const int Dim = Size + (2 * Pad);
    private const int StrideX = 1;
    private const int StrideY = Dim * Dim;
    private const int StrideZ = Dim;

    public static ChunkMeshSet Build(Chunk chunk, LightMap light, ChunkNeighbors neighbors)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        ArgumentNullException.ThrowIfNull(light);
        ArgumentNullException.ThrowIfNull(neighbors);

        var view = new View(chunk, light, neighbors);
        var vertices = new List<ChunkVertex>(4096);
        var indices = new List<uint>(6144);
        var mask = new ulong[Size * Size];
        var solid = new byte[Dim * Dim * Dim];
        var sections = new SectionMesh[ChunkConstants.SectionCount];

        for (var section = 0; section < sections.Length; section++)
        {
            if (chunk.IsSectionEmpty(section))
            {
                sections[section] = SectionMesh.Empty;
                continue;
            }

            vertices.Clear();
            indices.Clear();
            MeshSection(view, section * ChunkConstants.SectionHeight, solid, mask, vertices, indices);
            sections[section] = indices.Count == 0 ? SectionMesh.Empty : new SectionMesh([.. vertices], [.. indices]);
        }

        return new ChunkMeshSet(chunk.Coord, sections);
    }

    private static int P(int x, int y, int z) => (((y + Pad) * Dim + (z + Pad)) * Dim) + x + Pad;

    private static int StrideOf(int axis) => axis == 0 ? StrideX : axis == 1 ? StrideY : StrideZ;

    /// <summary>Fills a padded grid of 0 or 1 (opaque) around one section, so the hot loops are plain array reads.</summary>
    private static void FillSolid(View view, int originY, byte[] solid)
    {
        var raw = view.Chunk.RawBlocks;
        for (var ly = -Pad; ly < Size + Pad; ly++)
        {
            var wy = originY + ly;
            for (var lz = -Pad; lz < Size + Pad; lz++)
            {
                var row = P(-Pad, ly, lz);
                if (wy < 0 || wy >= ChunkConstants.Height)
                {
                    Array.Clear(solid, row, Dim);
                    continue;
                }

                var inside = (uint)lz < Size;
                for (var lx = -Pad; lx < Size + Pad; lx++)
                {
                    var block = inside && (uint)lx < Size ? raw[Chunk.Index(lx, wy, lz)] : view.Block(lx, wy, lz);
                    solid[row + lx + Pad] = (byte)(Blocks.IsOpaque(block) ? 1 : 0);
                }
            }
        }
    }

    private static void MeshSection(View view, int originY, byte[] solid, ulong[] mask, List<ChunkVertex> vertices, List<uint> indices)
    {
        FillSolid(view, originY, solid);
        Span<int> origin = [0, originY, 0];
        Span<int> local = stackalloc int[3];

        for (var face = 0; face < 6; face++)
        {
            var axis = face >> 1;
            var u = (axis + 1) % 3;
            var v = (axis + 2) % 3;
            var step = (face & 1) == 0 ? 1 : -1;
            var strideA = StrideOf(axis);
            var strideU = StrideOf(u);
            var strideV = StrideOf(v);
            var frontOffset = step * strideA;

            for (var slice = 0; slice < Size; slice++)
            {
                var any = false;
                var sliceBase = P(0, 0, 0) + (slice * strideA);
                for (var j = 0; j < Size; j++)
                {
                    var rowBase = sliceBase + (j * strideV);
                    for (var i = 0; i < Size; i++)
                    {
                        var index = rowBase + (i * strideU);
                        ulong key = 0;
                        if (solid[index] != 0 && solid[index + frontOffset] == 0)
                        {
                            local[0] = 0;
                            local[1] = 0;
                            local[2] = 0;
                            local[axis] = slice;
                            local[u] = i;
                            local[v] = j;
                            var block = view.Block(local[0], originY + local[1], local[2]);
                            local[axis] += step;
                            key = FaceKey(view, solid, originY, block, face, u, v, local);
                            any = true;
                        }

                        mask[(j * Size) + i] = key;
                    }
                }

                if (any)
                {
                    Merge(mask, face, axis, u, v, origin, slice, step, vertices, indices);
                }
            }
        }
    }

    /// <summary>
    /// Packs everything that makes two faces look different into one number: tile (16 bits), corner ambient occlusion
    /// (4 x 2 bits), and corner light (4 x 8 bits), plus a flag so a real face is never zero.
    /// <paramref name="front"/> is the open cell in front of the face, in section-local coordinates.
    /// </summary>
    private static ulong FaceKey(View view, byte[] solid, int originY, ushort block, int face, int u, int v, ReadOnlySpan<int> front)
    {
        var strideU = StrideOf(u);
        var strideV = StrideOf(v);
        var frontIndex = P(front[0], front[1], front[2]);
        ulong aoBits = 0;
        ulong lightBits = 0;

        for (var corner = 0; corner < 4; corner++)
        {
            var su = corner is 0 or 3 ? -1 : 1;
            var sv = corner < 2 ? -1 : 1;

            var side1 = solid[frontIndex + (su * strideU)] != 0;
            var side2 = solid[frontIndex + (sv * strideV)] != 0;
            var diagonal = solid[frontIndex + (su * strideU) + (sv * strideV)] != 0;
            var ao = side1 && side2 ? 0 : 3 - ((side1 ? 1 : 0) + (side2 ? 1 : 0) + (diagonal ? 1 : 0));

            int sky = 0, blockLight = 0, count = 0;
            Sample(view, solid, originY, front, frontIndex, 0, u, 0, v, ref sky, ref blockLight, ref count);
            Sample(view, solid, originY, front, frontIndex + (su * strideU), su, u, 0, v, ref sky, ref blockLight, ref count);
            Sample(view, solid, originY, front, frontIndex + (sv * strideV), 0, u, sv, v, ref sky, ref blockLight, ref count);
            Sample(view, solid, originY, front, frontIndex + (su * strideU) + (sv * strideV), su, u, sv, v, ref sky, ref blockLight, ref count);
            if (count == 0)
            {
                view.Light(front[0], originY + front[1], front[2], out sky, out blockLight);
                count = 1;
            }

            var packed = (ulong)(uint)((((sky + (count / 2)) / count) << 4) | ((blockLight + (count / 2)) / count));
            aoBits |= (ulong)(uint)ao << (corner * 2);
            lightBits |= packed << (corner * 8);
        }

        return Present | (ulong)(uint)Blocks.Tile(block, face) | (aoBits << 16) | (lightBits << 24);
    }

    /// <summary>Adds the light of one of the four cells around a corner, skipping opaque cells.</summary>
    private static void Sample(
        View view,
        byte[] solid,
        int originY,
        ReadOnlySpan<int> front,
        int index,
        int du,
        int u,
        int dv,
        int v,
        ref int sky,
        ref int blockLight,
        ref int count)
    {
        if (solid[index] != 0)
        {
            return;
        }

        var x = front[0];
        var y = front[1];
        var z = front[2];
        Move(ref x, ref y, ref z, u, du);
        Move(ref x, ref y, ref z, v, dv);
        view.Light(x, originY + y, z, out var s, out var b);
        sky += s;
        blockLight += b;
        count++;
    }

    private static void Move(ref int x, ref int y, ref int z, int axis, int delta)
    {
        switch (axis)
        {
            case 0:
                x += delta;
                break;
            case 1:
                y += delta;
                break;
            default:
                z += delta;
                break;
        }
    }

    private static void Merge(
        ulong[] mask,
        int face,
        int axis,
        int u,
        int v,
        ReadOnlySpan<int> origin,
        int slice,
        int step,
        List<ChunkVertex> vertices,
        List<uint> indices)
    {
        for (var j = 0; j < Size; j++)
        {
            for (var i = 0; i < Size;)
            {
                var key = mask[(j * Size) + i];
                if (key == 0)
                {
                    i++;
                    continue;
                }

                var width = 1;
                while (i + width < Size && mask[(j * Size) + i + width] == key)
                {
                    width++;
                }

                var height = 1;
                while (j + height < Size && RowMatches(mask, (j + height) * Size, i, width, key))
                {
                    height++;
                }

                for (var dj = 0; dj < height; dj++)
                {
                    Array.Clear(mask, ((j + dj) * Size) + i, width);
                }

                EmitQuad(key, face, axis, u, v, origin, slice, step, i, j, width, height, vertices, indices);
                i += width;
            }
        }
    }

    private static bool RowMatches(ulong[] mask, int rowStart, int column, int width, ulong key)
    {
        for (var k = 0; k < width; k++)
        {
            if (mask[rowStart + column + k] != key)
            {
                return false;
            }
        }

        return true;
    }

    private static void EmitQuad(
        ulong key,
        int face,
        int axis,
        int u,
        int v,
        ReadOnlySpan<int> origin,
        int slice,
        int step,
        int i,
        int j,
        int width,
        int height,
        List<ChunkVertex> vertices,
        List<uint> indices)
    {
        var plane = origin[axis] + slice + (step > 0 ? 1 : 0);
        var u0 = origin[u] + i;
        var v0 = origin[v] + j;
        var tile = (ushort)(key & 0xFFFF);
        var baseIndex = (uint)vertices.Count;

        Span<int> aoValues = stackalloc int[4];
        Span<int> position = stackalloc int[3];
        for (var corner = 0; corner < 4; corner++)
        {
            var ao = (int)((key >> (16 + (corner * 2))) & 3);
            aoValues[corner] = ao;
            var packedLight = (int)((key >> (24 + (corner * 8))) & 0xFF);

            position[axis] = plane;
            position[u] = corner is 1 or 2 ? u0 + width : u0;
            position[v] = corner >= 2 ? v0 + height : v0;

            vertices.Add(new ChunkVertex(
                (byte)position[0],
                (byte)position[1],
                (byte)position[2],
                (byte)face,
                tile,
                (byte)(corner is 1 or 2 ? width : 0),
                (byte)(corner >= 2 ? height : 0),
                (byte)ao,
                (byte)(packedLight >> 4),
                (byte)(packedLight & 15)));
        }

        // Split the quad along the diagonal that hides the smaller ambient occlusion step.
        var flip = aoValues[0] + aoValues[2] < aoValues[1] + aoValues[3];
        uint a = baseIndex, b = baseIndex + 1, c = baseIndex + 2, d = baseIndex + 3;
        if (step < 0)
        {
            (b, d) = (d, b);
        }

        if (flip)
        {
            indices.Add(b);
            indices.Add(c);
            indices.Add(d);
            indices.Add(b);
            indices.Add(d);
            indices.Add(a);
        }
        else
        {
            indices.Add(a);
            indices.Add(b);
            indices.Add(c);
            indices.Add(a);
            indices.Add(c);
            indices.Add(d);
        }
    }

    /// <summary>Reads blocks and light for a chunk and its edge neighbors, treating anything outside as open air.</summary>
    private sealed class View(Chunk chunk, LightMap light, ChunkNeighbors neighbors)
    {
        public Chunk Chunk => chunk;

        public ushort Block(int x, int y, int z)
        {
            if (y < 0 || y >= ChunkConstants.Height)
            {
                return Blocks.Air;
            }

            if ((uint)x < Size && (uint)z < Size)
            {
                return chunk.Get(x, y, z);
            }

            // Diagonal cells fall back to the neighbor along x, with z clamped.
            Chunk? other;
            int lx = x, lz = z;
            if (x < 0)
            {
                other = neighbors.NegX;
                lx = x + Size;
                lz = Math.Clamp(z, 0, Size - 1);
            }
            else if (x >= Size)
            {
                other = neighbors.PosX;
                lx = x - Size;
                lz = Math.Clamp(z, 0, Size - 1);
            }
            else if (z < 0)
            {
                other = neighbors.NegZ;
                lz = z + Size;
            }
            else
            {
                other = neighbors.PosZ;
                lz = z - Size;
            }

            return other is null ? Blocks.Air : other.Get(lx, y, lz);
        }

        /// <summary>Light at a cell; outside the chunk horizontally it uses the nearest cell inside, above the world is full sky.</summary>
        public void Light(int x, int y, int z, out int sky, out int block)
        {
            if (y >= ChunkConstants.Height)
            {
                sky = Blocks.MaxLight;
                block = 0;
                return;
            }

            if (y < 0)
            {
                sky = 0;
                block = 0;
                return;
            }

            var packed = light.Data[Chunk.Index(Math.Clamp(x, 0, Size - 1), y, Math.Clamp(z, 0, Size - 1))];
            sky = packed >> 4;
            block = packed & 15;
        }
    }
}
