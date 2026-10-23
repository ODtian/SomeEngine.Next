using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Navigation.Tests;

public sealed class NavigationPredictionTests
{
    [Fact]
    public void AnimationReadsFutureTurningAndTheNextPredictionStartsAtActualFeedback()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        using World world = WorldJobs.CreateWorld(scheduler: scheduler);
        var profile = new NavigationProfile([new Vector2(-0.2f), new(0.2f, -0.2f), new(0.2f), new(-0.2f, 0.2f)],
            1, maximumSpeed: 1, maximumAcceleration: 2, maximumAngularSpeed: 1);
        // The open square and imposed displacement are examples of navigation and physical movement feedback.
        var mesh = new NavigationMesh([new(-10, 0, -10), new(10, 0, -10), new(10, 0, 10), new(-10, 0, 10)],
            [0, 1, 2, 3], [new(0, 4, 0, 0)], []);
        var paths = new NavigationSystems<NavigationMesh, PolygonNavigationSearch, PolygonSearchState, TestMotion, TestMotionReader>(world);
        var neighbors = new NavigationNeighborSystem<TestMotion, TestMotionReader>(world, scheduler);
        var avoidance = new NavigationAvoidanceSystem<AccelerationVelocityAvoidance, TestMotion, TestMotionReader>(world, neighbors);
        var prediction = new NavigationPredictionSystem<NavigationVelocityPrediction, TestMotion, TestMotionReader>(world);
        Entity entity = world.CreateEntity(new TestMotion());
        world.Add(entity, new AnimationMotionDemand());
        paths.AddAgent(entity, mesh, profile, new(), new(new Vector3(8, 0, 0)), 1);
        NavigationComponents.AddAvoidance(world, entity, new(0.3f, ResponseTime: 0.5f));
        NavigationComponents.AddPrediction<NavigationVelocityPrediction>(world, entity, horizon: 0.8f, interval: 0.2f);

        JobHandle guidance = paths.ScheduleGuidance(0.1f, 32).Guidance;
        JobHandle steering = avoidance.Schedule(0.1f, guidance, neighbors.Schedule(), avoidance.SchedulePathBoundaries<NavigationMesh>());
        JobHandle predicted = prediction.Schedule(100, steering);
        new ReadAnimationMotion().ScheduleParallel(world, predicted).Complete();
        AnimationMotionDemand first = world.Read<AnimationMotionDemand>(entity);
        Assert.Equal(100, first.StartTime);
        Assert.Equal(100.8f, first.EndTime, 4);
        Assert.InRange(first.FutureHeading, 0.79f, 0.81f);
        Assert.True(first.FutureVelocity.X > 0.5f);

        JobHandle moved = new DeflectedPredictedMotion(0.1f, new(0, 0, 0.2f)).ScheduleParallel(world, predicted);
        paths.ScheduleProgress(moved).Complete();
        TestMotion actual = world.Read<TestMotion>(entity);
        guidance = paths.ScheduleGuidance(0.1f, 32).Guidance;
        steering = avoidance.Schedule(0.1f, guidance, neighbors.Schedule(), avoidance.SchedulePathBoundaries<NavigationMesh>());
        new ReadAnimationMotion().ScheduleParallel(world, prediction.Schedule(100.1f, steering)).Complete();
        AnimationMotionDemand next = world.Read<AnimationMotionDemand>(entity);

        Assert.Equal(actual.Position, next.StartPosition);
        Assert.Equal(actual.Velocity, next.StartVelocity);
        Assert.Equal(0.2f, next.StartPosition.Z, 4);
        Assert.Equal(100.1f, next.StartTime);
        Assert.True(next.FutureHeading > actual.Heading);
    }
}

public record struct AnimationMotionDemand(Vector3 StartPosition, Vector3 StartVelocity,
    float StartTime, float EndTime, float FutureHeading, Vector3 FutureVelocity) : IComponent;

public partial struct ReadAnimationMotion : IJobEntity
{
    public void Execute(BufferView<NavigationTrajectoryPoint> trajectory, ref AnimationMotionDemand demand)
    {
        NavigationTrajectoryPoint first = trajectory[0], last = trajectory[trajectory.Count - 1];
        demand = new(first.Position, first.Velocity, first.Time, last.Time, last.Heading, last.Velocity);
    }
}

public partial struct DeflectedPredictedMotion(float deltaTime, Vector3 displacement) : IJobEntity
{
    private readonly float _deltaTime = deltaTime;
    private readonly Vector3 _displacement = displacement;

    public void Execute([JobResources<NavigationAgentResources>] in NavigationAgent agent,
        in NavigationSteering steering, ref TestMotion actual)
    {
        NavigationMotion initial = TestMotionReader.Read(in actual);
        NavigationMotion next = NavigationVelocityPrediction.Integrate(in initial, in steering, agent.Profile, _deltaTime);
        actual = new() { Position = next.Position + _displacement, Velocity = next.Velocity, Heading = next.Heading };
    }
}
