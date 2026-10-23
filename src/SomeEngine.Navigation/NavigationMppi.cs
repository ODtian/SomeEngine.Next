using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;

namespace SomeEngine.Navigation;

public sealed record NavigationMppiSettings
{
    public int CandidateCount { get; init; } = 128;
    public int HorizonSteps { get; init; } = 24;
    public float TimeStep { get; init; } = 0.1f;
    public int Iterations { get; init; } = 1;
    public Vector2 NoiseStandardDeviation { get; init; } = new(0.5f, 0.3f);
    public float NoiseCorrelation { get; init; } = 0.6f;
    public float Temperature { get; init; } = 0.3f;
    public float ControlWeight { get; init; } = 0.015f;
    public float PathWeight { get; init; } = 8;
    public float GoalWeight { get; init; } = 3;
    public float HeadingWeight { get; init; } = 2;
    public float NeighborWeight { get; init; } = 1;
    public float TraversalWeight { get; init; } = 1;
    public float CollisionResolution { get; init; } = 0.1f;
    public float ReferenceLookahead { get; init; } = 0.6f;
    public uint Seed { get; init; } = 1;
}

/// <summary>A two-input motion model, including its control bounds, complete traversal query and public output.</summary>
public interface INavigationMppiModel<TSpace> where TSpace : class, INavigationSpace<TSpace>
{
    static abstract Vector2 ReferenceControl(in NavigationMotion actual, NavigationPose reference,
        float desiredSpeed, NavigationProfile profile);
    static abstract Vector2 Constrain(Vector2 control, in NavigationMotion actual, NavigationProfile profile, float timeStep);
    static abstract NavigationMotion Integrate(in NavigationMotion actual, Vector2 control, NavigationProfile profile, float time);
    static abstract bool TryStep(TSpace space, in NavigationMotion actual, Vector2 control,
        NavigationProfile profile, NavigationPolicy policy, float time, out NavigationMotion next, out float cost);
    static abstract NavigationSteering Steering(Vector2 control, in NavigationMotion actual, in NavigationMotion next,
        NavigationProfile profile, in NavigationGuidance guidance, float timeStep);
}

/// <summary>Controls are signed speed and curvature, integrated by NavigationVehicleModel.</summary>
public readonly struct NavigationVehicleMppiModel : INavigationMppiModel<NavigationVehicleSpace>
{
    public static Vector2 ReferenceControl(in NavigationMotion actual, NavigationPose reference, float desiredSpeed, NavigationProfile profile)
    {
        Vector3 difference = reference.Position - actual.Position;
        float lateral = MathF.Cos(actual.Heading) * difference.X - MathF.Sin(actual.Heading) * difference.Z;
        float distanceSquared = difference.X * difference.X + difference.Z * difference.Z;
        float curvature = distanceSquared > 0.000001f ? 2 * lateral / distanceSquared : 0;
        return new(desiredSpeed, curvature);
    }

    public static Vector2 Constrain(Vector2 control, in NavigationMotion actual, NavigationProfile profile, float timeStep)
    {
        NavigationVehicleModel model = profile.VehicleModel!.Value;
        float currentSpeed = Vector3.Dot(actual.Velocity, NavigationHeading.Direction(actual.Heading));
        float change = profile.MaximumAcceleration * timeStep;
        float speed = Math.Clamp(control.X, MathF.Max(model.AllowReverse ? -profile.MaximumSpeed : 0, currentSpeed - change),
            MathF.Min(profile.MaximumSpeed, currentSpeed + change));
        float curvature = Math.Clamp(control.Y, -model.MaximumCurvature, model.MaximumCurvature);
        if (speed != 0) curvature = Math.Clamp(curvature, -profile.MaximumAngularSpeed / MathF.Abs(speed), profile.MaximumAngularSpeed / MathF.Abs(speed));
        return new(speed, curvature);
    }

    public static NavigationMotion Integrate(in NavigationMotion actual, Vector2 control, NavigationProfile profile, float time)
    {
        NavigationPose pose = profile.VehicleModel!.Value.Integrate(new(actual.Position, actual.Heading), new(control.X, control.Y, time), time);
        return actual with { Position = pose.Position, Heading = pose.Heading, Velocity = NavigationHeading.Direction(pose.Heading) * control.X };
    }

    public static bool TryStep(NavigationVehicleSpace space, in NavigationMotion actual, Vector2 control,
        NavigationProfile profile, NavigationPolicy policy, float time, out NavigationMotion next, out float cost)
    {
        bool traversable = space.TryTraverse(new(actual.Position, actual.Heading), new(control.X, control.Y, time), profile, policy,
            out NavigationPose pose, out cost);
        next = actual with { Position = pose.Position, Heading = pose.Heading, Velocity = NavigationHeading.Direction(pose.Heading) * control.X };
        return traversable;
    }

    public static NavigationSteering Steering(Vector2 control, in NavigationMotion actual, in NavigationMotion next,
        NavigationProfile profile, in NavigationGuidance guidance, float timeStep) =>
        new(next.Velocity, next.Heading, guidance.RequestVersion, guidance.Active,
            VehicleControl: new(control.X, control.Y, timeStep));
}

