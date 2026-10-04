using Zombies.Domain.World;
using Zombies.Engine.Voxel;

namespace Zombies.Engine.Tests;

public sealed class VoxelTests
{
    private static readonly Biome Forest = new("base:biome/temperate_forest", ["forest", "grassland"], (0, 100), (0, 100), 52, 14, 50);

    private static WorldGenerator Generator(ulong seed = 12345, Biome? biome = null) => new(seed, new BiomeCatalog([biome ?? Forest]));

    [Fact]
    public void Noise_MatchesKnownAnswers()
    {
        Assert.Equal(9900, IntNoise.Value2D(12345, 100, -200, 6));
        Assert.Equal(35386, IntNoise.Fractal2D(12345, -37, 911, 6, 4));
    }

    [Fact]
    public void Noise_StaysInRangeIsDeterministicAndSmooth()
    {
        var previous = IntNoise.Value2D(5, -300, 40, 6);
        for (var x = -299; x < 300; x++)
        {
            var value = IntNoise.Value2D(5, x, 40, 6);
            Assert.InRange(value, 0, IntNoise.Max);
            Assert.Equal(value, IntNoise.Value2D(5, x, 40, 6));
            Assert.InRange(Math.Abs(value - previous), 0, 2500);
            previous = value;
        }

        Assert.InRange(IntNoise.Fractal2D(5, int.MinValue / 2, int.MaxValue / 2, 6, 4), 0, IntNoise.Max);
    }

    [Fact]
    public void Chunk_SameSeedGivesTheSameHashAndOtherSeedsDiffer()
    {
        var coord = new ChunkCoord(3, -4);

        Assert.Equal(Generator().Generate(coord).Hash(), Generator().Generate(coord).Hash());
        Assert.NotEqual(Generator(12345).Generate(coord).Hash(), Generator(54321).Generate(coord).Hash());
        Assert.NotEqual(Generator().Generate(new ChunkCoord(0, 0)).Hash(), Generator().Generate(new ChunkCoord(1, 0)).Hash());
    }

    [Fact]
    public void Chunk_HashesMatchKnownAnswers_SoEveryPlatformBuildsTheSameWorld()
    {
        var generator = Generator();

        Assert.Equal(0x2D6ECCC31C497AABUL, generator.Generate(new ChunkCoord(0, 0)).Hash());
        Assert.Equal(0xB548826F09E65DFEUL, generator.Generate(new ChunkCoord(1, 0)).Hash());
        Assert.Equal(0x95E14EAD5335468EUL, generator.Generate(new ChunkCoord(-3, 5)).Hash());
        Assert.Equal(0x010101, WorldGenerator.GeneratorVersion);
    }

    [Fact]
    public void Chunk_DoesNotDependOnWhichChunksWereBuiltBefore()
    {
        var first = Generator();
        var second = Generator();

        var a1 = first.Generate(new ChunkCoord(2, 2)).Hash();
        var b1 = first.Generate(new ChunkCoord(-5, 7)).Hash();
        var b2 = second.Generate(new ChunkCoord(-5, 7)).Hash();
        var a2 = second.Generate(new ChunkCoord(2, 2)).Hash();

        Assert.Equal(a1, a2);
        Assert.Equal(b1, b2);
    }

    [Fact]
    public void Chunk_HashChangesWhenABlockChanges()
    {
        var chunk = Generator().Generate(new ChunkCoord(0, 0));
        var before = chunk.Hash();

        chunk.Set(3, 100, 3, Blocks.Stone);

        Assert.NotEqual(before, chunk.Hash());
    }

