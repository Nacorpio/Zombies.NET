using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Zombies.Engine.Ai;

/// <summary>How much the planner may search.</summary>
public sealed record PathPlannerOptions
{
    /// <summary>The most cells one route search expands before it settles for the cell nearest the goal.</summary>
    public int MaxNodesPerSearch { get; init; } = 3000;

    /// <summary>The most cells a route holds.</summary>
    public int MaxPathLength { get; init; } = 256;

    /// <summary>How many clusters around the coarse route the fine search may also use.</summary>
    public int CorridorMargin { get; init; }

    /// <summary>The most regions the coarse search expands.</summary>
    public int MaxClusterExpansions { get; init; } = 1024;

    /// <summary>The most cells one flow field covers.</summary>
    public int MaxFlowFieldCells { get; init; } = 12_000;

    /// <summary>How far a flow field reaches from its target, as a route cost in tenths of a block.</summary>
    public int MaxFlowFieldCost { get; init; } = 640;
}

/// <summary>
/// Plans routes and builds flow fields inside a time budget per tick. A route uses hierarchical A*: a coarse A* over the regions of
/// chunk-sized clusters (see <see cref="ClusterMap"/>) finds the clusters a route passes through, and a fine A* over cells stays inside
/// that corridor, so a long route never floods the whole world. A goal out of reach yields a partial route toward the region and
/// then the cell nearest to it. Searches are resumable: one that outlasts the budget continues on
/// the next tick, and requests wait in a queue. A steady state of searching allocates nothing.
/// </summary>
public sealed class PathPlanner
{
    private const int ExpansionsPerSlice = 64;

    private readonly Navigation _navigation;
    private readonly PathPlannerOptions _options;
    private readonly Queue<NavJob> _queue = new();
    private readonly NavStep[] _steps = new NavStep[Navigation.MaxSteps];

    // The fine search, shared by every route since one runs at a time.
    private readonly PriorityQueue<long, int> _open;
    private readonly Dictionary<long, Node> _nodes;
    private readonly HashSet<long> _corridor = [];
    private readonly long[] _trace;

    // The coarse search over the regions of clusters, which are analysed once and kept.
    private readonly ClusterMap _clusters;
    private readonly PriorityQueue<long, int> _clusterOpen = new();
    private readonly Dictionary<long, Node> _clusterNodes = [];

    private NavJob? _current;
    private int _terrainVersion;
    private int _searchTerrainVersion;
    private bool _coarse;
    private (int X, int Z) _goalCluster;
    private bool _hasGoalRegion;
    private int _goalRegion;
    private int _startRegion;
    private int _bestRegion;
    private int _bestRegionH;
    private int _coarseExpanded;
    private bool _corridorReachesGoal;
    private long _goalKey;
    private bool _restricted;
    private long _bestKey;
    private int _bestH;
    private int _expanded;

    public PathPlanner(Navigation navigation, PathPlannerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(navigation);
        _navigation = navigation;
        _options = options ?? new PathPlannerOptions();
        if (_options.MaxNodesPerSearch < 1 || _options.MaxPathLength < 1 || _options.CorridorMargin < 0 || _options.MaxFlowFieldCells < 1 || _options.MaxFlowFieldCost < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The planner needs room for at least one node, one cell, and some reach.");
        }

        // Sized for the largest search up front, so searching never grows them during play.
        _trace = new long[_options.MaxNodesPerSearch + 1];
        _nodes = new Dictionary<long, Node>(_options.MaxNodesPerSearch * 8);
        _open = new PriorityQueue<long, int>(Math.Max(_options.MaxNodesPerSearch, _options.MaxFlowFieldCells) * 8);
        _clusters = new ClusterMap(navigation);
    }

    public Navigation Navigation => _navigation;

    /// <summary>Searches waiting, counting one in progress.</summary>
    public int Pending => _queue.Count + (_current is null ? 0 : 1);

    /// <summary>Cells expanded during the last <see cref="Run(TimeSpan, int)"/>.</summary>
    public int LastRunExpansions { get; private set; }

    /// <summary>Time spent in the last <see cref="Run(TimeSpan, int)"/>.</summary>
    public TimeSpan LastRunTime { get; private set; }

    /// <summary>Routes and fields finished since the planner was made.</summary>
    public long Completed { get; private set; }

    /// <summary>A route for one creature to keep and request again. It allocates; make one per creature, not per request.</summary>
    public NavPath CreatePath() => new(_options.MaxPathLength);

    /// <summary>A flow field to keep and retarget, sized for the largest field. It allocates; keep a pool of them.</summary>
    public FlowField CreateFlowField() => new(_options.MaxFlowFieldCells + Navigation.MaxSteps);

