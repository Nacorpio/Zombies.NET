using System.Numerics;

namespace Zombies.Engine.Net;

/// <summary>Kinds of replicated entity. Later milestones add dropped items and deployables.</summary>
public static class EntityKind
{
    public const ushort Player = 1;

    public const ushort Zombie = 2;

    /// <summary>The Corpse a dead player left, which any player can loot.</summary>
    public const ushort Corpse = 3;
}

/// <summary>
/// What a client needs to draw a zombie. The spec (<see cref="Seed"/>, <see cref="Type"/>, <see cref="Level"/>) is all it sends of
/// the look: every client derives the same proportions, skin, outfit, and starting Missing parts from it. <see cref="Missing"/>
/// is the Server's current Missing parts, one bit per body part, which grows as the Server dismembers.
/// </summary>
public readonly record struct ZombieState(ulong Seed, ushort Type, byte Level, byte Missing, bool Dead);

/// <summary>What a client needs to know of another player: whether their Body is dead and they are spectating, and whether they are asleep.</summary>
public readonly record struct PlayerState(bool Dead, bool Sleeping = false);

/// <summary>The replicated state of one entity, as the Server sends it and a client sees it.</summary>
public readonly record struct EntityState(uint Id, ushort Kind, Vector3 Position, float Yaw, ZombieState Zombie = default, PlayerState Player = default)
{
    /// <summary>Side length of a chunk in blocks, matching <c>ChunkConstants.Size</c> in Zombies.Engine.Voxel. Interest is measured in chunks.</summary>
    public const int ChunkSize = 16;

    public int ChunkX => (int)MathF.Floor(Position.X / ChunkSize);

    public int ChunkZ => (int)MathF.Floor(Position.Z / ChunkSize);
}
