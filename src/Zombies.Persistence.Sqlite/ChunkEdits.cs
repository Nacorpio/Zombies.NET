using System.Buffers.Binary;
using System.IO.Compression;
using Zombies.Engine.Voxel;

namespace Zombies.Persistence.Sqlite;

/// <summary>One block a player changed in a chunk, in the chunk's own coordinates.</summary>
public readonly record struct BlockEdit(int X, int Y, int Z, ushort Block);

/// <summary>
/// Chunk edits are a diff against what the world generator makes, not a copy of the chunk, so a save stays small and a
/// generator fix still reaches every block nobody touched.
/// </summary>
public static class ChunkEdits
{
    /// <summary>The blocks that differ between a freshly generated chunk and the same chunk after play, in index order.</summary>
    public static IReadOnlyList<BlockEdit> Between(Chunk generated, Chunk current)
    {
        ArgumentNullException.ThrowIfNull(generated);
        ArgumentNullException.ThrowIfNull(current);
        if (generated.Coord != current.Coord)
        {
            throw new ArgumentException("The two chunks are not the same chunk.", nameof(current));
        }

        var before = generated.BlockData;
        var after = current.BlockData;
        var edits = new List<BlockEdit>();
        for (var index = 0; index < after.Length; index++)
        {
            if (before[index] != after[index])
            {
                var x = index % ChunkConstants.Size;
                var z = index / ChunkConstants.Size % ChunkConstants.Size;
                var y = index / (ChunkConstants.Size * ChunkConstants.Size);
                edits.Add(new BlockEdit(x, y, z, after[index]));
            }
        }

        return edits;
    }

    /// <summary>Replays edits onto a chunk and refreshes its column heights. Later edits to the same block win.</summary>
    public static void Apply(Chunk chunk, IEnumerable<BlockEdit> edits)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        ArgumentNullException.ThrowIfNull(edits);
        foreach (var edit in edits)
        {
            chunk.Set(edit.X, edit.Y, edit.Z, edit.Block);
        }

        chunk.RecomputeHeights();
    }
}

/// <summary>
/// The stored form of a chunk's edits: a count and four bytes per edit (x and z packed in one byte, y, and the block as a
/// little-endian ushort), compressed with Brotli. Binary because a busy chunk has thousands of edits.
/// </summary>
internal static class ChunkEditCodec
{
    private const int BytesPerEdit = 4;
    private const int MaxEdits = ChunkConstants.Volume;

    public static byte[] Encode(IReadOnlyList<BlockEdit> edits)
    {
        if (edits.Count > MaxEdits)
        {
            throw new ArgumentException($"A chunk has {MaxEdits} blocks, so it cannot have {edits.Count} edits.", nameof(edits));
        }

        var raw = new byte[4 + (edits.Count * BytesPerEdit)];
        BinaryPrimitives.WriteInt32LittleEndian(raw, edits.Count);
        for (var i = 0; i < edits.Count; i++)
        {
            var edit = edits[i];
            if ((uint)edit.X >= ChunkConstants.Size || (uint)edit.Z >= ChunkConstants.Size || (uint)edit.Y >= ChunkConstants.Height)
            {
                throw new ArgumentException($"Edit ({edit.X}, {edit.Y}, {edit.Z}) is outside a chunk.", nameof(edits));
            }

            var offset = 4 + (i * BytesPerEdit);
            raw[offset] = (byte)((edit.X << 4) | edit.Z);
            raw[offset + 1] = (byte)edit.Y;
            BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(offset + 2), edit.Block);
        }

        using var output = new MemoryStream();
        using (var brotli = new BrotliStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            brotli.Write(raw);
        }

        return output.ToArray();
    }

    public static IReadOnlyList<BlockEdit> Decode(byte[] data)
    {
        // The most a valid chunk can expand to bounds what a damaged or hostile file may make us allocate.
        const int MaxRaw = 4 + (MaxEdits * BytesPerEdit);
        byte[] raw;
        try
        {
            using var input = new MemoryStream(data);
            using var brotli = new BrotliStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            var buffer = new byte[8192];
            int read;
            while ((read = brotli.Read(buffer)) > 0)
            {
                if (output.Length + read > MaxRaw)
                {
                    throw new SaveCorruptException("Chunk edits expand to more than a chunk can hold.");
                }

                output.Write(buffer, 0, read);
            }

            raw = output.ToArray();
        }
        catch (InvalidDataException ex)
        {
            throw new SaveCorruptException("Chunk edits are not valid compressed data.", ex);
        }

        if (raw.Length < 4)
        {
            throw new SaveCorruptException("Chunk edits are truncated.");
        }

        var count = BinaryPrimitives.ReadInt32LittleEndian(raw);
        if (count < 0 || count > MaxEdits || raw.Length != 4 + (count * BytesPerEdit))
        {
            throw new SaveCorruptException("Chunk edits do not match their stated count.");
        }

        var edits = new List<BlockEdit>(count);
        for (var i = 0; i < count; i++)
        {
            var offset = 4 + (i * BytesPerEdit);
            var y = raw[offset + 1];
            if (y >= ChunkConstants.Height)
            {
                throw new SaveCorruptException($"A chunk edit is at height {y}, above the top of the world.");
            }

            edits.Add(new BlockEdit(raw[offset] >> 4, y, raw[offset] & 0xF, BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(offset + 2))));
        }

        return edits;
    }
}

/// <summary>Stores the edits players made to each chunk.</summary>
public interface IChunkEditRepository
{
    /// <summary>The edits stored for a chunk, or none.</summary>
    IReadOnlyList<BlockEdit> Load(ChunkCoord coord);

    /// <summary>Stores the edits of a chunk, replacing what was stored. No edits removes the chunk from the save.</summary>
    void Save(ChunkCoord coord, IReadOnlyList<BlockEdit> edits);

    /// <summary>Every chunk that has stored edits.</summary>
    IReadOnlyList<ChunkCoord> EditedChunks();
}

public sealed class SqliteChunkEditRepository(SaveDatabase database) : IChunkEditRepository
{
    public IReadOnlyList<BlockEdit> Load(ChunkCoord coord)
    {
        using var command = database.Command(null, "SELECT data FROM chunk_edits WHERE chunk_x = $x AND chunk_z = $z", ("$x", coord.X), ("$z", coord.Z));
        return command.ExecuteScalar() is byte[] data ? ChunkEditCodec.Decode(data) : [];
    }

    public void Save(ChunkCoord coord, IReadOnlyList<BlockEdit> edits)
    {
        ArgumentNullException.ThrowIfNull(edits);
        if (edits.Count == 0)
        {
            database.Transact(t => database.Command(t, "DELETE FROM chunk_edits WHERE chunk_x = $x AND chunk_z = $z", ("$x", coord.X), ("$z", coord.Z)).ExecuteNonQuery());
            return;
        }

        var data = ChunkEditCodec.Encode(edits);
        database.Transact(t => database.Command(
            t,
            "INSERT INTO chunk_edits (chunk_x, chunk_z, data) VALUES ($x, $z, $data) ON CONFLICT (chunk_x, chunk_z) DO UPDATE SET data = excluded.data",
            ("$x", coord.X),
            ("$z", coord.Z),
            ("$data", data)).ExecuteNonQuery());
    }

    public IReadOnlyList<ChunkCoord> EditedChunks()
    {
        var coords = new List<ChunkCoord>();
        using var command = database.Command(null, "SELECT chunk_x, chunk_z FROM chunk_edits ORDER BY chunk_x, chunk_z");
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            coords.Add(new ChunkCoord(reader.GetInt32(0), reader.GetInt32(1)));
        }

        return coords;
    }
}