    [Fact]
    public void Terrain_HasBedrockSolidGroundAndGrassOnTop()
    {
        var chunk = Generator().Generate(new ChunkCoord(0, 0));

        for (var z = 0; z < ChunkConstants.Size; z++)
        {
            for (var x = 0; x < ChunkConstants.Size; x++)
            {
                Assert.Equal(Blocks.Bedrock, chunk.Get(x, 0, z));
                var ground = 0;
                while (ground + 1 < ChunkConstants.Height && chunk.Get(x, ground + 1, z) is not Blocks.Air and not Blocks.Log and not Blocks.Leaves)
                {
                    ground++;
                }

                Assert.Equal(Blocks.Grass, chunk.Get(x, ground, z));
                Assert.InRange(ground, 52 - 14, 52 + 14);
                Assert.Equal(Blocks.Dirt, chunk.Get(x, ground - 1, z));
                Assert.Equal(Blocks.Stone, chunk.Get(x, ground - 5, z));
            }
        }
    }

    [Fact]
    public void Terrain_HeightmapMatchesTheBlocks()
    {
        var chunk = Generator().Generate(new ChunkCoord(-2, 3));

        for (var z = 0; z < ChunkConstants.Size; z++)
        {
            for (var x = 0; x < ChunkConstants.Size; x++)
            {
                var top = chunk.TopOpaque(x, z);
                Assert.True(Blocks.IsOpaque(chunk.Get(x, top, z)));
                Assert.All(Enumerable.Range(top + 1, ChunkConstants.Height - top - 1), y => Assert.False(Blocks.IsOpaque(chunk.Get(x, y, z))));
            }
        }
    }

    [Fact]
    public void Trees_GrowInForestsAndNotWhereTheBiomeHasNone()
    {
        var generator = Generator();
        var bare = Generator(biome: new Biome("t:biome/bare", ["plains"], (0, 100), (0, 100), 52, 14, 0));

        var forestLogs = 0;
        var bareLogs = 0;
        for (var x = 0; x < 4; x++)
        {
            for (var z = 0; z < 4; z++)
            {
                forestLogs += generator.Generate(new ChunkCoord(x, z)).BlockData.Count(Blocks.Log);
                bareLogs += bare.Generate(new ChunkCoord(x, z)).BlockData.Count(Blocks.Log);
            }
        }

        Assert.True(forestLogs > 50);
        Assert.Equal(0, bareLogs);
    }

    [Fact]
    public void Trees_AreIdenticalOnBothSidesOfAChunkBorder()
    {
        var generator = Generator();
        var checkedTrunks = 0;

        for (var cx = -3; cx < 3; cx++)
        {
            var west = generator.Generate(new ChunkCoord(cx, 0));
            var east = generator.Generate(new ChunkCoord(cx + 1, 0));
            for (var z = 0; z < ChunkConstants.Size; z++)
            {
                // A trunk on the east edge of the west chunk has a crown that reaches into the east chunk.
                var top = -1;
                for (var y = 0; y < ChunkConstants.Height; y++)
                {
                    if (west.Get(ChunkConstants.Size - 1, y, z) == Blocks.Log)
                    {
                        top = y;
                    }
                }

                if (top < 0)
                {
                    continue;
                }

                checkedTrunks++;
                Assert.Contains(east.Get(0, top, z), new[] { Blocks.Leaves, Blocks.Log });
                Assert.Contains(east.Get(1, top - 1, z), new[] { Blocks.Leaves, Blocks.Log });
            }
        }

        Assert.True(checkedTrunks > 0, "no trunk on a chunk edge was found to check");
    }

    [Fact]
    public void Light_OpenSkyIsFullAndGroundIsDark()
    {
        var chunk = ChunkWithFloor(10);
        var light = ChunkLighting.Compute(chunk, ChunkNeighbors.None);

        Assert.Equal(15, light.Sky(5, 11, 5));
        Assert.Equal(15, light.Sky(5, 127, 5));
        Assert.Equal(0, light.Sky(5, 10, 5));
        Assert.Equal(0, light.Block(5, 11, 5));
    }

    [Fact]
    public void Light_SpreadsSidewaysUnderARoofLosingOneLevelPerCell()
    {
        var chunk = new Chunk(new ChunkCoord(0, 0));
        for (var x = 7; x <= 9; x++)
        {
            for (var z = 7; z <= 9; z++)
            {
                chunk.Set(x, 50, z, Blocks.Stone);
            }
        }

        chunk.RecomputeHeights();
        var light = ChunkLighting.Compute(chunk, ChunkNeighbors.None);

        Assert.Equal(15, light.Sky(6, 49, 8));
        Assert.Equal(14, light.Sky(7, 49, 8));
        Assert.Equal(13, light.Sky(8, 49, 8));
        Assert.Equal(13, light.Sky(8, 20, 8));
    }

