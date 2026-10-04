namespace Zombies.Engine.Voxel;

/// <summary>What the streamer wants done after the camera moved: chunks to start loading (nearest first) and chunks to drop.</summary>
public sealed record StreamUpdate(IReadOnlyList<ChunkCoord> Load, IReadOnlyList<ChunkCoord> Unload);

/// <summary>
/// Decides which chunks should exist around a moving viewer. A chunk loads when it comes within the view radius and unloads only after
/// it falls a little way outside it, so a viewer standing on a border does not make chunks flicker in and out.
/// It only keeps track of coordinates; loading, meshing, and uploading happen elsewhere.
/// </summary>
public sealed class ChunkStreamer
{
    private readonly HashSet<ChunkCoord> _requested = [];
    private readonly int _radius;
    private readonly int _unloadRadius;

    public ChunkStreamer(int viewRadius, int hysteresis = 1)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(viewRadius, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(hysteresis);
        _radius = viewRadius;
        _unloadRadius = viewRadius + hysteresis;
    }

    public int ViewRadius => _radius;

    /// <summary>Chunks currently requested and not yet dropped.</summary>
    public int RequestedCount => _requested.Count;

    public bool IsRequested(ChunkCoord coord) => _requested.Contains(coord);

    /// <summary>Chunk the world position <paramref name="x"/>, <paramref name="z"/> falls in.</summary>
    public static ChunkCoord ChunkOf(float x, float z) => new(
        (int)MathF.Floor(x / ChunkConstants.Size),
        (int)MathF.Floor(z / ChunkConstants.Size));

    public StreamUpdate Update(ChunkCoord center)
    {
        var load = new List<(ChunkCoord Coord, int Distance)>();
        for (var dz = -_radius; dz <= _radius; dz++)
        {
            for (var dx = -_radius; dx <= _radius; dx++)
            {
                var distance = (dx * dx) + (dz * dz);
                var coord = new ChunkCoord(center.X + dx, center.Z + dz);
                if (distance <= _radius * _radius && _requested.Add(coord))
                {
                    load.Add((coord, distance));
                }
            }
        }

        var unload = new List<ChunkCoord>();
        foreach (var coord in _requested)
        {
            var dx = coord.X - center.X;
            var dz = coord.Z - center.Z;
            if ((dx * dx) + (dz * dz) > _unloadRadius * _unloadRadius)
            {
                unload.Add(coord);
            }
        }

        foreach (var coord in unload)
        {
            _requested.Remove(coord);
        }

        return new StreamUpdate(
            [.. load.OrderBy(l => l.Distance).ThenBy(l => l.Coord.X).ThenBy(l => l.Coord.Z).Select(l => l.Coord)],
            [.. unload.OrderBy(c => c.X).ThenBy(c => c.Z)]);
    }
}
