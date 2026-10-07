using System.Numerics;

namespace Zombies.Engine.Ai;

/// <summary>How a kind of creature gets around. The defaults are a zombie's.</summary>
public sealed record NavigationOptions
{
    /// <summary>
    /// The highest ledge, in blocks, a creature walks up without climbing. A zombie steps up one block, the height a player can jump,
    /// and never climbs: a wall of two blocks stops it, and it cannot reach a roof or scale a tree.
    /// </summary>
    public int StepUp { get; init; } = 1;

    /// <summary>The deepest drop, in blocks, a creature walks off.</summary>
    public int MaxDrop { get; init; } = 3;

    /// <summary>How many blocks of headroom a creature needs.</summary>
    public int Height { get; init; } = 2;

    /// <summary>Side of the square clusters the coarse level of the hierarchical search plans across, in blocks. A chunk by default.</summary>
    public int ClusterSize { get; init; } = 16;

    /// <summary>The lowest and highest Y a creature can stand at.</summary>
    public int MinY { get; init; } = 1;

    public int MaxY { get; init; } = 126;

    /// <summary>What passing a closed door adds to a route, in tenths of a block, for the time it takes to break it.</summary>
    public int ClosedDoorCost { get; init; } = 120;
}

/// <summary>One move a creature can make from a cell: where it ends, and what it costs in tenths of a block.</summary>
public readonly record struct NavStep(NavCell Cell, int Cost);

/// <summary>
/// The walkable world: which cells a creature can stand in and which moves between neighbouring cells it can make. A move goes to one
/// of the eight neighbouring columns, up at most <see cref="NavigationOptions.StepUp"/> blocks or down at most
/// <see cref="NavigationOptions.MaxDrop"/>. A diagonal move stays level and never cuts a corner. A closed door is a wall to movement and to
/// sight, but routes may pass it at <see cref="NavigationOptions.ClosedDoorCost"/>, because a zombie can break it.
/// </summary>
public sealed class Navigation
{
    public const int StraightCost = 10;
    public const int DiagonalCost = 14;

    /// <summary>The most steps <see cref="Neighbors"/> or <see cref="Predecessors"/> can write.</summary>
    public const int MaxSteps = 64;