public readonly record struct NavigationMppi<TSpace, TModel>(TSpace Space, NavigationMppiSettings Settings) : IComponent
    where TSpace : class, INavigationSpace<TSpace>
    where TModel : struct, INavigationMppiModel<TSpace>
{
    internal NavigationMppiData<TSpace> Data { get; } = new();
}

public enum NavigationMppiStatus : byte { Idle, Ready, NoFeasibleTrajectory }
public readonly record struct NavigationMppiResult(NavigationMppiStatus Status, float Cost, ulong RequestVersion) : IComponent;

public static partial class NavigationComponents
{
    public static void AddMppi<TSpace, TModel>(World world, Entity entity, TSpace space,
        NavigationMppiSettings settings, AvoidanceAgent avoidance)
        where TSpace : class, INavigationSpace<TSpace>
        where TModel : struct, INavigationMppiModel<TSpace>
    {
        if (world.Has<AvoidanceAgent>(entity)) world.Replace(entity, avoidance);
        else world.Add(entity, avoidance);
        if (world.Has<NavigationAvoidanceWork>(entity)) world.Remove<NavigationAvoidanceWork>(entity);
        world.Add(entity, new NavigationMppi<TSpace, TModel>(space, settings));
        world.Add(entity, default(NavigationMppiResult));
        SetTrajectorySource(world, entity, NavigationTrajectoryKind.Mppi);
    }
}

internal readonly struct NavigationMppiResources<TSpace, TModel> : IJobComponentResources<NavigationMppi<TSpace, TModel>>
    where TSpace : class, INavigationSpace<TSpace>
    where TModel : struct, INavigationMppiModel<TSpace>
{
    public static long GetVersion(in NavigationMppi<TSpace, TModel> component) => TSpace.GetVersion(component.Space);
    public static void Collect(in NavigationMppi<TSpace, TModel> component, ref JobResourceAccessBuilder resources)
    {
        resources.Exclusive(component.Data);
        TSpace.CollectResources(component.Space, ref resources);
    }
}

internal sealed class NavigationMppiData<TSpace> where TSpace : class, INavigationSpace<TSpace>
{
    internal Vector2[] Controls = [], Noise = [], CandidateControls = [], UpdatedControls = [], SelectedControls = [];
    internal NavigationMotion[] CandidateMotions = [], SelectedMotions = [];
    internal NavigationPose[] Reference = [];
    internal float[] ReferenceSpeeds = [], Scores = [], Weights = [];
    internal NavigationMppiFrame<TSpace> Frame = null!;
    internal NavigationMppiSettings? Settings;
    internal NavigationPath? Path;
    internal int Sequence, ReferenceIndex, SelectedCount;
    internal ulong Version;
    internal float LastTime, ShiftTime, SelectedCost;
    internal bool Initialized, Valid;
    internal NavigationSteering Steering;

    internal void Configure(NavigationMppiSettings settings)
    {
        if (Settings == settings) return;
        int steps = settings.HorizonSteps, candidates = settings.CandidateCount;
        Controls = new Vector2[steps];
        UpdatedControls = new Vector2[steps];
        SelectedControls = new Vector2[steps];
        Noise = new Vector2[steps * candidates];
        CandidateControls = new Vector2[steps * candidates];
        CandidateMotions = new NavigationMotion[(steps + 1) * candidates];
        SelectedMotions = new NavigationMotion[steps + 1];
        Reference = new NavigationPose[steps + 1];
        ReferenceSpeeds = new float[steps];
        Scores = new float[candidates];
        Weights = new float[candidates];
        Settings = settings;
        Initialized = false;
    }
}

internal sealed record NavigationMppiFrame<TSpace>(TSpace Space, NavigationProfile Profile, NavigationPolicy Policy,
    NavigationMppiSettings Settings, NavigationMotion Actual, NavigationGuidance Guidance, NavigationGoal Goal,
    Vector3 Target, NavigationNeighbors Neighbors, int Actor, float Time, float DesiredSpeed)
    where TSpace : class, INavigationSpace<TSpace>;
