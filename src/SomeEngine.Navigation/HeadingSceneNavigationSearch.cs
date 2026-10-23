using System.Numerics;

namespace SomeEngine.Navigation;

public sealed class HeadingSceneSearchState
{
    private HeadingSceneSearchRun _run;
    private long _version;
    private bool _reset;
    internal HeadingSceneSearchState(in NavigationSearchInput<HeadingNavigationScene> input)
    {
        _run = new(in input); _version = input.Space.Content.Version;
    }
    internal bool Update(in NavigationSearchInput<HeadingNavigationScene> input)
    {
        if (_version == input.Space.Content.Version) return false;
        _version = input.Space.Content.Version;
        if (!_run.Waiting) return false;
        if (_run.StartMissing || (_run.GoalMissing && input.Space.Content.TryGetTile(_run.GoalTile, out _)))
        { _run = new(in input); _reset = true; }
        else _run.Resume();
        return true;
    }
    internal NavigationSearchStep Advance(int budget)
    {
        NavigationSearchStep step = _run.Advance(budget);
        if (_reset && step.Path is { Poses.IsEmpty: false }) { _reset = false; return step with { ResetPathProgress = true }; }
        return step;
    }
}

/// <summary>Heading-aware region search with ASA endpoint and detailed-tile refinement.</summary>
public readonly struct HeadingSceneNavigationSearch : INavigationSearch<HeadingNavigationScene, HeadingSceneSearchState>
{
    public static HeadingSceneSearchState Create(in NavigationSearchInput<HeadingNavigationScene> input) => new(in input);
    public static NavigationSearchStep Advance(HeadingSceneSearchState state, int expansionBudget) => state.Advance(expansionBudget);
    public static bool Update(HeadingSceneSearchState state, in NavigationSearchInput<HeadingNavigationScene> input) => state.Update(in input);
}

internal sealed class HeadingSceneSearchRun
{
    private readonly NavigationSearchInput<HeadingNavigationScene> _input;
    private readonly HeadingNavigationRegionGraph _directory;
    private readonly AsaGraph _graph = new();
    private AsaGraphSearch _coarse = null!;
    private readonly List<(int From, int To)> _connectors = [];
    private readonly Dictionary<(int From, int To), NavigationPath> _endpointPaths = [];
    private readonly Dictionary<(int From, int To), HeadingNavigationMesh> _endpointMeshes = [];
    private readonly List<NavigationPose> _poses = [];
    private readonly List<Vector3> _corners = [];
    private readonly List<int> _corridor = [];
    private readonly List<NavigationPathSection> _sections = [];
    private readonly List<NavigationPathDependency> _dependencies = [];
    private readonly HashSet<NavigationTileCoordinate> _usedTiles = [];
    private AsaSearchState? _local;
    private HeadingNavigationMesh? _localMesh;
    private int _connector, _detail;
    private int[] _route = [];
    private NavigationRegionRoute? _regionRoute;
    private NavigationPath? _path;
    private NavigationSearchStatus _status;
    private int _phase;
    private float _cost;
    private readonly NavigationTileCoordinate _startTile;
    internal NavigationTileCoordinate GoalTile { get; }
    internal bool StartMissing { get; }
    internal bool GoalMissing { get; }
    internal bool Waiting => _status == NavigationSearchStatus.WaitingForTiles;
    internal void Resume() => _status = NavigationSearchStatus.Searching;
    private int StartNode => _graph.Nodes.Count - 2;
    private int GoalNode => _graph.Nodes.Count - 1;

    internal HeadingSceneSearchRun(in NavigationSearchInput<HeadingNavigationScene> input)
    {
        _input = input;
        HeadingNavigationSceneContent content = input.Space.Content;
        _directory = content.Graph;
        HeadingNavigationMeshTile? start = content.FindTile(input.Start, input.StartHeading), goal = content.FindTile(input.Goal, input.GoalHeading);
        NavigationTileCoordinate? startId = start?.Tile ?? content.FindTileInDirectory(input.Start);
        NavigationTileCoordinate? goalId = goal?.Tile ?? content.FindTileInDirectory(input.Goal);
        if (startId is null || goalId is null) { _status = NavigationSearchStatus.Unreachable; return; }
        _startTile = startId.Value; GoalTile = goalId.Value; StartMissing = start is null; GoalMissing = goal is null;
        if (StartMissing)
        {
            _status = NavigationSearchStatus.WaitingForTiles;
            _path = new(default, default, requiredTiles: new NavigationSceneTileId[] { new(0, _startTile) });
            return;
        }
        foreach (HeadingNavigationRegionEntrance entrance in _directory.Entrances.Span)
            _graph.Add(new(entrance.Position, entrance.Channel * MathF.Tau / _directory.ChannelCount, 0, entrance.Channel));
        _graph.Add(new(input.Start, input.StartHeading, 0, -1));
        _graph.Add(new(input.Goal, input.GoalHeading ?? 0, 0, -1));
        foreach (HeadingNavigationRegionEdge edge in _directory.Edges.Span) _graph.Connect(edge.From, edge.To, edge.Cost);
        for (int index = 0; index < _directory.Entrances.Length; index++)
        {
            HeadingNavigationRegionEntrance entrance = _directory.Entrances.Span[index];
            if (entrance.Tile == _startTile) _connectors.Add((StartNode, index));
            if (entrance.Tile == GoalTile)
            {
                if (GoalMissing) _graph.Connect(index, GoalNode, Vector3.Distance(entrance.Position, input.Goal) / input.Profile.MaximumSpeed);
                else _connectors.Add((index, GoalNode));
            }
        }
        if (_startTile == GoalTile) _connectors.Add((StartNode, GoalNode));
    }

