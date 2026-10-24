using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Navigation;

/// <summary>A sample in the caller's time coordinates, beginning at actual movement feedback.</summary>
public readonly record struct NavigationTrajectoryPoint(float Time, Vector3 Position, Vector3 Velocity, float Heading) : IBufferElement;

public enum NavigationTrajectoryKind : byte { ModelPrediction, Mppi }
public readonly record struct NavigationTrajectorySource(NavigationTrajectoryKind Kind) : IComponent;

public interface INavigationPredictionModel
{
    static abstract NavigationMotion Integrate(in NavigationMotion initial, in NavigationSteering steering,
        NavigationProfile profile, float elapsedTime);
}

/// <summary>Uses the completed Steering response and the profile's angular-speed limit.</summary>
public readonly struct NavigationVelocityPrediction : INavigationPredictionModel
{
    public static NavigationMotion Integrate(in NavigationMotion initial, in NavigationSteering steering,
        NavigationProfile profile, float elapsedTime)
    {
        Vector3 target = steering.Active ? steering.Velocity : Vector3.Zero;
        NavigationVelocityState result = steering.Response.Integrate(initial.Position, initial.Velocity, target, elapsedTime);
        float turn = steering.Active ? NavigationHeading.Difference(steering.Heading, initial.Heading) : 0;
        float maximumTurn = profile.MaximumAngularSpeed * elapsedTime;
        float heading = initial.Heading + Math.Clamp(turn, -maximumTurn, maximumTurn);
        return new(result.Position, result.Velocity, heading, initial.Grounded, initial.Layer);
    }
}

/// <summary>Selects one prediction model and its sampling interval for an actor.</summary>
public readonly record struct NavigationPrediction<TModel>(float Horizon, float Interval) : IComponent
    where TModel : struct, INavigationPredictionModel;

public static partial class NavigationComponents
{
    public static void AddPrediction<TModel>(World world, Entity entity, float horizon, float interval)
        where TModel : struct, INavigationPredictionModel
    {
        world.Add(entity, new NavigationPrediction<TModel>(horizon, interval));
        SetTrajectorySource(world, entity, NavigationTrajectoryKind.ModelPrediction);
    }

    internal static void SetTrajectorySource(World world, Entity entity, NavigationTrajectoryKind kind)
    {
        var source = new NavigationTrajectorySource(kind);
        if (world.Has<NavigationTrajectorySource>(entity)) world.Replace(entity, source);
        else world.Add(entity, source);
        if (!world.HasBuffer<NavigationTrajectoryPoint>(entity)) world.AddBuffer<NavigationTrajectoryPoint>(entity);
    }
}

/// <summary>Writes one completed trajectory per actor after actual motion and selected Steering are ready.</summary>
public sealed class NavigationPredictionSystem<TModel, TMotion, TReader>(World world)
    where TModel : struct, INavigationPredictionModel
    where TMotion : unmanaged, IComponent
    where TReader : struct, INavigationMotionReader<TMotion>
{
    private readonly QueryDefinition _actors = world.QueryDefinition().All<NavigationPrediction<TModel>>().Build();
    private JobHandle _completion;

    public JobHandle Schedule(float time, JobHandle steeringReady, JobHandle motionReady = default)
    {
        JobHandle ready = WorldJobs.CombineDependencies(world, [_completion, steeringReady, motionReady]);
        _completion = new NavigationPredictionJob<TModel, TMotion, TReader>(time).ScheduleParallel(world,
            new JobEntityScheduleOptions(filter: _actors), ready);
        return _completion;
    }
}

internal partial struct NavigationPredictionJob<TModel, TMotion, TReader>(float time) : IJobEntity
    where TModel : struct, INavigationPredictionModel
    where TMotion : unmanaged, IComponent
    where TReader : struct, INavigationMotionReader<TMotion>
{
    private readonly float _time = time;

    public void Execute(in NavigationPrediction<TModel> settings, in NavigationTrajectorySource source,
        [JobResources<NavigationAgentResources>] in NavigationAgent agent,
        in NavigationSteering steering, in TMotion movement, DynamicBuffer<NavigationTrajectoryPoint> trajectory)
    {
        if (source.Kind != NavigationTrajectoryKind.ModelPrediction) return;
        NavigationMotion initial = TReader.Read(in movement);
        trajectory.Clear();
        trajectory.Add(new(_time, initial.Position, initial.Velocity, initial.Heading));
        int count = (int)MathF.Ceiling(settings.Horizon / settings.Interval);
        for (int sample = 1; sample <= count; sample++)
        {
            float elapsed = MathF.Min(sample * settings.Interval, settings.Horizon);
            NavigationMotion predicted = TModel.Integrate(in initial, in steering, agent.Profile, elapsed);
            trajectory.Add(new(_time + elapsed, predicted.Position, predicted.Velocity, predicted.Heading));
        }
    }
}