    [Fact]
    public void Light_SealedCavesAreDark()
    {
        var chunk = new Chunk(new ChunkCoord(0, 0));
        for (var y = 0; y < ChunkConstants.Height; y++)
        {
            for (var z = 0; z < ChunkConstants.Size; z++)
            {
                for (var x = 0; x < ChunkConstants.Size; x++)
                {
                    chunk.Set(x, y, z, Blocks.Stone);
                }
            }
        }

        chunk.Set(8, 50, 8, Blocks.Air);
        chunk.RecomputeHeights();
        var light = ChunkLighting.Compute(chunk, ChunkNeighbors.None);

        Assert.Equal(0, light.Sky(8, 50, 8));
        Assert.Equal(0, light.Block(8, 50, 8));
    }

    [Fact]
    public void Light_BlockLightSpreadsFromALampAndWallsBlockIt()
    {
        var chunk = new Chunk(new ChunkCoord(0, 0));
        chunk.Set(8, 30, 8, Blocks.Lamp);
        for (var y = 25; y <= 35; y++)
        {
            for (var z = 5; z <= 11; z++)
            {
                chunk.Set(9, y, z, Blocks.Stone);
            }
        }

        chunk.RecomputeHeights();
        var light = ChunkLighting.Compute(chunk, ChunkNeighbors.None);

        Assert.Equal(14, light.Block(8, 30, 8));
        Assert.Equal(13, light.Block(7, 30, 8));
        Assert.Equal(11, light.Block(5, 30, 8));
        Assert.Equal(4, light.Block(10, 30, 8));
        Assert.Equal(0, light.Block(9, 30, 8));
    }

    [Fact]
    public void Light_EntersFromAnOpenNeighborChunk()
    {
        var chunk = new Chunk(new ChunkCoord(0, 0));
        for (var z = 0; z < ChunkConstants.Size; z++)
        {
            for (var x = 0; x < ChunkConstants.Size; x++)
            {
                chunk.Set(x, 60, z, Blocks.Stone);
            }
        }

        chunk.RecomputeHeights();
        var open = new Chunk(new ChunkCoord(-1, 0));
        open.RecomputeHeights();
        var walled = new Chunk(new ChunkCoord(-1, 0));
        for (var y = 0; y < ChunkConstants.Height; y++)
        {
            for (var z = 0; z < ChunkConstants.Size; z++)
            {
                walled.Set(ChunkConstants.Size - 1, y, z, Blocks.Stone);
            }
        }

        walled.RecomputeHeights();

        var withOpen = ChunkLighting.Compute(chunk, new ChunkNeighbors(null, open, null, null));
        var withWall = ChunkLighting.Compute(chunk, new ChunkNeighbors(null, walled, null, null));

        Assert.Equal(14, withOpen.Sky(0, 30, 8));
        Assert.Equal(13, withOpen.Sky(1, 30, 8));
        Assert.True(withWall.Sky(0, 30, 8) < withOpen.Sky(0, 30, 8) || withWall.Sky(0, 30, 8) == 0);
    }

    [Fact]
    public void Mesher_ASingleBlockHasSixFaces()
    {
        var chunk = new Chunk(new ChunkCoord(0, 0));
        chunk.Set(8, 20, 8, Blocks.Stone);
        chunk.RecomputeHeights();

        var mesh = Mesh(chunk);

        Assert.Equal(6, mesh.QuadCount);
        Assert.Equal(24, mesh.VertexCount);
        Assert.Equal(36, mesh.Sections.Sum(s => s.Indices.Length));
    }