    private static readonly (int X, int Z)[] Directions = [(1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (1, -1), (-1, 1), (-1, -1)];

    public Navigation(INavigationTerrain terrain, DoorMap? doors = null, NavigationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(terrain);
        Terrain = terrain;
        Doors = doors ?? new DoorMap();
        Options = options ?? new NavigationOptions();
        if (Options.StepUp < 0 || Options.MaxDrop < 0 || Options.Height < 1 || Options.ClusterSize < 4 || Options.MinY < 1 || Options.MinY > Options.MaxY || Options.ClosedDoorCost < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Navigation needs a step and drop of zero or more, headroom, clusters of at least 4 blocks, and a Y range.");
        }
    }

    public INavigationTerrain Terrain { get; }

    public DoorMap Doors { get; }

    public NavigationOptions Options { get; }

    /// <summary>Whether a creature fits in the cell, standing on solid ground. Doors do not count; see <see cref="IsBlockedByDoor"/>.</summary>
    public bool CanStand(NavCell cell)
    {
        if (cell.Y < Options.MinY || cell.Y > Options.MaxY || !Terrain.IsSolid(cell.X, cell.Y - 1, cell.Z))
        {
            return false;
        }

        for (var dy = 0; dy < Options.Height; dy++)
        {
            if (Terrain.IsSolid(cell.X, cell.Y + dy, cell.Z))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Whether a closed door fills any of the blocks a creature in the cell would.</summary>
    public bool IsBlockedByDoor(NavCell cell)
    {
        if (Doors.Count == 0)
        {
            return false;
        }

        for (var dy = 0; dy < Options.Height; dy++)
        {
            if (Doors.IsClosedAt(cell.X, cell.Y + dy, cell.Z))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether the block stops a creature: solid terrain or a closed door.</summary>
    public bool IsWall(int x, int y, int z) => Terrain.IsSolid(x, y, z) || (Doors.Count > 0 && Doors.IsClosedAt(x, y, z));

    /// <summary>
    /// Whether a creature standing in <paramref name="from"/> can walk into the neighbouring <paramref name="to"/>, ignoring doors, and
    /// what it costs. Both cells must be standable.
    /// </summary>
    public bool TryMoveCost(NavCell from, NavCell to, out int cost) => TryMoveCost(from, to, checkTarget: true, out cost);

    private bool TryMoveCost(NavCell from, NavCell to, bool checkTarget, out int cost)
    {
        cost = 0;
        var dx = to.X - from.X;
        var dz = to.Z - from.Z;
        var dy = to.Y - from.Y;
        if (Math.Abs(dx) > 1 || Math.Abs(dz) > 1 || (dx == 0 && dz == 0) || dy > Options.StepUp || -dy > Options.MaxDrop || (checkTarget && !CanStand(to)))
        {
            return false;
        }

        var diagonal = dx != 0 && dz != 0;
        if (diagonal)
        {
            // Level only, and both cells beside the corner must be open, so a creature never cuts through the edge of a wall.
            if (dy != 0)
            {
                return false;
            }

            for (var h = 0; h < Options.Height; h++)
            {
                if (IsWall(from.X + dx, from.Y + h, from.Z) || IsWall(from.X, from.Y + h, from.Z + dz))
                {
                    return false;
                }
            }

            cost = DiagonalCost;
            return true;
        }

        if (dy > 0)
        {
            // Stepping up needs room above the head before moving over.
            for (var h = Options.Height; h < Options.Height + dy; h++)
            {
                if (Terrain.IsSolid(from.X, from.Y + h, from.Z))
                {
                    return false;
                }
            }

            cost = StraightCost + (4 * dy);
            return true;
        }

        if (dy < 0)
        {
            // Walking off a ledge falls through the open column below the edge.
            for (var y = to.Y + Options.Height; y < from.Y + Options.Height; y++)
            {
                if (Terrain.IsSolid(to.X, y, to.Z))
                {
                    return false;
                }
            }
        }

        cost = StraightCost;
        return true;
    }

    /// <summary>What a move into <paramref name="to"/> costs a route, including breaking a closed door in it.</summary>
    public int RouteCost(int moveCost, NavCell to) => IsBlockedByDoor(to) ? moveCost + Options.ClosedDoorCost : moveCost;

    /// <summary>Writes every move a creature in <paramref name="cell"/> can make, with route costs. Returns how many it wrote.</summary>
    public int Neighbors(NavCell cell, Span<NavStep> into)
    {
        var count = 0;
        foreach (var (dx, dz) in Directions)
        {
            // Prefer level ground, then a step up, then the shortest drop, and take one move per column.
            for (var i = 0; i <= Options.StepUp + Options.MaxDrop; i++)
            {
                var dy = i <= Options.StepUp ? i : Options.StepUp - i;
                var to = new NavCell(cell.X + dx, cell.Y + dy, cell.Z + dz);
                if (TryMoveCost(cell, to, out var cost))
                {
                    into[count++] = new NavStep(to, RouteCost(cost, to));
                    break;
                }
            }
        }

        return count;
    }

    /// <summary>Writes every cell from which a creature can move into the standable <paramref name="cell"/>, with route costs. Returns how many it wrote.</summary>
    public int Predecessors(NavCell cell, Span<NavStep> into)
    {
        var count = 0;
        var cost = RouteCost(0, cell);
        foreach (var (dx, dz) in Directions)
        {
            for (var dy = -Options.StepUp; dy <= Options.MaxDrop; dy++)
            {
                var from = new NavCell(cell.X + dx, cell.Y + dy, cell.Z + dz);
                if (CanStand(from) && TryMoveCost(from, cell, checkTarget: false, out var move) && count < into.Length)
                {
                    into[count++] = new NavStep(from, move + cost);
                }
            }
        }

        return count;
    }

    /// <summary>
    /// The standable cell a creature at <paramref name="position"/> is in: the cell holding its feet, or the nearest standable one in the
    /// same column a little above or below. False when there is none, as in mid-air or inside a wall.
    /// </summary>
    public bool TrySnap(Vector3 position, out NavCell cell)
    {
        var at = NavCell.Containing(position);
        for (var i = 0; i <= 4; i++)
        {
            cell = at with { Y = at.Y - i };
            if (CanStand(cell))
            {
                return true;
            }

            cell = at with { Y = at.Y + i + 1 };
            if (i < 1 && CanStand(cell))
            {
                return true;
            }
        }

        cell = at;
        return false;
    }

    /// <summary>The cluster of the coarse search that holds a cell.</summary>
    public (int X, int Z) ClusterOf(NavCell cell) =>
        ((int)MathF.Floor(cell.X / (float)Options.ClusterSize), (int)MathF.Floor(cell.Z / (float)Options.ClusterSize));

    /// <summary>
    /// Whether nothing solid and no closed door stands on the straight line between two points, walking the blocks it passes through.
    /// </summary>
    public bool HasLineOfSight(Vector3 from, Vector3 to)
    {
        var delta = to - from;
        var length = delta.Length();
        if (length < 1e-4f)
        {
            return true;
        }

        var direction = delta / length;
        int x = (int)MathF.Floor(from.X), y = (int)MathF.Floor(from.Y), z = (int)MathF.Floor(from.Z);
        int endX = (int)MathF.Floor(to.X), endY = (int)MathF.Floor(to.Y), endZ = (int)MathF.Floor(to.Z);
        int stepX = Math.Sign(direction.X), stepY = Math.Sign(direction.Y), stepZ = Math.Sign(direction.Z);
        var tMaxX = Boundary(from.X, direction.X, x);
        var tMaxY = Boundary(from.Y, direction.Y, y);
        var tMaxZ = Boundary(from.Z, direction.Z, z);
        var tDeltaX = direction.X == 0 ? float.PositiveInfinity : MathF.Abs(1f / direction.X);
        var tDeltaY = direction.Y == 0 ? float.PositiveInfinity : MathF.Abs(1f / direction.Y);
        var tDeltaZ = direction.Z == 0 ? float.PositiveInfinity : MathF.Abs(1f / direction.Z);
        var limit = (Math.Abs(endX - x) + Math.Abs(endY - y) + Math.Abs(endZ - z)) + 1;
        for (var i = 0; i < limit; i++)
        {
            if (x == endX && y == endY && z == endZ)
            {
                return true;
            }

            if (tMaxX < tMaxY && tMaxX < tMaxZ)
            {
                x += stepX;
                tMaxX += tDeltaX;
            }
            else if (tMaxY < tMaxZ)
            {
                y += stepY;
                tMaxY += tDeltaY;
            }
            else
            {
                z += stepZ;
                tMaxZ += tDeltaZ;
            }

            if (IsWall(x, y, z) && !(x == endX && y == endY && z == endZ))
            {
                return false;
            }
        }

        return true;

        static float Boundary(float start, float direction, int cell) =>
            direction > 0 ? (cell + 1 - start) / direction : direction < 0 ? (start - cell) / -direction : float.PositiveInfinity;
    }
}
