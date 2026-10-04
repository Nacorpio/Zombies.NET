using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Zombies.Engine.Voxel;

/// <summary>
/// Generates chunks on demand and builds their light and mesh on a fixed pool of worker threads, so the main thread never waits.
/// A chunk is generated once and cached; building a mesh also needs its four edge neighbors. The result for a chunk is the same
/// whether it is built alone, in a crowd, or on one thread.
/// </summary>
public sealed class ChunkPipeline : IDisposable
{
    private readonly WorldGenerator _generator;
    private readonly ConcurrentDictionary<ChunkCoord, Lazy<Chunk>> _chunks = new();
    private readonly Channel<(ChunkCoord Coord, TaskCompletionSource<ChunkMeshSet> Result)> _jobs =
        Channel.CreateUnbounded<(ChunkCoord, TaskCompletionSource<ChunkMeshSet>)>();
    private readonly Thread[] _workers;

    public ChunkPipeline(WorldGenerator generator, int workerCount = 0)
    {
        ArgumentNullException.ThrowIfNull(generator);
        _generator = generator;
        var count = workerCount > 0 ? workerCount : Math.Max(1, Environment.ProcessorCount - 1);
        _workers = new Thread[count];
        for (var i = 0; i < count; i++)
        {
            _workers[i] = new Thread(Work) { IsBackground = true, Name = $"chunk-worker-{i}" };
            _workers[i].Start();
        }
    }

    public int WorkerCount => _workers.Length;

    public int CachedChunkCount => _chunks.Count;

    /// <summary>Returns the chunk, generating it first if no one has yet. Safe to call from any thread.</summary>
    public Chunk GetChunk(ChunkCoord coord) =>
        _chunks.GetOrAdd(coord, c => new Lazy<Chunk>(() => _generator.Generate(c), LazyThreadSafetyMode.ExecutionAndPublication)).Value;

    /// <summary>Queues a light and mesh build on a worker thread.</summary>
    public Task<ChunkMeshSet> RequestMeshAsync(ChunkCoord coord)
    {
        var result = new TaskCompletionSource<ChunkMeshSet>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_jobs.Writer.TryWrite((coord, result)))
        {
            result.SetException(new ObjectDisposedException(nameof(ChunkPipeline)));
        }

        return result.Task;
    }

    /// <summary>Drops a chunk from the cache, such as one that is far behind the player.</summary>
    public bool Evict(ChunkCoord coord) => _chunks.TryRemove(coord, out _);

    public void Dispose()
    {
        _jobs.Writer.TryComplete();
        foreach (var worker in _workers)
        {
            worker.Join();
        }
    }

    /// <summary>Builds light and mesh for a chunk on the calling thread. The workers call this too.</summary>
    public ChunkMeshSet Build(ChunkCoord coord)
    {
        var chunk = GetChunk(coord);
        var neighbors = NeighborsOf(coord);
        var light = ChunkLighting.Compute(chunk, neighbors);
        return ChunkMesher.Build(chunk, light, neighbors);
    }

    public ChunkNeighbors NeighborsOf(ChunkCoord coord) => new(
        GetChunk(new ChunkCoord(coord.X + 1, coord.Z)),
        GetChunk(new ChunkCoord(coord.X - 1, coord.Z)),
        GetChunk(new ChunkCoord(coord.X, coord.Z + 1)),
        GetChunk(new ChunkCoord(coord.X, coord.Z - 1)));

    private void Work()
    {
        var reader = _jobs.Reader;
        while (reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
        {
            while (reader.TryRead(out var job))
            {
                try
                {
                    job.Result.SetResult(Build(job.Coord));
                }
                catch (Exception ex)
                {
                    job.Result.SetException(ex);
                }
            }
        }
    }
}
