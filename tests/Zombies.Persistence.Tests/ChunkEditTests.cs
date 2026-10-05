using Zombies.Engine.Voxel;
using Zombies.Persistence.Sqlite;

namespace Zombies.Persistence.Tests;

public sealed class ChunkEditTests
{
    private static readonly ChunkCoord Origin = new(0, 0);

    private static Chunk Edited(out Chunk generated)
    {
        generated = Fixtures.Generator.Generate(Origin);
        var current = Fixtures.Generator.Generate(Origin);
        var top = current.TopOpaque(3, 4);
        current.Set(3, top, 4, Blocks.Air);
        current.Set(3, top + 1, 4, Blocks.Dirt);
        current.Set(15, 0, 15, Blocks.Air);
        current.RecomputeHeights();
        return current;
    }

    [Fact]
    public void Between_FindsExactlyTheBlocksThatChanged()
    {
        var current = Edited(out var generated);

        var edits = ChunkEdits.Between(generated, current);

        Assert.Equal(3, edits.Count);
        Assert.Contains(new BlockEdit(15, 0, 15, Blocks.Air), edits);
    }

    [Fact]
    public void Between_AnUntouchedChunk_HasNoEdits()
    {
        var generated = Fixtures.Generator.Generate(Origin);

        Assert.Empty(ChunkEdits.Between(generated, Fixtures.Generator.Generate(Origin)));
    }

    [Fact]
    public void SaveThenLoad_ReplayedOntoAFreshChunk_ReproducesTheEditedChunk()
    {
        var current = Edited(out var generated);
        using var save = new TempSave();
        using (var database = save.Open())
        {
            new SqliteChunkEditRepository(database).Save(Origin, ChunkEdits.Between(generated, current));
        }

        using var reopened = save.Open();
        var restored = Fixtures.Generator.Generate(Origin);
        ChunkEdits.Apply(restored, new SqliteChunkEditRepository(reopened).Load(Origin));

        Assert.Equal(current.Hash(), restored.Hash());
        Assert.Equal(current.TopOpaque(3, 4), restored.TopOpaque(3, 4));
    }

    [Fact]
    public void Load_AChunkWithNoEdits_ReturnsNone()
    {
        using var save = new TempSave();
        using var database = save.Open();

        Assert.Empty(new SqliteChunkEditRepository(database).Load(new ChunkCoord(-4, 9)));
    }

    [Fact]
    public void Save_Twice_ReplacesTheEarlierEdits()
    {
        using var save = new TempSave();
        using var database = save.Open();
        var repository = new SqliteChunkEditRepository(database);
        repository.Save(Origin, [new BlockEdit(1, 2, 3, Blocks.Dirt), new BlockEdit(4, 5, 6, Blocks.Air)]);

        repository.Save(Origin, [new BlockEdit(7, 8, 9, Blocks.Grass)]);

        Assert.Equal([new BlockEdit(7, 8, 9, Blocks.Grass)], repository.Load(Origin));
    }

    [Fact]
    public void Save_NoEdits_RemovesTheChunkFromTheSave()
    {
        using var save = new TempSave();
        using var database = save.Open();
        var repository = new SqliteChunkEditRepository(database);
        repository.Save(Origin, [new BlockEdit(1, 2, 3, Blocks.Dirt)]);

        repository.Save(Origin, []);

        Assert.Empty(repository.EditedChunks());
    }

    [Fact]
    public void EditedChunks_ListsEveryChunkWithEditsIncludingNegativeCoordinates()
    {
        using var save = new TempSave();
        using var database = save.Open();
        var repository = new SqliteChunkEditRepository(database);
        repository.Save(new ChunkCoord(2, -1), [new BlockEdit(0, 0, 0, Blocks.Air)]);
        repository.Save(new ChunkCoord(-3, 4), [new BlockEdit(0, 0, 0, Blocks.Air)]);

        Assert.Equal([new ChunkCoord(-3, 4), new ChunkCoord(2, -1)], repository.EditedChunks());
    }

    [Fact]
    public void Edits_AreStoredAsCompressedBinary()
    {
        var edits = new List<BlockEdit>();
        for (var y = 0; y < 8; y++)
        {
            for (var z = 0; z < ChunkConstants.Size; z++)
            {
                for (var x = 0; x < ChunkConstants.Size; x++)
                {
                    edits.Add(new BlockEdit(x, y, z, Blocks.Air));
                }
            }
        }

        var stored = ChunkEditCodec.Encode(edits);

        Assert.True(stored.Length < edits.Count * 4, $"{edits.Count} edits took {stored.Length} bytes; four bytes each uncompressed would be {edits.Count * 4}.");
        Assert.Equal(edits, ChunkEditCodec.Decode(stored));
    }

    [Theory]
    [InlineData(16, 0, 0)]
    [InlineData(0, 0, 16)]
    [InlineData(0, 128, 0)]
    [InlineData(-1, 0, 0)]
    public void Encode_ABlockOutsideTheChunk_Throws(int x, int y, int z)
    {
        Assert.Throws<ArgumentException>(() => ChunkEditCodec.Encode([new BlockEdit(x, y, z, Blocks.Air)]));
    }

    [Fact]
    public void Decode_DamagedData_ThrowsASaveCorruptException()
    {
        Assert.Throws<SaveCorruptException>(() => ChunkEditCodec.Decode([1, 2, 3, 4, 5, 6, 7, 8]));
    }

    [Fact]
    public void Load_AStoredBlobThatIsNotCompressedEdits_ThrowsASaveCorruptException()
    {
        using var save = new TempSave();
        using var database = save.Open();
        database.Transact(t => database.Command(t, "INSERT INTO chunk_edits (chunk_x, chunk_z, data) VALUES (0, 0, x'00010203')").ExecuteNonQuery());

        Assert.Throws<SaveCorruptException>(() => new SqliteChunkEditRepository(database).Load(Origin));
    }
}
