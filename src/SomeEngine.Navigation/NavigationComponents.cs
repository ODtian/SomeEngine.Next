using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;

namespace SomeEngine.Navigation;

public readonly record struct NavigationAgent(NavigationProfile Profile, NavigationPolicy Policy, float DesiredSpeed) : IComponent;

public readonly record struct PathNavigation<TSpace>(TSpace Space) : IComponent
    where TSpace : class, INavigationSpace<TSpace>;

/// <summary>A world-space point or entity-relative goal. World.Enable/Disable controls navigation.</summary>
public readonly record struct NavigationGoal : IEnableableComponent
{
    public NavigationGoal(Vector3 position, float arrivalRange = 0, float? heading = null, float headingTolerance = 0.05f)
    {
        Position = position;
        ArrivalRange = arrivalRange;
        Heading = heading;
        HeadingTolerance = headingTolerance;
    }

    public NavigationGoal(Entity target, float arrivalRange = 0, float? heading = null,
        float headingTolerance = 0.05f, Vector3 offset = default)
        : this(offset, arrivalRange, heading, headingTolerance)
    {
        Target = target;
        IsEntityTarget = true;
    }

    /// <summary>World position for point goals; world-space offset from the entity for entity goals.</summary>
    public Vector3 Position { get; init; }
    public Entity Target { get; init; }
    public bool IsEntityTarget { get; init; }
    public float ArrivalRange { get; init; }
    public float? Heading { get; init; }
    public float HeadingTolerance { get; init; }
}

public readonly record struct NavigationPathResult(
    NavigationPath? Path,
    ulong RequestVersion,
    NavigationSearchStatus Status,
    Vector3 TargetPosition,
    bool HasTarget) : IComponent;

public readonly record struct NavigationGuidance(Vector3 Velocity, float Heading, ulong RequestVersion, bool Active,
    NavigationVehicleControl? VehicleControl = null) : IComponent;

/// <summary>The selected target velocity and the response model used by movement and prediction.</summary>
public readonly record struct NavigationSteering(Vector3 Velocity, float Heading, ulong RequestVersion, bool Active,
    NavigationVelocityResponse Response = default, NavigationVehicleControl? VehicleControl = null) : IComponent;

public enum NavigationStatus : byte
{
    Idle,
    Searching,
    Moving,
    Aligning,
    Arrived,
    Unreachable,
    TargetUnavailable,
    WaitingForTiles,
}

/// <summary>Distance is the geometric distance from actual position to the goal region.</summary>
public readonly record struct NavigationProgress(
    NavigationStatus Status, float Distance, float HeadingError, ulong RequestVersion) : IComponent;

/// <summary>Adds an entity's navigation inputs, outputs and its private search workspace.</summary>
public static partial class NavigationComponents
{
    public static void AddPath<TSpace, TState>(World world, Entity entity, TSpace space,
        NavigationProfile profile, NavigationPolicy policy, NavigationGoal goal, float desiredSpeed)
        where TSpace : class, INavigationSpace<TSpace>
        where TState : class
    {
        world.Add(entity, new NavigationAgent(profile, policy, desiredSpeed));
        world.Add(entity, new PathNavigation<TSpace>(space));
        world.Add(entity, goal);
        world.Add(entity, default(NavigationPathResult));
        world.Add(entity, default(NavigationGuidance));
        world.Add(entity, default(NavigationSteering));
        world.Add(entity, default(NavigationProgress));
        world.Add(entity, new NavigationRequest<TSpace, TState>(new()));
    }
}

internal readonly record struct NavigationRequest<TSpace, TState>(NavigationRequestData<TSpace, TState> Data) : IComponent
    where TSpace : class, INavigationSpace<TSpace>
    where TState : class;

internal sealed class NavigationRequestData<TSpace, TState>
    where TSpace : class, INavigationSpace<TSpace>
    where TState : class
{
    public TSpace? Space;
    public NavigationProfile? Profile;
    public NavigationPolicy? Policy;
    public NavigationGoal Goal;
    public Vector3 TargetPosition;
    public NavigationSearch<TState>? Search;
    public ulong Version;
    public int CornerIndex;
    public bool Active;
    public bool TargetAvailable;
}