    /// <summary>Asks for a route. A route already waiting takes the new ends; one being searched finishes for its old ends.</summary>
    public void Request(NavPath path, NavCell start, NavCell goal)
    {
        ArgumentNullException.ThrowIfNull(path);
        path.IsCancelled = false;
        if (ReferenceEquals(path, _current))
        {
            return;
        }

        path.Reset(start, goal);
        Enqueue(path);
    }

    /// <summary>
    /// Asks for a field toward <paramref name="target"/> that reaches as far as a route cost of <paramref name="reach"/> tenths of a
    /// block, held to <see cref="PathPlannerOptions.MaxFlowFieldCost"/>. A field being built for another target builds again when it finishes.
    /// </summary>
    public void Request(FlowField field, NavCell target, int reach = int.MaxValue)
    {
        ArgumentNullException.ThrowIfNull(field);
        var wasCancelled = field.IsCancelled;
        field.IsCancelled = false;
        field.RequestedTarget = target;
        field.RequestedReach = Math.Clamp(reach, Navigation.StraightCost, _options.MaxFlowFieldCost);
        if (!field.IsQueued)
        {
            field.Building = target;
            Enqueue(field);
        }
        else if (wasCancelled)
        {
            // The build waiting or under way was dropped, so it must never be published: build for the new target instead.
            field.Building = target;
            if (ReferenceEquals(field, _current))
            {
                BeginField(field);
            }
        }
    }

    /// <summary>Drops a waiting or running search, as when the creature that wanted it is gone.</summary>
    public void Cancel(NavJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (job.IsQueued || ReferenceEquals(job, _current))
        {
            job.IsCancelled = true;
        }
    }

    /// <summary>
    /// Forgets which clusters link to which, for when chunks are loaded or unloaded. A route or field already found is not changed;
    /// a route being searched starts again, since the regions it was walking are gone.
    /// </summary>
    public void TerrainChanged()
    {
        _clusters.Clear();
        _terrainVersion++;
    }

    /// <summary>
    /// Analyses the clusters that cover the blocks from <paramref name="min"/> to <paramref name="max"/> now, as a Server does when it
    /// loads chunks, so the first searches there neither pay for it nor allocate during play.
    /// </summary>
    public void Prepare(NavCell min, NavCell max) => _clusters.Prepare(_navigation.ClusterOf(min), _navigation.ClusterOf(max));

    /// <summary>
    /// Searches until the queue is empty, <paramref name="budget"/> has passed, or <paramref name="maxExpansions"/> cells have been
    /// expanded, whichever comes first. Returns how many cells it expanded.
    /// </summary>
    public int Run(TimeSpan budget, int maxExpansions = int.MaxValue)
    {
        var started = Stopwatch.GetTimestamp();
        var deadline = started + (long)(budget.TotalSeconds * Stopwatch.Frequency);
        var expansions = 0;
        while (expansions < maxExpansions)
        {
            if (_current is null && !TryBeginNext())
            {
                break;
            }

            var slice = Math.Min(ExpansionsPerSlice, maxExpansions - expansions);
            var used = _current is NavPath path ? StepPath(path, slice) : StepField((FlowField)_current!, slice);
            expansions += used;
            if (Stopwatch.GetTimestamp() >= deadline)
            {
                break;
            }
        }

        LastRunExpansions = expansions;
        LastRunTime = Stopwatch.GetElapsedTime(started);
        return expansions;
    }

    private void Enqueue(NavJob job)
    {
        if (!job.IsQueued)
        {
            job.IsQueued = true;
            _queue.Enqueue(job);
        }
    }

    private bool TryBeginNext()
    {
        while (_queue.TryDequeue(out var job))
        {
            if (job.IsCancelled)
            {
                job.IsQueued = false;
                job.IsCancelled = false;
                continue;
            }

            _current = job;
            if (job is NavPath path)
            {
                BeginPath(path);
            }
            else
            {
                BeginField((FlowField)job);
            }

            return true;
        }

        return false;
    }

    private void Finish(NavJob job)
    {
        job.IsQueued = false;
        _current = null;
        Completed++;
    }

    private static int Heuristic(NavCell a, NavCell b)
    {
        var dx = Math.Abs(a.X - b.X);
        var dz = Math.Abs(a.Z - b.Z);
        return (Navigation.StraightCost * Math.Max(dx, dz)) + ((Navigation.DiagonalCost - Navigation.StraightCost) * Math.Min(dx, dz));
    }

    private void BeginPath(NavPath path)
    {
        _searchTerrainVersion = _terrainVersion;
        _open.Clear();
        _nodes.Clear();
        _corridor.Clear();
        _expanded = 0;
        _goalKey = path.Goal.Key;
        _restricted = false;
        _coarse = BeginCoarse(path.Start, path.Goal);
        if (!_coarse)
        {
            SeedPath(path);
        }
    }

