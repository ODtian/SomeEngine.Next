using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;

namespace SomeEngine.Navigation;

/// <summary>
/// XZ disk model. Radius covers the profile's rotation footprint; Spacing expands this disk.
/// Time horizons are seconds. Responsibility is the positive share of pair correction.
/// Matching interaction masks and actual movement layers share a neighbor set.
/// </summary>
public readonly record struct AvoidanceAgent(float Radius, float NeighborDistance = 5, int MaximumNeighbors = 32,
    float TimeHorizon = 2, float BoundaryTimeHorizon = 2, float Spacing = 0,
    float Responsibility = 1, ulong InteractionMask = ulong.MaxValue) : IComponent;

public readonly record struct NavigationNeighborAgent(Entity Entity, NavigationMotion Motion,
    AvoidanceAgent Avoidance, NavigationProfile Profile)
{
    public float Radius => Avoidance.Radius + Avoidance.Spacing;
}

/// <summary>A directed boundary with free space on its right; clearance is already baked into the space.</summary>
public readonly record struct NavigationBoundarySegment(Vector3 Start, Vector3 End, float BakedRadius = 0);

public struct NavigationVelocityConstraint(Vector2 point, Vector2 direction)
{
    public Vector2 Point = point;
    public Vector2 Direction = direction;
}

public readonly ref struct NavigationAvoidanceInput(NavigationNeighborAgent self,
    ReadOnlySpan<NavigationNeighborAgent> agents, ReadOnlySpan<int> neighbors,
    ReadOnlySpan<NavigationBoundarySegment> boundaries, Vector2 preferredVelocity, float deltaTime)
{
    public NavigationNeighborAgent Self { get; } = self;
    public ReadOnlySpan<NavigationNeighborAgent> Agents { get; } = agents;
    public ReadOnlySpan<int> Neighbors { get; } = neighbors;
    public ReadOnlySpan<NavigationBoundarySegment> Boundaries { get; } = boundaries;
    public Vector2 PreferredVelocity { get; } = preferredVelocity;
    public float DeltaTime { get; } = deltaTime;
}

public interface INavigationAvoidanceSolver
{
    static abstract Vector2 Solve(in NavigationAvoidanceInput input, NavigationAvoidanceWorkspace workspace);
}

/// <summary>Exclusive per-actor query and numerical workspace, reused by concrete avoidance solvers.</summary>
public sealed class NavigationAvoidanceWorkspace
{
    public List<NavigationVelocityConstraint> Constraints { get; } = [];
    public List<NavigationVelocityConstraint> ProjectedConstraints { get; } = [];
    public List<NavigationBoundarySegment> Boundaries { get; } = [];
    internal Queue<int> SearchQueue { get; } = [];
    internal HashSet<int> Visited { get; } = [];
    internal List<Vector2> Openings { get; } = [];
}

internal readonly record struct NavigationAvoidanceWork(NavigationAvoidanceWorkspace Workspace) : IComponent;

internal readonly struct NavigationAvoidanceWorkResources : IJobComponentResources<NavigationAvoidanceWork>
{
    public static void Collect(in NavigationAvoidanceWork component, ref JobResourceAccessBuilder resources) =>
        resources.Exclusive(component.Workspace);
}

public static partial class NavigationComponents
{
    public static void AddAvoidance(World world, Entity entity, AvoidanceAgent settings)
    {
        world.Add(entity, settings);
        world.Add(entity, new NavigationAvoidanceWork(new()));
    }
}
