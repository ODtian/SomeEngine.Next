using System.Numerics;

namespace SomeEngine.Navigation;

/// <summary>One request's endpoint connections, coarse route and detailed polygon searches.</summary>
public sealed class SceneSearchState
{
    private SceneSearchRun _run;
    private NavigationPath? _path;
    private long _version;
    private long _contentVersion;
    private bool _replace;

    internal SceneSearchState(in NavigationSearchInput<NavigationScene> input)
    {
        _run = new(in input);
        _version = input.Space.Content.Version;
        _contentVersion = input.Space.Content.ContentVersion;
    }

    internal bool Update(in NavigationSearchInput<NavigationScene> input)
    {
        NavigationSceneContent current = input.Space.Content;
        if (_version == current.Version) return false;
        _version = current.Version;
        bool contentChanged = _contentVersion != current.ContentVersion;
        _contentVersion = current.ContentVersion;
        if (contentChanged && (_run.Found ? !current.AreDependenciesCurrent(_path!) : _run.RequiresRepair(current)))
        {
            _run = new(in input);
            _path = null;
            _replace = true;
            return true;
        }
        if (!_run.Waiting) return false;
        if (_run.StartMissing || (_run.GoalMissing && input.Space.Content.TryGetTile(_run.GoalTile, out _)))
        {
            _run = new(in input);
            _replace = true;
        }
        else _run.Resume();
        return true;
    }

    internal NavigationSearchStep Advance(int budget)
    {
        NavigationSearchStep step = _run.Advance(budget);
        bool reset = false;
        if (step.Path is { } path)
        {
            _path = path;
            if (_replace && !path.Corners.IsEmpty) { reset = true; _replace = false; }
        }
        return step with { Path = _path, ResetPathProgress = reset };
    }
}

internal sealed class SceneSearchRun
{
    private readonly DStarLiteWorkspace? _incremental;
    private readonly NavigationSearchInput<NavigationScene> _input;
    private readonly NavigationSceneContent _content;
    private readonly List<SceneNode> _nodes = [];
    private readonly List<List<RouteEdge>> _edges = [];
    private readonly List<RouteEdge> _connectors = [];
    private readonly PriorityQueue<QueueEntry, float> _open = new();
    private readonly float[] _distances = [];
    private readonly RouteEdge?[] _parents = [];
    private readonly float _minimumAreaCost;
    private readonly List<int> _corridor = [];
    private readonly List<Vector3> _corners = [];
    private readonly List<NavigationPathSection> _sections = [];
    private readonly List<NavigationPathDependency> _dependencies = [];
    private readonly HashSet<NavigationSceneTileId> _usedTiles = [];
    private readonly List<NavigationConnectionDependency> _connectionDependencies = [];
    private readonly List<NavigationPathTraversal> _traversals = [];
    private readonly HashSet<int> _usedConnections = [];
    private List<RouteEdge> _route = [];
    private NavigationRegionRoute? _regionRoute;
    private PolygonSearchState? _localSearch;
    private NavigationMesh? _localMesh;
    private int _connector;
    private int _detail;
    private float _detailedCost;
    private SearchPhase _phase;
    private NavigationSearchStatus _status;
    private NavigationPath? _path;

    internal bool Waiting => _status == NavigationSearchStatus.WaitingForTiles;
    internal bool Found => _status == NavigationSearchStatus.Found;
    internal bool StartMissing { get; }
    internal bool GoalMissing { get; }
    internal NavigationSceneTileId GoalTile { get; }
    internal void Resume() => _status = NavigationSearchStatus.Searching;

    internal bool RequiresRepair(NavigationSceneContent current)
    {
        if (_route.Count == 0) return true;
        foreach (RouteEdge edge in _route)
        {
            foreach (int node in new[] { edge.From, edge.To })
            {
                SceneNode point = _nodes[node];
                var id = new NavigationSceneTileId(point.Region, point.Tile);
                if (!_content.TryGetContentVersion(id, out ulong before) ||
                    !current.TryGetContentVersion(id, out ulong after) || before != after) return true;
            }
            if (edge.Connection >= 0 && (!current.TryGetConnectionVersion(edge.Connection, out ulong version) ||
                version != edge.ConnectionVersion)) return true;
        }
        return false;
    }

