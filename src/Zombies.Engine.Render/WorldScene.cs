using System.Numerics;
using Zombies.Engine.Voxel;

namespace Zombies.Engine.Render;

/// <summary>One 16-high slice of a chunk, uploaded to the GPU: where its vertices and indices live, and the box that bounds it.</summary>
public sealed record GpuSection(int VertexStart, int VertexCount, int IndexStart, int IndexCount, Vector3 BoundsMin, Vector3 BoundsMax);

/// <summary>A chunk whose mesh is on the GPU. The renderer owns the memory; release it through <see cref="IWorldRenderer.ReleaseChunk"/>.</summary>
public sealed class GpuChunk(ChunkCoord coord, IReadOnlyList<GpuSection> sections)
{
    public ChunkCoord Coord { get; } = coord;

    public IReadOnlyList<GpuSection> Sections { get; } = sections;

    /// <summary>World position of the chunk's corner, which the vertex positions are relative to.</summary>
    public Vector3 Origin => new(Coord.WorldX, 0, Coord.WorldZ);
}

/// <summary>Quality settings for sun shadows. Zero cascades turns shadows off.</summary>
public sealed record ShadowSettings(int Cascades = 3, int Resolution = 2048, float Distance = 150f)
{
    public const int MaxCascades = 3;

    public bool Enabled => Cascades > 0;
}

/// <summary>One player body to draw this frame: where it stands and which way it faces.</summary>
public sealed record BodyInstance(Vector3 Position, float Yaw, bool Crouched);

/// <summary>Everything needed to draw the world for one frame.</summary>
public sealed record WorldScene(
    Camera Camera,
    SunState Sun,
    IReadOnlyCollection<GpuChunk> Chunks,
    int ViewDistanceChunks,
    ShadowSettings Shadows)
{
    /// <summary>Remote players to draw as simple bodies. Empty when playing alone.</summary>
    public IReadOnlyList<BodyInstance> Bodies { get; init; } = [];
}

/// <summary>The part of a graphics backend that holds chunk meshes. Uploading and releasing are cheap, and must happen on the render thread.</summary>
public interface IWorldRenderer
{
    /// <summary>Copies a chunk's section meshes into GPU memory. Returns null if the chunk has nothing to draw.</summary>
    GpuChunk? UploadChunk(ChunkMeshSet mesh);

    /// <summary>Gives a chunk's GPU memory back. It stays in use for a few frames, so a frame already being drawn is never disturbed.</summary>
    void ReleaseChunk(GpuChunk chunk);

    /// <summary>Uploads the one body mesh every remote player is drawn with. Call once, before the first frame that draws bodies.</summary>
    void SetBodyMesh(BodyMesh mesh);
}
