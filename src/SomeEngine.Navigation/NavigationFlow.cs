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
    ReadOnlyMemory<NavigationFlowGoal> goals, ulong goalVersion = 1, ulong version = 1)
{
    public NavigationCosts Costs { get; } = costs;
    public NavigationProfile Profile { get; } = profile;
    public ReadOnlyMemory<NavigationFlowGoal> Goals { get; } = goals;
    public ulong GoalVersion { get; } = goalVersion;
    public ulong Version { get; } = version;
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

    /// <summary>Samples the installed direction on the position's support layer.</summary>
    public Vector3 SampleDirection(Vector3 position)
    {
        int cell = Input.Costs.Grid.FindCell(position);
        return cell >= 0 ? NavigationFlowSampler.Direction(this, cell, position) : Vector3.Zero;
    }
}

/// <summary>The game's shared target and its most recently completed field.</summary>
public sealed class NavigationFlow
{
    private readonly Lock _gate = new();
    private NavigationFlowInput _input;
    private NavigationFlowData _data = new(null, 0);

    public NavigationFlow(NavigationCosts costs, NavigationProfile profile, NavigationFlowGoal[] goals)
        : this(costs, profile, (ReadOnlyMemory<NavigationFlowGoal>)goals) { }

    public NavigationFlow(NavigationCosts costs, NavigationProfile profile, ReadOnlyMemory<NavigationFlowGoal> goals) =>
        _input = new(costs, profile, goals);

    public NavigationFlow(NavigationFlowInput input) => _input = input;

    public NavigationFlow(NavigationFlowField field)
    {
        _input = field.Input;
        _data = new(field, 1);
    }

    public NavigationFlowInput Input => Volatile.Read(ref _input);
    public NavigationFlowField? Field => Volatile.Read(ref _data).Field;
    public long FieldVersion => Volatile.Read(ref _data).Version;
    public NavigationCosts Costs => Input.Costs;
    public NavigationProfile Profile => Input.Profile;
    public ReadOnlyMemory<NavigationFlowGoal> Goals => Input.Goals;

    public void SetGoals(NavigationFlowGoal[] goals) => SetGoals((ReadOnlyMemory<NavigationFlowGoal>)goals);

    public void SetGoals(ReadOnlyMemory<NavigationFlowGoal> goals)
    {
        lock (_gate)
        {
            NavigationFlowInput current = _input;
            Volatile.Write(ref _input, new(current.Costs, current.Profile, goals, current.GoalVersion + 1, current.Version + 1));
        }
    }

    public void SetCosts(NavigationCosts costs)
    {
        lock (_gate)
        {
            NavigationFlowInput current = _input;
            Volatile.Write(ref _input, new(costs, current.Profile, current.Goals, current.GoalVersion, current.Version + 1));
        }
    }

    internal void Apply(NavigationFlowField field)
    {
        lock (_gate)
            if (ReferenceEquals(_input, field.Input) && !ReferenceEquals(_data.Field, field))
                Volatile.Write(ref _data, new(field, _data.Version + 1));
    }

    internal bool ApplyCosts(NavigationFlowInput input, NavigationCosts costs)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_input, input)) return false;
            Volatile.Write(ref _input, new(costs, input.Profile, input.Goals, input.GoalVersion, input.Version + 1));
            return true;
        }
    }

    private sealed record NavigationFlowData(NavigationFlowField? Field, long Version);
}