    internal SceneSearchRun(in NavigationSearchInput<NavigationScene> input, DStarLiteWorkspace? incremental = null)
    {
        _incremental = incremental;
        _input = input;
        _minimumAreaCost = input.Policy.DefaultAreaCost;
        foreach (NavigationAreaCost cost in input.Policy.AreaCosts.Span)
            _minimumAreaCost = MathF.Min(_minimumAreaCost, cost.Multiplier);
        NavigationSceneContent content = _content = input.Space.Content;
        NavigationSceneTile? start = content.FindTile(input.Start, out Vector3 startPoint);
        NavigationSceneTile? goal = content.FindTile(input.Goal, out Vector3 goalPoint);
        NavigationSceneTileId? startId = start?.Id ?? content.FindTileInDirectory(input.Start);
        NavigationSceneTileId? goalId = goal?.Id ?? content.FindTileInDirectory(input.Goal);
        if (startId is null || goalId is null)
        {
            _status = NavigationSearchStatus.Unreachable;
            return;
        }
        GoalTile = goalId.Value;
        StartMissing = start is null;
        GoalMissing = goal is null;
        if (StartMissing)
        {
            _status = NavigationSearchStatus.WaitingForTiles;
            _path = new(default, default, requiredTiles: new NavigationSceneTileId[] { startId.Value });
            return;
        }
        if (GoalMissing && content.TryGetTile(GoalTile, out _))
        {
            _status = NavigationSearchStatus.Unreachable;
            return;
        }

        var offsets = new Dictionary<int, int>();
        foreach (NavigationSceneRegion region in content.Regions.Span)
        {
            offsets.Add(region.Id, _nodes.Count);
            for (int entrance = 0; entrance < region.Graph.Entrances.Length; entrance++)
            {
                NavigationRegionEntrance point = region.Graph.Entrances.Span[entrance];
                _nodes.Add(new(region.Id, point.Tile, entrance, point.Position));
                _edges.Add([]);
            }
        }
        _nodes.Add(new(start!.Region, start.Tile, -1, startPoint));
        _nodes.Add(new(GoalTile.Region, GoalTile.Tile, -1, goalPoint));
        _edges.Add([]);
        _edges.Add([]);
        if (incremental is null)
        {
            _distances = new float[_nodes.Count];
            _parents = new RouteEdge?[_nodes.Count];
            Array.Fill(_distances, float.PositiveInfinity);
        }
        ulong capabilities = input.Profile.Capabilities & input.Policy.AllowedCapabilities;
        foreach (NavigationSceneRegion region in content.Regions.Span)
        foreach (NavigationRegionEdge edge in region.Graph.Edges.Span)
        {
            if ((edge.RequiredCapabilities & capabilities) != edge.RequiredCapabilities) continue;
            int from = offsets[region.Id] + edge.From, to = offsets[region.Id] + edge.To;
            NavigationSceneTileId? tile = _nodes[from].Tile == _nodes[to].Tile
                ? new(region.Id, _nodes[from].Tile) : null;
            _edges[from].Add(new(from, to, edge.Cost, tile));
        }
        foreach (NavigationSceneConnection connection in content.Connections.Span)
        {
            if ((connection.RequiredCapabilities & capabilities) != connection.RequiredCapabilities) continue;
            NavigationRegionGraph fromGraph = content.GetRegion(connection.FromRegion).Graph;
            NavigationRegionGraph toGraph = content.GetRegion(connection.ToRegion).Graph;
            int fromPort = fromGraph.FindPort(connection.FromPort), toPort = toGraph.FindPort(connection.ToPort);
            if (fromPort < 0 || toPort < 0 || fromGraph.Entrances.Span[fromPort].Polygon < 0 || toGraph.Entrances.Span[toPort].Polygon < 0) continue;
            int from = offsets[connection.FromRegion] + fromPort;
            int to = offsets[connection.ToRegion] + toPort;
            float cost = Vector3.Distance(_nodes[from].Position, _nodes[to].Position) * input.Policy.DefaultAreaCost +
                connection.Cost + input.Policy.GetActionCost(connection.Action);
            if (!float.IsFinite(cost)) continue;
            _edges[from].Add(new(from, to, cost, null, connection.Id, connection.ContentVersion, connection.Action, connection.Links));
            if (connection.Bidirectional)
                _edges[to].Add(new(to, from, cost, null, connection.Id, connection.ContentVersion, connection.Action, connection.Links));
        }
        for (int entrance = 0; entrance < StartNode; entrance++)
        {
            SceneNode node = _nodes[entrance];
            if (node.Region == start.Region && node.Tile == start.Tile)
                _connectors.Add(new(StartNode, entrance, 0, start.Id));
            if (node.Region == GoalTile.Region && node.Tile == GoalTile.Tile)
            {
                var edge = new RouteEdge(entrance, GoalNode,
                    Vector3.Distance(node.Position, goalPoint) * _minimumAreaCost, GoalTile);
                if (GoalMissing) _edges[entrance].Add(edge);
                else _connectors.Add(edge);
            }
        }
        if (start.Id == GoalTile)
            _connectors.Add(new(StartNode, GoalNode, 0, start.Id));
    }

    private int StartNode => _nodes.Count - 2;
    private int GoalNode => _nodes.Count - 1;

