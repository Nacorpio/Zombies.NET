namespace Zombies.Engine.Ai;

/// <summary>A search the <see cref="PathPlanner"/> runs a slice of at a time, inside its per-tick budget.</summary>
public abstract class NavJob
{
    private protected NavJob()
    {
    }

    /// <summary>Whether the job waits in the planner's queue or is being searched.</summary>
    public bool IsQueued { get; internal set; }

    internal bool IsCancelled { get; set; }
}

public enum PathStatus
{
    /// <summary>Never requested.</summary>
    None,

    /// <summary>Waiting for the planner.</summary>
    Pending,

    /// <summary>Found: the cells lead to the goal, or as near to it as the search got when <see cref="NavPath.IsPartial"/>.</summary>
    Ready,

    /// <summary>No move at all leads toward the goal.</summary>
    Failed,
}

/// <summary>
/// One creature's route: the cells to walk through in order, not counting the one it starts in. A long route holds only its first
/// <see cref="PathPlannerOptions.MaxPathLength"/> cells; the creature asks again when it gets there.
/// </summary>
public sealed class NavPath : NavJob
{
    internal NavPath(int capacity) => Cells = new NavCell[capacity];

    public PathStatus Status { get; internal set; }

    public NavCell Start { get; internal set; }

    public NavCell Goal { get; internal set; }

    /// <summary>Whether the route stops short of the goal, because the goal could not be reached or the search ran out of nodes.</summary>
    public bool IsPartial { get; internal set; }

    /// <summary>How many cells the route has.</summary>
    public int Count { get; internal set; }

    /// <summary>The index of the next cell to walk to.</summary>
    public int Next { get; private set; }

    /// <summary>Whether cells of the route remain to be walked.</summary>
    public bool HasNext => Status == PathStatus.Ready && Next < Count;

    /// <summary>The last cell of the route, which is the goal unless the route is partial.</summary>
    public NavCell End => Count > 0 ? Cells[Count - 1] : Start;

    internal NavCell[] Cells { get; }

    public NavCell PeekNext() => Cells[Next];

    /// <summary>Marks the next cell as reached.</summary>
    public void Advance() => Next++;

    /// <summary>Forgets the route, so the creature plans again.</summary>
    public void Clear()
    {
        Status = PathStatus.None;
        Count = 0;
        Next = 0;
        IsPartial = false;
    }

    internal void Reset(NavCell start, NavCell goal)
    {
        Start = start;
        Goal = goal;
        Count = 0;
        Next = 0;
        IsPartial = false;
        Status = PathStatus.Pending;
    }

    internal void Finish(ReadOnlySpan<long> cellsFromStart, bool partial)
    {
        var count = Math.Min(cellsFromStart.Length, Cells.Length);
        for (var i = 0; i < count; i++)
        {
            Cells[i] = NavCell.FromKey(cellsFromStart[i]);
        }

        Count = count;
        Next = 0;
        IsPartial = partial || count < cellsFromStart.Length;
        Status = PathStatus.Ready;
    }

    internal void Fail()
    {
        Count = 0;
        Next = 0;
        Status = PathStatus.Failed;
    }
}

/// <summary>
/// A flow field: for every cell within reach of one target, the neighbouring cell that is one step nearer to it. However many creatures
/// head for the target, they share one search, which is what lets a horde move without each member planning its own route. While a
/// field is rebuilt for a moved target, creatures keep following the last complete one.
/// </summary>
public sealed class FlowField : NavJob
{
    internal FlowField(int capacity)
    {
        Front = new Dictionary<long, FlowEntry>(capacity);
        Back = new Dictionary<long, FlowEntry>(capacity);
    }

    /// <summary>The target the creatures following the field reach.</summary>
    public NavCell Target { get; private set; }

    /// <summary>Whether a complete field is there to follow.</summary>
    public bool IsReady { get; private set; }

    /// <summary>How many times the field has been built, so a follower can tell it changed.</summary>
    public int Version { get; private set; }

    /// <summary>How many cells the complete field covers.</summary>
    public int CellCount => Front.Count;

    /// <summary>The target the field is being built, or will next be built, for.</summary>
    public NavCell RequestedTarget { get; internal set; }

    internal Dictionary<long, FlowEntry> Front { get; private set; }

    internal Dictionary<long, FlowEntry> Back { get; private set; }

    internal NavCell Building { get; set; }

    /// <summary>How far, as a route cost, the field asked for reaches.</summary>
    internal int RequestedReach { get; set; }

    /// <summary>How far the field being built reaches.</summary>
    internal int Reach { get; set; }

    /// <summary>The cell to step into from <paramref name="from"/>, toward the target. False when the field does not cover the cell or it is the target.</summary>
    public bool TryGetNext(NavCell from, out NavCell next)
    {
        if (IsReady && Front.TryGetValue(from.Key, out var entry) && entry.Next != from.Key)
        {
            next = NavCell.FromKey(entry.Next);
            return true;
        }

        next = default;
        return false;
    }

    /// <summary>Whether the complete field reaches the cell.</summary>
    public bool Covers(NavCell cell) => IsReady && Front.ContainsKey(cell.Key);

    /// <summary>The route cost, in tenths of a block, from the cell to the target, or -1 when the field does not reach it.</summary>
    public int CostAt(NavCell cell) => IsReady && Front.TryGetValue(cell.Key, out var entry) ? entry.Cost : -1;

    /// <summary>Drops the complete field, so a pooled field never leads a new horde toward an old target.</summary>
    internal void Invalidate()
    {
        IsReady = false;
        Front.Clear();
    }

    internal void Swap()
    {
        (Front, Back) = (Back, Front);
        Back.Clear();
        Target = Building;
        IsReady = true;
        Version++;
    }

    internal struct FlowEntry
    {
        public int Cost;
        public long Next;
        public bool Closed;
    }
}
