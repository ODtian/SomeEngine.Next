using System.Numerics;

namespace SomeEngine.Navigation;

public readonly record struct NavigationFlowGoal(
    Vector3 Position, float ArrivalRange = 0, float? Heading = null, float HeadingTolerance = 0.05f);

public readonly record struct NavigationFlowDependency(
    NavigationTileCoordinate Tile, ulong GridVersion, ulong CostVersion, ulong GoalVersion);

public readonly record struct NavigationFlowTile(NavigationTileCoordinate Tile, int CellStart, int CellCount,
    ulong GridVersion, ulong CostVersion, ulong GoalVersion, bool Detailed);
public readonly record struct NavigationFlowBoundaryDependency(NavigationTileCoordinate From, NavigationTileCoordinate To,
    int FromCell, int ToCell, float Cost, ulong InputVersion, ulong Round);

public enum NavigationFlowSampling : byte { Successor, Gradient }

public sealed class NavigationFlowInput(NavigationCosts costs, NavigationProfile profile,
    ReadOnlyMemory<NavigationFlowGoal> goals, ulong goalVersion = 1, ulong version = 1,
    HeadingNavigationFlowGraph? headingGraph = null)
{
    public NavigationCosts Costs { get; } = costs;
    public NavigationProfile Profile { get; } = profile;
    public ReadOnlyMemory<NavigationFlowGoal> Goals { get; } = goals;
    public ulong GoalVersion { get; } = goalVersion;
    public ulong Version { get; } = version;
    public HeadingNavigationFlowGraph? HeadingGraph { get; } = headingGraph;
}

/// <summary>One complete integration result. SuccessorEdges retain forward transition and action identity.</summary>
public sealed class NavigationFlowField(NavigationFlowInput input, ReadOnlyMemory<float> cumulativeCosts,
    ReadOnlyMemory<int> successorEdges, ReadOnlyMemory<Vector3> directions,
    ReadOnlyMemory<int> goalIndices, ReadOnlyMemory<NavigationFlowDependency> dependencies, string solver = "",
    NavigationFlowSampling sampling = NavigationFlowSampling.Successor,
    ReadOnlyMemory<NavigationFlowTile> tiles = default,
    ReadOnlyMemory<NavigationFlowBoundaryDependency> boundaryDependencies = default,
    ReadOnlyMemory<NavigationTileCoordinate> requiredTiles = default, ulong round = 0)
{
    public NavigationFlowInput Input { get; } = input;
    public ReadOnlyMemory<float> CumulativeCosts { get; } = cumulativeCosts;
    public ReadOnlyMemory<int> SuccessorEdges { get; } = successorEdges;
    public ReadOnlyMemory<Vector3> Directions { get; } = directions;
    public ReadOnlyMemory<int> GoalIndices { get; } = goalIndices;
    public ReadOnlyMemory<NavigationFlowDependency> Dependencies { get; } = dependencies;
    public string Solver { get; } = solver;
    public NavigationFlowSampling Sampling { get; } = sampling;
    public ReadOnlyMemory<NavigationFlowTile> Tiles { get; } = tiles;
    public ReadOnlyMemory<NavigationFlowBoundaryDependency> BoundaryDependencies { get; } = boundaryDependencies;
    public ReadOnlyMemory<NavigationTileCoordinate> RequiredTiles { get; } = requiredTiles;
    public ulong Round { get; } = round;
    public ulong ContentVersion => Input.Costs.Grid.ContentVersion;
    public ulong ResidenceVersion => Input.Costs.Grid.ResidenceVersion;

    /// <summary>Samples the installed direction on the position's support layer.</summary>
    public Vector3 SampleDirection(Vector3 position)
    {
        int cell = Input.Costs.Grid.FindCell(position);
        return cell >= 0 ? NavigationFlowSampler.Direction(this, cell, position) : Vector3.Zero;
    }

    /// <summary>Returns the concrete movement, rotation or action selected by this completed field.</summary>
    public NavigationFlowSample Sample(NavigationPose pose)
    {
        if (Input.HeadingGraph is { } heading)
        {
            int state = heading.FindState(this, pose);
            return heading.Sample(this, pose, ref state);
        }
        NavigationGrid grid = Input.Costs.Grid;
        int cell = grid.FindCell(pose.Position);
        if (cell < 0 || GoalIndices.Span[cell] < 0) return new(NavigationFlowAction.Unreachable, pose);
        int edge = SuccessorEdges.Span[cell];
        if (edge >= 0)
        {
            NavigationGridEdge transition = grid.Edges.Span[edge];
            var next = new NavigationPose(grid.Cells.Span[transition.ToCell].Position, pose.Heading);
            NavigationPathTraversal? traversal = transition.Action != 0
                ? new(edge, transition.Action, 0, 0, grid.Cells.Span[cell].Position, next.Position,
                    default, 0.05f, 0.1f, Input.Costs.Version) : null;
            return new(traversal.HasValue ? NavigationFlowAction.Traversal : NavigationFlowAction.Move, next, cell, edge, traversal);
        }
        NavigationFlowGoal goal = Input.Goals.Span[GoalIndices.Span[cell]];
        bool arrived = Vector3.Distance(pose.Position, goal.Position) <= goal.ArrivalRange + 0.0001f;
        NavigationFlowAction action = arrived
            ? MathF.Abs(NavigationHeading.Difference(goal.Heading ?? pose.Heading, pose.Heading)) <= goal.HeadingTolerance
                ? NavigationFlowAction.Arrived : NavigationFlowAction.Rotate
            : NavigationFlowAction.Move;
        return new(action, new(goal.Position, goal.Heading ?? pose.Heading), cell);
    }
}