    internal NavigationSearchStep Advance(int expansionBudget)
    {
        if (_status != NavigationSearchStatus.Searching)
            return new(_status, _path);
        for (int expanded = 0; expanded < expansionBudget && _status == NavigationSearchStatus.Searching; expanded++)
        {
            switch (_phase)
            {
                case SearchPhase.ConnectEndpoints: ConnectEndpoint(); break;
                case SearchPhase.CoarseRoute: AdvanceCoarseRoute(); break;
                case SearchPhase.DetailedRoute: AdvanceDetailedRoute(); break;
            }
        }
        return new(_status, _path);
    }

    private void ConnectEndpoint()
    {
        if (_connector == _connectors.Count)
        {
            if (_incremental is { } workspace)
                workspace.ApplyGraph(_nodes, _edges, StartNode, GoalNode, _content);
            else
            {
                _distances[StartNode] = 0;
                _open.Enqueue(new(StartNode, 0), Heuristic(StartNode));
            }
            _phase = SearchPhase.CoarseRoute;
            return;
        }
        RouteEdge edge = _connectors[_connector];
        NavigationSearchStep step = AdvanceLocal(edge);
        if (step.Status == NavigationSearchStatus.Searching) return;
        if (step.Path is { } path)
        {
            edge.Cost = path.Cost;
            edge.Path = path;
            edge.Mesh = _localMesh;
            _edges[edge.From].Add(edge);
        }
        _localSearch = null;
        _localMesh = null;
        _connector++;
    }

    private void AdvanceCoarseRoute()
    {
        if (_incremental is { } workspace)
        {
            NavigationSearchStatus status = workspace.Advance();
            if (status == NavigationSearchStatus.Found)
            {
                _route = workspace.CreateRoute();
                SetRegionRoute(workspace.Cost);
            }
            else if (status == NavigationSearchStatus.Unreachable) _status = status;
            return;
        }
        if (!_open.TryDequeue(out QueueEntry entry, out _))
        {
            _status = NavigationSearchStatus.Unreachable;
            return;
        }
        if (entry.Cost != _distances[entry.Node]) return;
        if (entry.Node == GoalNode)
        {
            for (int node = GoalNode; _parents[node] is { } edge; node = edge.From)
                _route.Add(edge);
            _route.Reverse();
            SetRegionRoute(entry.Cost);
            return;
        }
        foreach (RouteEdge edge in _edges[entry.Node])
        {
            float distance = entry.Cost + edge.Cost;
            if (distance >= _distances[edge.To]) continue;
            _distances[edge.To] = distance;
            _parents[edge.To] = edge;
            _open.Enqueue(new(edge.To, distance), distance + Heuristic(edge.To));
        }
    }

    private float Heuristic(int node) =>
        Vector3.Distance(_nodes[node].Position, _nodes[GoalNode].Position) * _minimumAreaCost;

    private void SetRegionRoute(float cost)
    {
        var entrances = new List<NavigationRouteEntrance>();
        foreach (RouteEdge edge in _route)
        {
            SceneNode node = _nodes[edge.To];
            if (node.Entrance >= 0) entrances.Add(new(node.Region, node.Tile, node.Entrance, node.Position));
        }
        _regionRoute = new(entrances.ToArray(), cost);
        _phase = SearchPhase.DetailedRoute;
    }

    private void AdvanceDetailedRoute()
    {
        if (_detail == _route.Count)
        {
            PublishPath();
            _status = NavigationSearchStatus.Found;
            return;
        }
        RouteEdge edge = _route[_detail];
        if (edge.Tile is { } tile)
        {
            if (edge.Path is null && !_input.Space.Content.TryGetTile(tile, out _))
            {
                WaitForTiles();
                return;
            }
            if (edge.Path is { } cached)
                AppendDetails(tile, edge.Mesh!, cached);
            else
            {
                NavigationSearchStep step = AdvanceLocal(edge);
                if (step.Status == NavigationSearchStatus.Searching) return;
                if (step.Path is not { } path)
                {
                    _status = NavigationSearchStatus.Unreachable;
                    return;
                }
                AppendDetails(tile, _localMesh!, path);
                _localSearch = null;
                _localMesh = null;
            }
        }
        else
        {
            SceneNode from = _nodes[edge.From], to = _nodes[edge.To];
            if (!_input.Space.Content.TryGetTile(new(to.Region, to.Tile), out NavigationSceneTile? target))
            {
                WaitForTiles();
                return;
            }
            AppendCorner(from.Position);
            int entryCorner = _corners.Count - 1;
            AppendCorner(to.Position);
            if (edge.Action != 0)
            {
                NavigationLink link = edge.Links?.GetLink(edge.Connection) ?? new(edge.Connection,
                    new(from.Region, -1), new(to.Region, -1), edge.Action);
                _traversals.Add(new(edge.Connection, edge.Action, entryCorner, _corners.Count - 1,
                    from.Position, to.Position, link.Parameters, link.EntryRange, link.ExitRange,
                    edge.ConnectionVersion, edge.Links));
            }
            if (_input.Space.Content.TryGetTile(new(from.Region, from.Tile), out NavigationSceneTile? source)) AddDependency(source!);
            AddDependency(target!);
            _detailedCost += edge.Cost;
            if (edge.Connection >= 0 && _usedConnections.Add(edge.Connection))
                _connectionDependencies.Add(new(edge.Connection, edge.ConnectionVersion));
        }
        _detail++;
    }

