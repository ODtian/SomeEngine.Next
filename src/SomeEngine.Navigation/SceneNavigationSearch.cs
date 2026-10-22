using System.Numerics;

namespace SomeEngine.Navigation;

/// <summary>One request's endpoint connections, coarse route and detailed polygon searches.</summary>
public sealed class SceneSearchState
{
    private readonly NavigationSearchInput<NavigationScene> _input;
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

    internal SceneSearchState(in NavigationSearchInput<NavigationScene> input)
    {
        _input = input;
        _minimumAreaCost = input.Policy.DefaultAreaCost;
        foreach (NavigationAreaCost cost in input.Policy.AreaCosts.Span)
            _minimumAreaCost = MathF.Min(_minimumAreaCost, cost.Multiplier);
        NavigationSceneContent content = input.Space.Content;
        NavigationSceneTile? start = content.FindTile(input.Start, out Vector3 startPoint);
        NavigationSceneTile? goal = content.FindTile(input.Goal, out Vector3 goalPoint);
        if (start is null || goal is null)
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
        _nodes.Add(new(start.Region, start.Tile, -1, startPoint));
        _nodes.Add(new(goal.Region, goal.Tile, -1, goalPoint));
        _edges.Add([]);
        _edges.Add([]);
        _distances = new float[_nodes.Count];
        _parents = new RouteEdge?[_nodes.Count];
        Array.Fill(_distances, float.PositiveInfinity);
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
            int from = offsets[connection.FromRegion] + content.GetRegion(connection.FromRegion).Graph.FindPort(connection.FromPort);
            int to = offsets[connection.ToRegion] + content.GetRegion(connection.ToRegion).Graph.FindPort(connection.ToPort);
            float cost = Vector3.Distance(_nodes[from].Position, _nodes[to].Position) * input.Policy.DefaultAreaCost +
                connection.Cost + input.Policy.GetActionCost(connection.Action);
            if (!float.IsFinite(cost)) continue;
            _edges[from].Add(new(from, to, cost, null, connection.Id, connection.ContentVersion));
            if (connection.Bidirectional)
                _edges[to].Add(new(to, from, cost, null, connection.Id, connection.ContentVersion));
        }
        for (int entrance = 0; entrance < StartNode; entrance++)
        {
            SceneNode node = _nodes[entrance];
            if (node.Region == start.Region && node.Tile == start.Tile)
                _connectors.Add(new(StartNode, entrance, 0, start.Id));
            if (node.Region == goal.Region && node.Tile == goal.Tile)
                _connectors.Add(new(entrance, GoalNode, 0, goal.Id));
        }
        if (start.Id == goal.Id)
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
            _distances[StartNode] = 0;
            _open.Enqueue(new(StartNode, 0), Heuristic(StartNode));
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
        if (!_open.TryDequeue(out QueueEntry entry, out _))
        {
            _status = NavigationSearchStatus.Unreachable;
            return;
        }
        if (entry.Cost != _distances[entry.Node]) return;
        if (entry.Node == GoalNode)
        {
            var nodes = new List<int> { GoalNode };
            for (int node = GoalNode; _parents[node] is { } edge; node = edge.From)
            {
                _route.Add(edge);
                nodes.Add(edge.From);
            }
            _route.Reverse();
            nodes.Reverse();
            var entrances = new List<NavigationRouteEntrance>();
            foreach (int index in nodes)
            {
                SceneNode node = _nodes[index];
                if (node.Entrance >= 0) entrances.Add(new(node.Region, node.Tile, node.Entrance, node.Position));
            }
            _regionRoute = new(entrances.ToArray(), entry.Cost);
            _phase = SearchPhase.DetailedRoute;
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

    private void AdvanceDetailedRoute()
    {
        if (_detail == _route.Count)
        {
            _path = new(_corridor.ToArray(), _corners.ToArray(), _dependencies.ToArray(), _detailedCost,
                _regionRoute, _sections.ToArray(), _connectionDependencies.ToArray());
            _status = NavigationSearchStatus.Found;
            return;
        }
        RouteEdge edge = _route[_detail];
        if (edge.Tile is { } tile)
        {
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
            AppendCorner(from.Position);
            AppendCorner(to.Position);
            AddDependency(_input.Space.Content.GetTile(new(from.Region, from.Tile)));
            AddDependency(_input.Space.Content.GetTile(new(to.Region, to.Tile)));
            _detailedCost += edge.Cost;
            if (edge.Connection >= 0 && _usedConnections.Add(edge.Connection))
                _connectionDependencies.Add(new(edge.Connection, edge.ConnectionVersion));
        }
        _detail++;
    }

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
    private readonly record struct SceneNode(int Region, NavigationTileCoordinate Tile, int Entrance, Vector3 Position);
    private readonly record struct QueueEntry(int Node, float Cost);

    private sealed class RouteEdge(int from, int to, float cost, NavigationSceneTileId? tile,
        int connection = -1, ulong connectionVersion = 0)
    {
        public int From { get; } = from;
        public int To { get; } = to;
        public float Cost { get; set; } = cost;
        public NavigationSceneTileId? Tile { get; } = tile;
        public int Connection { get; } = connection;
        public ulong ConnectionVersion { get; } = connectionVersion;
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
}