    internal NavigationSearchStep Advance(int budget)
    {
        while (budget > 0 && _status == NavigationSearchStatus.Searching)
        {
            budget--;
            if (_phase == 0) ConnectEndpoint();
            else if (_phase == 1)
            {
                int single = 1;
                NavigationSearchStatus status = _coarse.Advance(ref single);
                if (status == NavigationSearchStatus.Unreachable) _status = status;
                else if (status == NavigationSearchStatus.Found)
                {
                    _route = _coarse.Route();
                    _regionRoute = new(_route.Where(index => index < StartNode).Select(index => new NavigationRouteEntrance(0,
                        _directory.Entrances.Span[index].Tile, index, _graph.Nodes[index].Position)).ToArray(), _coarse.Cost);
                    _phase = 2;
                }
            }
            else AdvanceDetail();
        }
        return new(_status, _path);
    }

    private void ConnectEndpoint()
    {
        if (_connector == _connectors.Count)
        {
            float minimum = _input.Policy.DefaultAreaCost;
            foreach (NavigationAreaCost area in _input.Policy.AreaCosts.Span) minimum = MathF.Min(minimum, area.Multiplier);
            _coarse = new(_graph, StartNode, GoalNode, _input.Profile.MaximumSpeed, _input.Profile.MaximumAngularSpeed, minimum, _input.GoalHeading.HasValue);
            _phase = 1; return;
        }
        var pair = _connectors[_connector];
        NavigationSearchStep result = AdvanceLocal(pair.From, pair.To);
        if (result.Status == NavigationSearchStatus.Searching) return;
        if (result.Path is { } path)
        {
            _graph.Connect(pair.From, pair.To, path.Cost);
            _endpointPaths.Add(pair, path); _endpointMeshes.Add(pair, _localMesh!);
        }
        _local = null; _localMesh = null; _connector++;
    }

    private NavigationTileCoordinate Tile(int node) => node == StartNode ? _startTile : node == GoalNode ? GoalTile : _directory.Entrances.Span[node].Tile;
    private NavigationSearchStep AdvanceLocal(int from, int to)
    {
        if (_local is null)
        {
            if (!_input.Space.Content.TryGetTile(Tile(from), out HeadingNavigationMeshTile tile))
                return new(NavigationSearchStatus.WaitingForTiles);
            _localMesh = tile.Mesh;
            AsaNode first = _graph.Nodes[from], last = _graph.Nodes[to];
            var input = new NavigationSearchInput<HeadingNavigationMesh>(tile.Mesh, _input.Profile, _input.Policy,
                first.Position, last.Position, to == GoalNode ? _input.GoalRange : 0, first.Heading,
                to == GoalNode ? _input.GoalHeading : last.Heading, _input.GoalHeadingTolerance);
            _local = new(in input);
        }
        return _local.Advance(1);
    }

    private void AdvanceDetail()
    {
        if (_detail == _route.Length - 1)
        {
            _status = NavigationSearchStatus.Found; Publish(); return;
        }
        int from = _route[_detail], to = _route[_detail + 1];
        NavigationTileCoordinate tile = Tile(from);
        if (tile != Tile(to))
        {
            AsaNode next = _graph.Nodes[to];
            AppendPose(new(next.Position, next.Heading));
            _detail++; return;
        }
        NavigationSearchStep step;
        if (_endpointPaths.TryGetValue((from, to), out NavigationPath? known))
        { step = new(NavigationSearchStatus.Found, known); _localMesh = _endpointMeshes[(from, to)]; }
        else step = AdvanceLocal(from, to);
        if (step.Status == NavigationSearchStatus.Searching) return;
        if (step.Status == NavigationSearchStatus.WaitingForTiles)
        {
            _status = step.Status; Publish(tile); return;
        }
        if (step.Status == NavigationSearchStatus.Unreachable) { _status = step.Status; return; }
        NavigationPath path = step.Path!;
        int corridorStart = _corridor.Count;
        _corridor.AddRange(path.Corridor.ToArray()); _corners.AddRange(path.Corners.ToArray());
        foreach (NavigationPose pose in path.Poses.Span) AppendPose(pose);
        _sections.Add(new(0, tile, _localMesh!.Geometry, corridorStart, path.Corridor.Length));
        if (_usedTiles.Add(tile)) _dependencies.Add(new(tile, _localMesh.ContentVersion));
        _cost += path.Cost;
        _local = null; _localMesh = null; _detail++;
        Publish();
    }

    private void AppendPose(NavigationPose pose)
    {
        if (_poses.Count == 0 || _poses[^1] != pose) _poses.Add(pose);
    }
    private void Publish(NavigationTileCoordinate? missing = null) => _path = new(_corridor.ToArray(), _corners.ToArray(),
        _dependencies.ToArray(), _cost, _regionRoute, _sections.ToArray(),
        requiredTiles: missing is { } tile ? new NavigationSceneTileId[] { new(0, tile) } : default, poses: _poses.ToArray());
}