    private void WaitForTiles()
    {
        var required = new List<NavigationSceneTileId>();
        foreach (RouteEdge edge in _route.Skip(_detail))
        {
            SceneNode node = _nodes[edge.To];
            var tile = new NavigationSceneTileId(node.Region, node.Tile);
            if (!_input.Space.Content.TryGetTile(tile, out _) && !required.Contains(tile)) required.Add(tile);
        }
        PublishPath(required.ToArray());
        _status = NavigationSearchStatus.WaitingForTiles;
    }

    private void PublishPath(NavigationSceneTileId[]? required = null) =>
        _path = new(_corridor.ToArray(), _corners.ToArray(), _dependencies.ToArray(), _detailedCost,
            _regionRoute, _sections.ToArray(), _connectionDependencies.ToArray(), required ?? [], traversals: _traversals.ToArray());

    private NavigationSearchStep AdvanceLocal(RouteEdge edge)
    {
        if (_localSearch is null)
        {
            _localMesh = _input.Space.Content.GetTile(edge.Tile!.Value).Mesh;
            var input = new NavigationSearchInput<NavigationMesh>(_localMesh, _input.Profile, _input.Policy,
                _nodes[edge.From].Position, _nodes[edge.To].Position);
            _localSearch = PolygonNavigationSearch.Create(in input);
        }
        return PolygonNavigationSearch.Advance(_localSearch, 1);
    }

    private void AppendDetails(NavigationSceneTileId tile, NavigationMesh mesh, NavigationPath path)
    {
        int corridorStart = _corridor.Count;
        _corridor.AddRange(path.Corridor.Span);
        _sections.Add(new(tile.Region, tile.Tile, mesh, corridorStart, path.Corridor.Length));
        foreach (Vector3 corner in path.Corners.Span) AppendCorner(corner);
        AddDependency(new(tile.Region, tile.Tile, mesh));
        _detailedCost += path.Cost;
    }

    private void AppendCorner(Vector3 corner)
    {
        if (_corners.Count == 0 || Vector3.DistanceSquared(_corners[^1], corner) > 0.0000000001f)
            _corners.Add(corner);
    }

    private void AddDependency(NavigationSceneTile tile)
    {
        if (_usedTiles.Add(tile.Id))
            _dependencies.Add(new(tile.Tile, tile.Mesh.ContentVersion, tile.Region));
    }

    private enum SearchPhase : byte { ConnectEndpoints, CoarseRoute, DetailedRoute }
    internal readonly record struct SceneNode(int Region, NavigationTileCoordinate Tile, int Entrance, Vector3 Position);
    private readonly record struct QueueEntry(int Node, float Cost);

    internal sealed class RouteEdge(int from, int to, float cost, NavigationSceneTileId? tile,
        int connection = -1, ulong connectionVersion = 0, int action = 0, NavigationLinkSet? links = null)
    {
        public int From { get; } = from;
        public int To { get; } = to;
        public float Cost { get; set; } = cost;
        public NavigationSceneTileId? Tile { get; } = tile;
        public int Connection { get; } = connection;
        public ulong ConnectionVersion { get; } = connectionVersion;
        public int Action { get; } = action;
        public NavigationLinkSet? Links { get; } = links;
        public NavigationPath? Path { get; set; }
        public NavigationMesh? Mesh { get; set; }
    }
}

/// <summary>Searches placed entrance graphs and refines their selected route through resident detailed tiles.</summary>
/// <remarks>Graphs are built for the same profile and policy as the request. Endpoint and detailed searches share its budget.</remarks>
public readonly struct SceneNavigationSearch : INavigationSearch<NavigationScene, SceneSearchState>
{
    public static SceneSearchState Create(in NavigationSearchInput<NavigationScene> input) => new(in input);
    public static NavigationSearchStep Advance(SceneSearchState state, int expansionBudget) => state.Advance(expansionBudget);
    public static bool Update(SceneSearchState state, in NavigationSearchInput<NavigationScene> input) => state.Update(in input);
}