    [Fact]
    public void Mesher_MergesFlatFacesAndHidesFacesBetweenBlocks()
    {
        var floor = ChunkWithFloor(10);
        var pair = new Chunk(new ChunkCoord(0, 0));
        pair.Set(4, 20, 4, Blocks.Stone);
        pair.Set(5, 20, 4, Blocks.Stone);
        pair.RecomputeHeights();

        // The top and the four sides of a flat floor are uniformly lit, so each merges into a single quad.
        // The underside is not: light spills in from the open chunk edges and fades toward the middle, so it correctly stays split.
        var floorMesh = Mesh(floor);
        foreach (var face in new[] { 0, 1, 2, 4, 5 })
        {
            var quads = floorMesh.Sections.SelectMany(s => s.Vertices.ToArray()).Count(v => v.Face == face) / 4;
            Assert.Equal(1, quads);
        }

        Assert.True(floorMesh.Sections.SelectMany(s => s.Vertices.ToArray()).Count(v => v.Face == 3) / 4 > 1);
        Assert.Equal(6, Mesh(pair).QuadCount);
    }

    [Fact]
    public void Mesher_DifferentBlocksDoNotMerge()
    {
        var chunk = new Chunk(new ChunkCoord(0, 0));
        chunk.Set(4, 20, 4, Blocks.Stone);
        chunk.Set(5, 20, 4, Blocks.Dirt);
        chunk.RecomputeHeights();

        Assert.Equal(10, Mesh(chunk).QuadCount);
    }

    [Fact]
    public void Mesher_EmptyChunkAndEmptySectionsProduceNothing()
    {
        var empty = Mesh(new Chunk(new ChunkCoord(0, 0)));
        var oneBlock = new Chunk(new ChunkCoord(0, 0));
        oneBlock.Set(1, 20, 1, Blocks.Stone);
        oneBlock.RecomputeHeights();

        Assert.Equal(ChunkConstants.SectionCount, empty.Sections.Count);
        Assert.Equal(0, empty.QuadCount);
        Assert.Equal(1, Mesh(oneBlock).Sections.Count(s => !s.IsEmpty));
    }

    [Fact]
    public void Mesher_FacesNextToANeighborChunkAreHiddenWhenThatBlockIsSolid()
    {
        var chunk = new Chunk(new ChunkCoord(0, 0));
        chunk.Set(15, 20, 8, Blocks.Stone);
        chunk.RecomputeHeights();
        var neighbor = new Chunk(new ChunkCoord(1, 0));
        neighbor.Set(0, 20, 8, Blocks.Stone);
        neighbor.RecomputeHeights();

        var without = Mesh(chunk, ChunkNeighbors.None);
        var with = Mesh(chunk, new ChunkNeighbors(neighbor, null, null, null));

        Assert.Equal(6, without.QuadCount);
        Assert.Equal(5, with.QuadCount);
    }

    [Fact]
    public void Mesher_VisibleAreaMatchesABruteForceFaceCountOnRealTerrain()
    {
        var pipeline = new ChunkPipeline(Generator(), workerCount: 1);
        try
        {
            var coord = new ChunkCoord(0, 0);
            var chunk = pipeline.GetChunk(coord);
            var neighbors = pipeline.NeighborsOf(coord);
            var mesh = ChunkMesher.Build(chunk, ChunkLighting.Compute(chunk, neighbors), neighbors);

            var area = 0L;
            foreach (var section in mesh.Sections)
            {
                var vertices = section.Vertices.Span;
                for (var quad = 0; quad < vertices.Length / 4; quad++)
                {
                    area += vertices[(quad * 4) + 2].U * vertices[(quad * 4) + 2].V;
                }
            }

            Assert.Equal(CountVisibleFaces(chunk, neighbors), area);
            Assert.True(mesh.QuadCount > 100);
        }
        finally
        {
            pipeline.Dispose();
        }
    }

