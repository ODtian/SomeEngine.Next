using System.Numerics;

namespace SomeEngine.Navigation;

public readonly record struct NavigationPathDependency(NavigationTileCoordinate Tile, ulong ContentVersion, int Region = 0);

/// <summary>The selected polygon corridor, ordered world-space corners and data dependencies.</summary>
public sealed class NavigationPath(
    ReadOnlyMemory<int> corridor,
    ReadOnlyMemory<Vector3> corners,
    ReadOnlyMemory<NavigationPathDependency> dependencies = default,
    float cost = 0,
    NavigationRegionRoute? regionRoute = null,
    ReadOnlyMemory<NavigationPathSection> sections = default,
    ReadOnlyMemory<NavigationConnectionDependency> connectionDependencies = default,
    ReadOnlyMemory<NavigationSceneTileId> requiredTiles = default,
    ReadOnlyMemory<NavigationPose> poses = default,
    ReadOnlyMemory<NavigationMotionSegment> motionSegments = default)
{
    public ReadOnlyMemory<int> Corridor { get; } = corridor;
    public ReadOnlyMemory<Vector3> Corners { get; } = corners;
    public ReadOnlyMemory<NavigationPathDependency> Dependencies { get; } = dependencies;
    public float Cost { get; } = cost;
    public NavigationRegionRoute? RegionRoute { get; } = regionRoute;
    public ReadOnlyMemory<NavigationPathSection> Sections { get; } = sections;
    public ReadOnlyMemory<NavigationConnectionDependency> ConnectionDependencies { get; } = connectionDependencies;
    public ReadOnlyMemory<NavigationSceneTileId> RequiredTiles { get; } = requiredTiles;
    public ReadOnlyMemory<NavigationPose> Poses { get; } = poses;
    public ReadOnlyMemory<NavigationMotionSegment> MotionSegments { get; } = motionSegments;

    public bool TrySampleHeight(Vector3 position, out float height)
    {
        height = position.Y;
        float closest = float.PositiveInfinity;
        bool found = false;
        foreach (NavigationPathSection section in Sections.Span)
        {
            if (!NavigationMesh.TryGetHeight(section.Mesh, position, out float candidate)) continue;
            float distance = MathF.Abs(candidate - position.Y);
            if (distance >= closest) continue;
            closest = distance;
            height = candidate;
            found = true;
        }
        return found;
    }
}

public enum NavigationSearchStatus : byte
{
    Searching,
    Found,
    Unreachable,
    WaitingForTiles,
}

public readonly record struct NavigationSearchStep(NavigationSearchStatus Status, NavigationPath? Path = null, bool ResetPathProgress = false);

public readonly record struct NavigationSearchInput<TSpace>(
    TSpace Space,
    NavigationProfile Profile,
    NavigationPolicy Policy,
    Vector3 Start,
    Vector3 Goal,
    float GoalRange = 0,
    float StartHeading = 0,
    float? GoalHeading = null,
    float GoalHeadingTolerance = 0.05f)
    where TSpace : class, INavigationSpace<TSpace>;

/// <summary>One concrete search implementation with a separate mutable workspace for each request.</summary>
public interface INavigationSearch<TSpace, TState>
    where TSpace : class, INavigationSpace<TSpace>
    where TState : class
{
    static abstract TState Create(in NavigationSearchInput<TSpace> input);
    static abstract NavigationSearchStep Advance(TState state, int expansionBudget);
    static virtual bool Update(TState state, in NavigationSearchInput<TSpace> input) => false;
}

/// <summary>Retains a request's workspace and result while successive jobs spend its search budget.</summary>
public sealed class NavigationSearch<TState>(TState state, ulong version) where TState : class
{
    public TState State { get; } = state;
    public ulong Version { get; } = version;
    public NavigationSearchStatus Status { get; internal set; } = NavigationSearchStatus.Searching;
    public NavigationPath? Path { get; internal set; }
}
