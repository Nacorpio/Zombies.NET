using System.Numerics;
using Zombies.Engine.Voxel;

namespace Zombies.Engine.Render;

/// <summary>
/// Keeps the right chunks on the GPU around a moving camera. It asks the streamer which chunks are wanted, has worker threads
/// build their meshes, and uploads finished meshes a few per frame so one burst of loading never stalls a frame.
/// Call <see cref="Update"/> once per frame from the render thread.
/// </summary>
public sealed class ChunkRenderManager(ChunkPipeline pipeline, IWorldRenderer renderer, ChunkStreamer streamer)
{
    private readonly Dictionary<ChunkCoord, Task<ChunkMeshSet>> _pending = [];
    private readonly Dictionary<ChunkCoord, GpuChunk> _loaded = [];
    private readonly List<ChunkCoord> _ready = [];

    /// <summary>Chunks drawn from the GPU now.</summary>
    public IReadOnlyCollection<GpuChunk> Chunks => _loaded.Values;

    public int LoadedCount => _loaded.Count;

    /// <summary>Chunks asked for whose meshes are still being built or waiting to be uploaded.</summary>
    public int PendingCount => _pending.Count;

    /// <summary>Chunks uploaded during the last <see cref="Update"/>.</summary>
    public int UploadedLastUpdate { get; private set; }

    public ChunkStreamer Streamer => streamer;

    public void Update(Vector3 cameraPosition, int uploadBudget)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(uploadBudget, 1);

        var center = ChunkStreamer.ChunkOf(cameraPosition.X, cameraPosition.Z);
        var update = streamer.Update(center);

        foreach (var coord in update.Unload)
        {
            _pending.Remove(coord);
            if (_loaded.Remove(coord, out var chunk))
            {
                renderer.ReleaseChunk(chunk);
            }

            pipeline.Evict(coord);
        }

        foreach (var coord in update.Load)
        {
            _pending[coord] = pipeline.RequestMeshAsync(coord);
        }

        UploadFinished(center, uploadBudget);
    }

    private void UploadFinished(ChunkCoord center, int budget)
    {
        _ready.Clear();
        foreach (var (coord, task) in _pending)
        {
            if (task.IsCompleted)
            {
                _ready.Add(coord);
            }
        }

        // Nearest chunks first, so what the player sees fills in before what is far away.
        _ready.Sort((a, b) => Distance(a, center).CompareTo(Distance(b, center)));

        UploadedLastUpdate = 0;
        foreach (var coord in _ready)
        {
            if (UploadedLastUpdate >= budget)
            {
                break;
            }

            var task = _pending[coord];
            _pending.Remove(coord);
            var mesh = task.GetAwaiter().GetResult();
            var chunk = renderer.UploadChunk(mesh);
            if (chunk is not null)
            {
                _loaded[coord] = chunk;
            }

            UploadedLastUpdate++;
        }
    }

    private static long Distance(ChunkCoord a, ChunkCoord b)
    {
        long dx = a.X - b.X;
        long dz = a.Z - b.Z;
        return (dx * dx) + (dz * dz);
    }
}