    [Fact]
    public void Mesher_TrianglesFaceOutward()
    {
        var pipeline = new ChunkPipeline(Generator(), workerCount: 1);
        try
        {
            var mesh = pipeline.Build(new ChunkCoord(0, 0));
            var normals = new (int X, int Y, int Z)[] { (1, 0, 0), (-1, 0, 0), (0, 1, 0), (0, -1, 0), (0, 0, 1), (0, 0, -1) };

            foreach (var section in mesh.Sections)
            {
                var vertices = section.Vertices.Span;
                var indices = section.Indices.Span;
                for (var t = 0; t < indices.Length; t += 3)
                {
                    var a = vertices[(int)indices[t]];
                    var b = vertices[(int)indices[t + 1]];
                    var c = vertices[(int)indices[t + 2]];
                    var (abx, aby, abz) = (b.X - a.X, b.Y - a.Y, b.Z - a.Z);
                    var (acx, acy, acz) = (c.X - a.X, c.Y - a.Y, c.Z - a.Z);
                    var cross = ((aby * acz) - (abz * acy), (abz * acx) - (abx * acz), (abx * acy) - (aby * acx));
                    var n = normals[a.Face];

                    Assert.True((cross.Item1 * n.X) + (cross.Item2 * n.Y) + (cross.Item3 * n.Z) > 0, $"triangle {t / 3} faces inward");
                }
            }
        }
        finally
        {
            pipeline.Dispose();
        }
    }

    [Fact]
    public void Mesher_VertexDataIsInRangeAndLightFollowsTheSky()
    {
        var chunk = new Chunk(new ChunkCoord(0, 0));
        chunk.Set(8, 20, 8, Blocks.Stone);
        chunk.RecomputeHeights();

        var mesh = Mesh(chunk);

        foreach (var vertex in mesh.Sections.SelectMany(s => s.Vertices.ToArray()))
        {
            Assert.InRange(vertex.Ao, 0, 3);
            Assert.InRange(vertex.SkyLight, 0, 15);
            Assert.InRange(vertex.BlockLight, 0, 15);
            Assert.InRange(vertex.Y, 20, 21);
        }

        var top = mesh.Sections.SelectMany(s => s.Vertices.ToArray()).Where(v => v.Face == 2).ToList();
        Assert.All(top, v => Assert.Equal(15, v.SkyLight));
        Assert.All(top, v => Assert.Equal(Blocks.Tile(Blocks.Stone, 2), v.Tile));
    }

    [Fact]
    public void Mesher_AmbientOcclusionDarkensAnInsideCorner()
    {
        var chunk = new Chunk(new ChunkCoord(0, 0));
        chunk.Set(8, 20, 8, Blocks.Stone);
        chunk.Set(9, 21, 8, Blocks.Stone);
        chunk.RecomputeHeights();

        var topFace = Mesh(chunk).Sections.SelectMany(s => s.Vertices.ToArray()).Where(v => v.Face == 2 && v.Y == 21 && v.X <= 9 && v.Z <= 9).ToList();

        Assert.Contains(topFace, v => v.Ao < 3);
    }

    [Fact]
    public void Mesher_IsDeterministic()
    {
        var pipeline = new ChunkPipeline(Generator(), workerCount: 1);
        try
        {
            var a = pipeline.Build(new ChunkCoord(1, 1));
            var b = pipeline.Build(new ChunkCoord(1, 1));

            AssertSameMesh(a, b);
        }
        finally
        {
            pipeline.Dispose();
        }
    }

    [Fact]
    public async Task Pipeline_WorkerThreadsBuildTheSameMeshesAsOneThread()
    {
        using var parallel = new ChunkPipeline(Generator(), workerCount: 4);
        using var serial = new ChunkPipeline(Generator(), workerCount: 1);
        var coords = Enumerable.Range(-2, 5).SelectMany(x => Enumerable.Range(-2, 5).Select(z => new ChunkCoord(x, z))).ToList();

        var built = await Task.WhenAll(coords.Select(parallel.RequestMeshAsync));

        Assert.Equal(4, parallel.WorkerCount);
        for (var i = 0; i < coords.Count; i++)
        {
            Assert.Equal(coords[i], built[i].Coord);
            AssertSameMesh(serial.Build(coords[i]), built[i]);
        }
    }

