namespace Zombies.Engine.Ai;

/// <summary>
/// The coarse level of the hierarchical search. The world is cut into square clusters, and the standable cells of each cluster are
/// split into regions: cells a creature can walk between both ways without leaving the cluster. Regions link where a single move
/// leads from one into another, across a cluster border or down a drop. A route between regions therefore always exists as a route
/// between cells, through the same clusters. A cluster is analysed the first time a search reaches it and kept until the terrain changes.
/// </summary>
internal sealed class ClusterMap
{
    private readonly Navigation _navigation;
    private readonly int _size;
    private readonly HashSet<long> _analyzed = [];
    private readonly Dictionary<long, int> _regionOf = [];
    private readonly List<Region> _regions = [];
    private readonly List<long> _cells = [];
    private readonly Queue<long> _flood = new();
    private readonly NavStep[] _steps = new NavStep[Navigation.MaxSteps];
    private readonly bool[] _solid;
    private readonly HashSet<int> _seen = [];

    public ClusterMap(Navigation navigation)
    {
        _navigation = navigation;
        _size = navigation.Options.ClusterSize;
        _solid = new bool[navigation.Options.MaxY + navigation.Options.Height + 2];
    }

    /// <summary>Clusters analysed since the map was made or cleared.</summary>
    public int AnalyzedCount => _analyzed.Count;

    public int RegionCount => _regions.Count;

    public static long ClusterKey((int X, int Z) cluster) => ((long)cluster.X << 32) | (uint)cluster.Z;

    public void Clear()
    {
        _analyzed.Clear();
        _regionOf.Clear();
        _regions.Clear();
    }

    /// <summary>Whether the cluster holding the cell has been analysed, which <see cref="TryRegionOf"/> would otherwise do now.</summary>
    public bool IsAnalyzed(NavCell cell) => _analyzed.Contains(ClusterKey(_navigation.ClusterOf(cell)));

    public bool TryRegionOf(NavCell cell, out int region)
    {
        var cluster = _navigation.ClusterOf(cell);
        if (_analyzed.Add(ClusterKey(cluster)))
        {
            Analyze(cluster);
        }

        return _regionOf.TryGetValue(cell.Key, out region);
    }

    public (int X, int Z) ClusterOf(int region) => _regions[region].Cluster;

    /// <summary>Analyses every cluster from one corner to the other, and links their regions, unless that is already done.</summary>
    public void Prepare((int X, int Z) min, (int X, int Z) max)
    {
        var first = _regions.Count;
        for (var z = min.Z; z <= max.Z; z++)
        {
            for (var x = min.X; x <= max.X; x++)
            {
                if (_analyzed.Add(ClusterKey((x, z))))
                {
                    Analyze((x, z));
                }
            }
        }

        for (var region = first; region < _regions.Count; region++)
        {
            NeighborsOf(region);
        }
    }

    /// <summary>The regions one move leads into from this one, analysing the clusters they lie in when that is still to do.</summary>
    public IReadOnlyList<int> NeighborsOf(int region)
    {
        var data = _regions[region];
        if (data.Neighbors is null)
        {
            var neighbors = new List<int>();
            _seen.Clear();
            foreach (var exit in data.Exits)
            {
                if (TryRegionOf(NavCell.FromKey(exit), out var target) && target != region && _seen.Add(target))
                {
                    neighbors.Add(target);
                }
            }

            data.Neighbors = neighbors;
        }

        return data.Neighbors;
    }

    private void Analyze((int X, int Z) cluster)
    {
        var options = _navigation.Options;
        var terrain = _navigation.Terrain;
        int minX = cluster.X * _size, minZ = cluster.Z * _size;
        _cells.Clear();
        for (var z = minZ; z < minZ + _size; z++)
        {
            for (var x = minX; x < minX + _size; x++)
            {
                for (var y = options.MinY - 1; y < _solid.Length; y++)
                {
                    _solid[y] = terrain.IsSolid(x, y, z);
                }

                for (var y = options.MinY; y <= options.MaxY; y++)
                {
                    if (!_solid[y - 1])
                    {
                        continue;
                    }

                    var open = true;
                    for (var h = 0; h < options.Height && open; h++)
                    {
                        open = !_solid[y + h];
                    }

                    if (open)
                    {
                        _cells.Add(new NavCell(x, y, z).Key);
                    }
                }
            }
        }

        // Flood each region with the moves that can be made both ways, staying inside the cluster.
        var first = _regions.Count;
        foreach (var key in _cells)
        {
            if (_regionOf.ContainsKey(key))
            {
                continue;
            }

            var id = _regions.Count;
            _regions.Add(new Region(cluster));
            _regionOf[key] = id;
            _flood.Enqueue(key);
            while (_flood.TryDequeue(out var current))
            {
                var cell = NavCell.FromKey(current);
                var count = _navigation.Neighbors(cell, _steps);
                for (var i = 0; i < count; i++)
                {
                    var next = _steps[i].Cell;
                    if (Inside(next, minX, minZ) && !_regionOf.ContainsKey(next.Key) && _navigation.TryMoveCost(next, cell, out _))
                    {
                        _regionOf[next.Key] = id;
                        _flood.Enqueue(next.Key);
                    }
                }
            }
        }

        // Every move out of a region, to another cluster or one-way into another region here, is an exit.
        foreach (var key in _cells)
        {
            var region = _regionOf[key];
            var count = _navigation.Neighbors(NavCell.FromKey(key), _steps);
            for (var i = 0; i < count; i++)
            {
                var next = _steps[i].Cell;
                if (!Inside(next, minX, minZ) || _regionOf[next.Key] != region)
                {
                    _regions[region].Exits.Add(next.Key);
                }
            }
        }

        for (var i = first; i < _regions.Count; i++)
        {
            _regions[i].Exits.TrimExcess();
        }
    }

    private bool Inside(NavCell cell, int minX, int minZ) =>
        cell.X >= minX && cell.X < minX + _size && cell.Z >= minZ && cell.Z < minZ + _size;

    private sealed class Region((int X, int Z) cluster)
    {
        public (int X, int Z) Cluster { get; } = cluster;

        public List<long> Exits { get; } = [];

        public List<int>? Neighbors { get; set; }
    }
}