    private void SeedPath(NavPath path)
    {
        _open.Clear();
        _nodes.Clear();
        _expanded = 0;
        var startKey = path.Start.Key;
        _nodes[startKey] = new Node { G = 0, Parent = startKey };
        _bestKey = startKey;
        _bestH = Heuristic(path.Start, path.Goal);
        _open.Enqueue(startKey, _bestH);
    }

    private int StepPath(NavPath path, int budget)
    {
        if (path.IsCancelled)
        {
            path.IsCancelled = false;
            Finish(path);
            return 0;
        }

        if (_searchTerrainVersion != _terrainVersion)
        {
            BeginPath(path);
        }

        var used = 0;
        if (_coarse)
        {
            used = StepCoarse(budget, out var corridorReady);
            if (!corridorReady)
            {
                return used;
            }

            _coarse = false;
            _restricted = true;
            SeedPath(path);
        }

        var goal = path.Goal;
        while (used < budget)
        {
            if (!_open.TryDequeue(out var key, out _))
            {
                if (_restricted && _corridorReachesGoal)
                {
                    // The regions said the goal is reachable through the corridor, so this should not happen; search without it to be sure.
                    _restricted = false;
                    SeedPath(path);
                    continue;
                }

                CompletePath(path, _bestKey, partial: true);
                return used;
            }

            ref var node = ref CollectionsMarshal.GetValueRefOrNullRef(_nodes, key);
            if (node.Closed)
            {
                continue;
            }

            node.Closed = true;
            var g = node.G;
            used++;
            _expanded++;
            if (key == _goalKey)
            {
                CompletePath(path, key, partial: false);
                return used;
            }

            var cell = NavCell.FromKey(key);
            var h = Heuristic(cell, goal);
            if (h < _bestH)
            {
                _bestH = h;
                _bestKey = key;
            }

            if (_expanded >= _options.MaxNodesPerSearch)
            {
                CompletePath(path, _bestKey, partial: true);
                return used;
            }

            var count = _navigation.Neighbors(cell, _steps);
            for (var i = 0; i < count; i++)
            {
                var step = _steps[i];
                if (_restricted && !_corridor.Contains(ClusterMap.ClusterKey(_navigation.ClusterOf(step.Cell))))
                {
                    continue;
                }

                var nextKey = step.Cell.Key;
                var cost = g + step.Cost;
                ref var next = ref CollectionsMarshal.GetValueRefOrAddDefault(_nodes, nextKey, out var exists);
                if (exists && (next.Closed || next.G <= cost))
                {
                    continue;
                }

                next.G = cost;
                next.Parent = key;
                _open.Enqueue(nextKey, cost + Heuristic(step.Cell, goal));
            }
        }

        return used;
    }

    private void CompletePath(NavPath path, long endKey, bool partial)
    {
        var startKey = path.Start.Key;
        if (endKey == startKey && startKey != _goalKey)
        {
            path.Fail();
            Finish(path);
            return;
        }

        var length = 0;
        for (var key = endKey; key != startKey && length < _trace.Length; key = _nodes[key].Parent)
        {
            _trace[length++] = key;
        }

        Array.Reverse(_trace, 0, length);
        path.Finish(_trace.AsSpan(0, length), partial);
        Finish(path);
    }

    /// <summary>
    /// Starts the coarse search between the regions of the two ends. False when the ends are near enough to search directly, or
    /// the start stands nowhere a region covers.
    /// </summary>
    private bool BeginCoarse(NavCell start, NavCell goal)
    {
        var from = _navigation.ClusterOf(start);
        _goalCluster = _navigation.ClusterOf(goal);
        if (!_clusters.TryRegionOf(start, out var startRegion))
        {
            return false;
        }

        // Ends in one region are joined inside one cluster, so the fine search alone finds the way quickly. Anything else goes through
        // the regions first, which is also what keeps a goal out of reach, such as a roof, from flooding the fine search.
        _hasGoalRegion = _clusters.TryRegionOf(goal, out _goalRegion);
        if (_hasGoalRegion && _goalRegion == startRegion)
        {
            return false;
        }

        _clusterOpen.Clear();
        _clusterNodes.Clear();
        _startRegion = startRegion;
        _bestRegion = startRegion;
        _bestRegionH = Chebyshev(from, _goalCluster);
        _clusterNodes[startRegion] = new Node { G = 0, Parent = startRegion };
        _clusterOpen.Enqueue(startRegion, _bestRegionH);
        _coarseExpanded = 0;
        return true;
    }