    [Fact]
    public void Pipeline_CachesChunksAndCanEvictThem()
    {
        using var pipeline = new ChunkPipeline(Generator(), workerCount: 1);

        var first = pipeline.GetChunk(new ChunkCoord(0, 0));

        Assert.Same(first, pipeline.GetChunk(new ChunkCoord(0, 0)));
        Assert.Equal(1, pipeline.CachedChunkCount);
        Assert.True(pipeline.Evict(new ChunkCoord(0, 0)));
        Assert.NotSame(first, pipeline.GetChunk(new ChunkCoord(0, 0)));
        Assert.Equal(first.Hash(), pipeline.GetChunk(new ChunkCoord(0, 0)).Hash());
    }

    [Fact]
    public async Task Pipeline_RefusesWorkAfterItIsDisposed()
    {
        var pipeline = new ChunkPipeline(Generator(), workerCount: 1);
        pipeline.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => pipeline.RequestMeshAsync(new ChunkCoord(0, 0)));
    }

    private static Chunk ChunkWithFloor(int y)
    {
        var chunk = new Chunk(new ChunkCoord(0, 0));
        for (var z = 0; z < ChunkConstants.Size; z++)
        {
            for (var x = 0; x < ChunkConstants.Size; x++)
            {
                chunk.Set(x, y, z, Blocks.Stone);
            }
        }

        chunk.RecomputeHeights();
        return chunk;
    }

    private static ChunkMeshSet Mesh(Chunk chunk, ChunkNeighbors? neighbors = null)
    {
        var around = neighbors ?? ChunkNeighbors.None;
        return ChunkMesher.Build(chunk, ChunkLighting.Compute(chunk, around), around);
    }

    private static void AssertSameMesh(ChunkMeshSet expected, ChunkMeshSet actual)
    {
        Assert.Equal(expected.Sections.Count, actual.Sections.Count);
        for (var i = 0; i < expected.Sections.Count; i++)
        {
            Assert.True(expected.Sections[i].Vertices.Span.SequenceEqual(actual.Sections[i].Vertices.Span), $"vertices differ in section {i}");
            Assert.True(expected.Sections[i].Indices.Span.SequenceEqual(actual.Sections[i].Indices.Span), $"indices differ in section {i}");
        }
    }

    /// <summary>Counts every face of an opaque block whose neighbor in that direction is not opaque, treating missing chunks as air.</summary>
    private static long CountVisibleFaces(Chunk chunk, ChunkNeighbors neighbors)
    {
        bool Opaque(int x, int y, int z)
        {
            if (y < 0 || y >= ChunkConstants.Height)
            {
                return false;
            }

            if (x >= 0 && x < ChunkConstants.Size && z >= 0 && z < ChunkConstants.Size)
            {
                return Blocks.IsOpaque(chunk.Get(x, y, z));
            }

            var other = x < 0 ? neighbors.NegX : x >= ChunkConstants.Size ? neighbors.PosX : z < 0 ? neighbors.NegZ : neighbors.PosZ;
            return other is not null && Blocks.IsOpaque(other.Get(
                (x + ChunkConstants.Size) % ChunkConstants.Size,
                y,
                (z + ChunkConstants.Size) % ChunkConstants.Size));
        }

        long faces = 0;
        for (var y = 0; y < ChunkConstants.Height; y++)
        {
            for (var z = 0; z < ChunkConstants.Size; z++)
            {
                for (var x = 0; x < ChunkConstants.Size; x++)
                {
                    if (!Opaque(x, y, z))
                    {
                        continue;
                    }

                    faces += (Opaque(x + 1, y, z) ? 0 : 1) + (Opaque(x - 1, y, z) ? 0 : 1)
                        + (Opaque(x, y + 1, z) ? 0 : 1) + (Opaque(x, y - 1, z) ? 0 : 1)
                        + (Opaque(x, y, z + 1) ? 0 : 1) + (Opaque(x, y, z - 1) ? 0 : 1);
                }
            }
        }

        return faces;
    }
}
