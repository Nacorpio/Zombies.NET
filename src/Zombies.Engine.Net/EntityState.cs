using System.Numerics;

namespace Zombies.Engine.Net;

/// <summary>Kinds of replicated entity. Later milestones add zombies, dropped items, and deployables.</summary>
public static class EntityKind
{
    public const ushort Player = 1;
}

/// <summary>The replicated state of one entity, as the Server sends it and a client sees it.</summary>
public readonly record struct EntityState(uint Id, ushort Kind, Vector3 Position, float Yaw)
{
    /// <summary>Side length of a chunk in blocks, matching <c>ChunkConstants.Size</c> in Zombies.Engine.Voxel. Interest is measured in chunks.</summary>
    public const int ChunkSize = 16;

    public int ChunkX => (int)MathF.Floor(Position.X / ChunkSize);

    public int ChunkZ => (int)MathF.Floor(Position.Z / ChunkSize);
}