    /// <summary>
    /// Runs the coarse search over regions. Analysing a cluster the first time costs as much as expanding its cells. Returns the work
    /// done; sets <paramref name="done"/> once the corridor is known.
    /// </summary>
    private int StepCoarse(int budget, out bool done)
    {
        done = false;
        var used = 0;
        var cellsPerCluster = _navigation.Options.ClusterSize * _navigation.Options.ClusterSize;
        while (used < budget)
        {
            if (!_clusterOpen.TryDequeue(out var key, out _) || _coarseExpanded >= _options.MaxClusterExpansions)
            {
                // The goal's region is out of reach: head for the region that came nearest to it.
                _corridorReachesGoal = false;
                FinishCorridor(_bestRegion);
                done = true;
                return used;
            }

            var region = (int)key;
            ref var node = ref CollectionsMarshal.GetValueRefOrNullRef(_clusterNodes, key);
            if (node.Closed)
            {
                continue;
            }

            node.Closed = true;
            var g = node.G;
            _coarseExpanded++;
            used++;
            if (_hasGoalRegion && region == _goalRegion)
            {
                _corridorReachesGoal = true;
                FinishCorridor(region);
                done = true;
                return used;
            }

            var cluster = _clusters.ClusterOf(region);
            var h = Chebyshev(cluster, _goalCluster);
            if (h < _bestRegionH)
            {
                _bestRegionH = h;
                _bestRegion = region;
            }

            var analyzed = _clusters.AnalyzedCount;
            var neighbors = _clusters.NeighborsOf(region);
            used += (_clusters.AnalyzedCount - analyzed) * cellsPerCluster;
            for (var i = 0; i < neighbors.Count; i++)
            {
                var neighbor = neighbors[i];
                var neighborCluster = _clusters.ClusterOf(neighbor);
                var cost = g + Chebyshev(cluster, neighborCluster);
                ref var next = ref CollectionsMarshal.GetValueRefOrAddDefault(_clusterNodes, neighbor, out var exists);
                if (exists && (next.Closed || next.G <= cost))
                {
                    continue;
                }

                next.G = cost;
                next.Parent = region;
                _clusterOpen.Enqueue(neighbor, cost + Chebyshev(neighborCluster, _goalCluster));
            }
        }

        return used;
    }

    private void FinishCorridor(int endRegion)
    {
        _corridor.Clear();
        for (long region = endRegion; ; region = _clusterNodes[region].Parent)
        {
            AddToCorridor(_clusters.ClusterOf((int)region));
            if (region == _startRegion)
            {
                return;
            }
        }
    }

    private void AddToCorridor((int X, int Z) cluster)
    {
        var margin = _options.CorridorMargin;
        for (var dz = -margin; dz <= margin; dz++)
        {
            for (var dx = -margin; dx <= margin; dx++)
            {
                _corridor.Add(ClusterMap.ClusterKey((cluster.X + dx, cluster.Z + dz)));
            }
        }
    }

    private static int Chebyshev((int X, int Z) a, (int X, int Z) b) => Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Z - b.Z));

    private void BeginField(FlowField field)
    {
        _open.Clear();
        field.Back.Clear();
        var target = field.Building;
        field.Reach = field.RequestedReach;
        if (_navigation.CanStand(target))
        {
            field.Back[target.Key] = new FlowField.FlowEntry { Cost = 0, Next = target.Key };
            _open.Enqueue(target.Key, 0);
        }
    }

    /// <summary>Grows the field outward from its target, cheapest first, following moves backward so each cell points one step nearer.</summary>
    private int StepField(FlowField field, int budget)
    {
        if (field.IsCancelled)
        {
            field.IsCancelled = false;
            Finish(field);
            return 0;
        }

        var used = 0;
        var cells = field.Back;
        while (used < budget)
        {
            if (!_open.TryDequeue(out var key, out var priority))
            {
                field.Swap();
                Finish(field);
                if (field.RequestedTarget != field.Target)
                {
                    Request(field, field.RequestedTarget, field.RequestedReach);
                }

                return used;
            }

            ref var entry = ref CollectionsMarshal.GetValueRefOrNullRef(cells, key);
            if (entry.Closed || priority > entry.Cost)
            {
                continue;
            }

            entry.Closed = true;
            var cost = entry.Cost;
            used++;
            var count = _navigation.Predecessors(NavCell.FromKey(key), _steps);
            for (var i = 0; i < count; i++)
            {
                var step = _steps[i];
                var total = cost + step.Cost;
                if (total > field.Reach)
                {
                    continue;
                }

                var fromKey = step.Cell.Key;
                ref var from = ref CollectionsMarshal.GetValueRefOrAddDefault(cells, fromKey, out var exists);
                if (exists && (from.Closed || from.Cost <= total))
                {
                    continue;
                }

                if (!exists && cells.Count > _options.MaxFlowFieldCells)
                {
                    cells.Remove(fromKey);
                    continue;
                }

                from.Cost = total;
                from.Next = key;
                _open.Enqueue(fromKey, total);
            }
        }

        return used;
    }

    private struct Node
    {
        public int G;
        public long Parent;
        public bool Closed;
    }
}