/// <summary>The game's shared target and its most recently completed field.</summary>
public sealed class NavigationFlow
{
    private readonly Lock _gate = new();
    private NavigationFlowInput _input;
    private NavigationFlowData _data = new(null, 0);
    private string _solver = ReverseDijkstraFlow.Id;

    public NavigationFlow(NavigationCosts costs, NavigationProfile profile, NavigationFlowGoal[] goals)
        : this(costs, profile, (ReadOnlyMemory<NavigationFlowGoal>)goals) { }

    public NavigationFlow(NavigationCosts costs, NavigationProfile profile, ReadOnlyMemory<NavigationFlowGoal> goals) =>
        _input = new(costs, profile, goals);

    public NavigationFlow(NavigationFlowInput input) => _input = input;

    public NavigationFlow(HeadingNavigationFlowGraph graph, NavigationProfile profile, ReadOnlyMemory<NavigationFlowGoal> goals) =>
        _input = new(graph.Costs, profile, goals, headingGraph: graph);

    public NavigationFlow(NavigationFlowField field)
    {
        _input = field.Input;
        _solver = field.Solver;
        NavigationFlowInput input = field.Input;
        NavigationFlowTileState[] tiles = input.Costs.Grid.FlowRanges.ToArray().Select(tile => new NavigationFlowTileState(
            tile.Tile, NavigationFlowStatus.Ready, input.Version, input.GoalVersion, tile.Detailed,
            tile.ContentVersion, input.Costs.Version, input.Costs.Grid.ResidenceVersion)).ToArray();
        _data = new(field, 1, input, tiles);
    }

    public NavigationFlowInput Input => Volatile.Read(ref _input);
    public NavigationFlowField? Field => Volatile.Read(ref _data).Field;
    public long FieldVersion => Volatile.Read(ref _data).Version;
    /// <summary>The selected integration method, retained while its first field is still being computed.</summary>
    public string Solver => Volatile.Read(ref _solver);
    public NavigationCosts Costs => Input.Costs;
    public NavigationProfile Profile => Input.Profile;
    public ReadOnlyMemory<NavigationFlowGoal> Goals => Input.Goals;
    public ReadOnlyMemory<NavigationFlowTileState> TileStates => Volatile.Read(ref _data).TileStates;

