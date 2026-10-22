using System.Numerics;

namespace SomeEngine.Navigation;

public readonly record struct NavigationPathDependency(NavigationTileCoordinate Tile, ulong ContentVersion);

/// <summary>The selected polygon corridor, ordered world-space corners and data dependencies.</summary>
public sealed class NavigationPath(
    ReadOnlyMemory<int> corridor,
    ReadOnlyMemory<Vector3> corners,
    ReadOnlyMemory<NavigationPathDependency> dependencies = default,
    float cost = 0)
{
    public ReadOnlyMemory<int> Corridor { get; } = corridor;
    public ReadOnlyMemory<Vector3> Corners { get; } = corners;
    public ReadOnlyMemory<NavigationPathDependency> Dependencies { get; } = dependencies;
    public float Cost { get; } = cost;
}

public enum NavigationSearchStatus : byte
{
    Searching,
    Found,
    Unreachable,
}

public readonly record struct NavigationSearchStep(NavigationSearchStatus Status, NavigationPath? Path = null);

public readonly record struct NavigationSearchInput<TSpace>(
    TSpace Space,
    NavigationProfile Profile,
    NavigationPolicy Policy,
    Vector3 Start,
    Vector3 Goal,
    float GoalRange = 0,
    float StartHeading = 0,
    float? GoalHeading = null)
    where TSpace : class, INavigationSpace<TSpace>;

/// <summary>One concrete search implementation with a separate mutable workspace for each request.</summary>
public interface INavigationSearch<TSpace, TState>
    where TSpace : class, INavigationSpace<TSpace>
    where TState : class
{
    static abstract TState Create(in NavigationSearchInput<TSpace> input);
    static abstract NavigationSearchStep Advance(TState state, int expansionBudget);
}

/// <summary>Retains a request's workspace and result while successive jobs spend its search budget.</summary>
public sealed class NavigationSearch<TState>(TState state, ulong version) where TState : class
{
    public TState State { get; } = state;
    public ulong Version { get; } = version;
    public NavigationSearchStatus Status { get; internal set; } = NavigationSearchStatus.Searching;
    public NavigationPath? Path { get; internal set; }
}
