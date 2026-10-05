using System.Numerics;
using Zombies.Domain.World;
using Zombies.Engine.Render;
using Zombies.Engine.Voxel;

namespace Zombies.Engine.Tests;

public sealed class ChunkRenderManagerTests
{
    private static readonly Biome Forest = new("base:biome/temperate_forest", ["forest"], (0, 100), (0, 100), 52, 14, 50);

    private sealed class FakeRenderer(Func<ChunkCoord, bool>? hasNothingToDraw = null) : IWorldRenderer
    {
        public List<ChunkCoord> Uploaded { get; } = [];

        public List<GpuChunk> Released { get; } = [];

        public GpuChunk? UploadChunk(ChunkMeshSet mesh)
        {
            Uploaded.Add(mesh.Coord);
            return hasNothingToDraw?.Invoke(mesh.Coord) == true ? null : new GpuChunk(mesh.Coord, []);
        }

        public void ReleaseChunk(GpuChunk chunk) => Released.Add(chunk);

        public void SetBodyMesh(BodyMesh mesh)
        {
        }
    }

    private static ChunkPipeline NewPipeline() => new(new WorldGenerator(1, new BiomeCatalog([Forest])), workerCount: 2);

    /// <summary>Calls Update until nothing is pending, giving the worker threads real time to finish.</summary>
    private static void PumpUntilIdle(ChunkRenderManager manager, Vector3 camera, int budget)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        do
        {
            manager.Update(camera, budget);
            if (manager.PendingCount == 0)
            {
                return;
            }

            Thread.Sleep(5);
        }
        while (DateTime.UtcNow < deadline);

        throw new TimeoutException("The chunk manager did not finish loading.");
    }

    [Fact]
    public void Update_LoadsEveryWantedChunkAndNeverUploadsMoreThanTheBudgetAtOnce()
    {
        using var pipeline = NewPipeline();
        var renderer = new FakeRenderer();
        var streamer = new ChunkStreamer(viewRadius: 2);
        var manager = new ChunkRenderManager(pipeline, renderer, streamer);
        var deadline = DateTime.UtcNow.AddSeconds(60);

        var largestBatch = 0;
        do
        {
            manager.Update(Vector3.Zero, uploadBudget: 2);
            largestBatch = Math.Max(largestBatch, manager.UploadedLastUpdate);
            Thread.Sleep(2);
        }
        while (manager.PendingCount > 0 && DateTime.UtcNow < deadline);

        Assert.Equal(0, manager.PendingCount);
        Assert.Equal(13, manager.LoadedCount);
        Assert.Equal(13, renderer.Uploaded.Distinct().Count());
        Assert.InRange(largestBatch, 1, 2);
        Assert.Equal(streamer.RequestedCount, manager.LoadedCount);
        Assert.Contains(manager.Chunks, c => c.Coord == new ChunkCoord(0, 0));
    }

    [Fact]
    public void Update_UnloadsAndReleasesChunksOnceTheCameraMovesAway()
    {
        using var pipeline = NewPipeline();
        var renderer = new FakeRenderer();
        var manager = new ChunkRenderManager(pipeline, renderer, new ChunkStreamer(viewRadius: 2));
        PumpUntilIdle(manager, Vector3.Zero, 64);
        Assert.Empty(renderer.Released);

        PumpUntilIdle(manager, new Vector3(16 * 40, 0, 0), 64);

        Assert.DoesNotContain(manager.Chunks, c => c.Coord == new ChunkCoord(0, 0));
        Assert.Contains(renderer.Released, c => c.Coord == new ChunkCoord(0, 0));
        Assert.Equal(13, manager.LoadedCount);
        Assert.Equal(13, renderer.Released.Count);
        Assert.Contains(manager.Chunks, c => c.Coord == new ChunkCoord(40, 0));
    }

    [Fact]
    public void Update_ChunksWithNothingToDrawAreNotKeptOrPending()
    {
        using var pipeline = NewPipeline();
        var renderer = new FakeRenderer(hasNothingToDraw: coord => coord.X == 0);
        var manager = new ChunkRenderManager(pipeline, renderer, new ChunkStreamer(viewRadius: 2));

        PumpUntilIdle(manager, Vector3.Zero, 64);

        Assert.DoesNotContain(manager.Chunks, c => c.Coord.X == 0);
        Assert.Equal(13, renderer.Uploaded.Count);
        Assert.Equal(13 - 5, manager.LoadedCount);
        Assert.Equal(0, manager.PendingCount);
    }

    [Fact]
    public void Update_StaysQuietOnceTheWorldIsLoadedAndTheCameraIsStill()
    {
        using var pipeline = NewPipeline();
        var renderer = new FakeRenderer();
        var manager = new ChunkRenderManager(pipeline, renderer, new ChunkStreamer(viewRadius: 2));
        PumpUntilIdle(manager, Vector3.Zero, 64);
        var uploads = renderer.Uploaded.Count;

        for (var i = 0; i < 20; i++)
        {
            manager.Update(Vector3.Zero, 64);
        }

        Assert.Equal(uploads, renderer.Uploaded.Count);
        Assert.Empty(renderer.Released);
        Assert.Equal(0, manager.UploadedLastUpdate);
    }

    [Fact]
    public void Update_RejectsAnUploadBudgetOfZero()
    {
        using var pipeline = NewPipeline();
        var manager = new ChunkRenderManager(pipeline, new FakeRenderer(), new ChunkStreamer(viewRadius: 1));

        Assert.Throws<ArgumentOutOfRangeException>(() => manager.Update(Vector3.Zero, 0));
    }

    [Fact]
    public void GpuChunk_ReportsItsWorldOrigin()
    {
        var chunk = new GpuChunk(new ChunkCoord(-2, 3), []);

        Assert.Equal(new Vector3(-32, 0, 48), chunk.Origin);
        Assert.True(new ShadowSettings().Enabled);
        Assert.False(new ShadowSettings(Cascades: 0).Enabled);
    }
}