    internal void SetSolver(string solver) => Volatile.Write(ref _solver, solver);

    public NavigationFlowStatus GetStatus(Vector3 position)
    {
        NavigationFlowInput input = Input;
        NavigationFlowData data = Volatile.Read(ref _data);
        if (ReferenceEquals(data.Field?.Input, input)) return NavigationFlowStatus.Ready;
        if (!ReferenceEquals(data.Input, input) || data.TileStates.IsEmpty) return NavigationFlowStatus.Searching;
        if (input.HeadingGraph is { } heading)
        {
            NavigationTileCoordinate? tile = heading.FindTile(position);
            foreach (NavigationFlowTileState current in data.TileStates.Span)
                if (current.Tile == tile) return current.Status;
            return NavigationFlowStatus.Searching;
        }
        NavigationGrid grid = input.Costs.Grid;
        int cell = grid.FindCell(position);
        if (cell < 0) return NavigationFlowStatus.Searching;
        return data.TileStates.Span[grid.CellTiles.Span[cell]].Status;
    }

    public void SetGoals(NavigationFlowGoal[] goals) => SetGoals((ReadOnlyMemory<NavigationFlowGoal>)goals);

    public void SetGoals(ReadOnlyMemory<NavigationFlowGoal> goals)
    {
        lock (_gate)
        {
            NavigationFlowInput current = _input;
            SetInput(new(current.Costs, current.Profile, goals, current.GoalVersion + 1, current.Version + 1, current.HeadingGraph));
        }
    }

    public void SetCosts(NavigationCosts costs)
    {
        lock (_gate)
        {
            NavigationFlowInput current = _input;
            SetInput(new(costs, current.Profile, current.Goals, current.GoalVersion, current.Version + 1, current.HeadingGraph));
        }
    }

    public void SetProfile(NavigationProfile profile)
    {
        lock (_gate)
        {
            NavigationFlowInput current = _input;
            SetInput(new(current.Costs, profile, current.Goals, current.GoalVersion, current.Version + 1, current.HeadingGraph));
        }
    }

    public void SetGraph(HeadingNavigationFlowGraph graph)
    {
        lock (_gate)
        {
            NavigationFlowInput current = _input;
            graph = graph.SharePassages(current.HeadingGraph);
            SetInput(new(graph.Costs, current.Profile, current.Goals, current.GoalVersion, current.Version + 1, graph));
        }
    }

    internal void ApplyCosts(NavigationCosts costs, NavigationProfile profile)
    {
        lock (_gate)
        {
            NavigationFlowInput current = _input;
            if (ReferenceEquals(current.Costs, costs) && ReferenceEquals(current.Profile, profile)) return;
            SetInput(new(costs, profile, current.Goals, current.GoalVersion, current.Version + 1, current.HeadingGraph));
        }
    }

    internal void Apply(NavigationFlowBuildState state)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_input, state.Input)) return;
            NavigationFlowField? field = state.Status == NavigationFlowStatus.Ready ? state.Field : _data.Field;
            if (ReferenceEquals(_data.Field, field) && _data.TileStates.Equals(state.TileStates)) return;
            Volatile.Write(ref _data, new(field, _data.Version + 1, state.Input, state.TileStates));
        }
    }

    internal bool ApplyCosts(NavigationFlowInput input, NavigationCosts costs)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_input, input)) return false;
            SetInput(new(costs, input.Profile, input.Goals, input.GoalVersion, input.Version + 1, input.HeadingGraph));
            return true;
        }
    }

    private void SetInput(NavigationFlowInput input)
    {
        Volatile.Write(ref _input, input);
        Volatile.Write(ref _data, _data with { Version = _data.Version + 1 });
    }

    private sealed record NavigationFlowData(NavigationFlowField? Field, long Version,
        NavigationFlowInput? Input = null, ReadOnlyMemory<NavigationFlowTileState> TileStates = default);
}
