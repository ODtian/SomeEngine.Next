using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Navigation.Tests;

public sealed class AccelerationVelocityAvoidanceTests
{
    [Fact]
    public void ActorsWithDifferentAccelerationAndResponsePassUsingActualFeedback()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 3, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        using World world = WorldJobs.CreateWorld(scheduler: scheduler);
        var paths = new NavigationSystems<NavigationMesh, PolygonNavigationSearch, PolygonSearchState, TestMotion, TestMotionReader>(world);
        var neighbors = new NavigationNeighborSystem<TestMotion, TestMotionReader>(world, scheduler);
        var avoidance = new NavigationAvoidanceSystem<AccelerationVelocityAvoidance, TestMotion, TestMotionReader>(world, neighbors);
        NavigationMesh mesh = Corridor();
        Entity first = Add(new(1, 0, 2.8f), new(1.2f, 0, 0), new(11, 0, 2.8f), 0.8f, 0.6f);
        Entity second = Add(new(11, 0, 3.2f), new(-1.2f, 0, 0), new(1, 0, 3.2f), 1.5f, 1);
        Entity[] actors = [first, second];
        float minimumSeparation = float.PositiveInfinity;

        for (int step = 0; step < 180; step++)
        {
            TestMotion[] before = actors.Select(world.Read<TestMotion>).ToArray();
            JobHandle guidance = paths.ScheduleGuidance(0.1f, 32).Guidance;
            JobHandle steering = avoidance.Schedule(0.1f, guidance, neighbors.Schedule(), avoidance.SchedulePathBoundaries<NavigationMesh>());
            JobHandle moved = new ConsumeResponseSteering(0.1f).ScheduleParallel(world, steering);
            paths.ScheduleProgress(moved).Complete();
            for (int index = 0; index < actors.Length; index++)
            {
                Entity actor = actors[index];
                NavigationSteering selected = world.Read<NavigationSteering>(actor);
                NavigationProfile profile = world.Read<NavigationAgent>(actor).Profile;
                TestMotion after = world.Read<TestMotion>(actor);
                Assert.True(Vector3.Distance(before[index].Velocity, selected.Velocity) <= profile.MaximumAcceleration * selected.Response.TimeConstant + 0.0001f);
                Assert.True(Vector3.Distance(before[index].Velocity, after.Velocity) <= profile.MaximumAcceleration * 0.1f + 0.0001f);
                Assert.True(after.Velocity.Length() <= profile.MaximumSpeed + 0.0001f);
            }
            minimumSeparation = MathF.Min(minimumSeparation,
                Vector3.Distance(world.Read<TestMotion>(first).Position, world.Read<TestMotion>(second).Position));
        }

        Assert.True(minimumSeparation >= 0.69f, $"minimum separation: {minimumSeparation}");
        Assert.True(world.Read<TestMotion>(first).Position.X > 10);
        Assert.True(world.Read<TestMotion>(second).Position.X < 2);

        Entity Add(Vector3 position, Vector3 velocity, Vector3 goal, float acceleration, float response)
        {
            var profile = new NavigationProfile([new Vector2(-0.2f), new(0.2f, -0.2f), new(0.2f), new(-0.2f, 0.2f)],
                1, maximumSpeed: 1.5f, maximumAcceleration: acceleration);
            Entity entity = world.CreateEntity(new TestMotion { Position = position, Velocity = velocity });
            paths.AddAgent(entity, mesh, profile, new(), new(goal, 0.15f), 1.5f);
            NavigationComponents.AddAvoidance(world, entity,
                new(0.3f, NeighborDistance: 8, TimeHorizon: 4, BoundaryTimeHorizon: 4, Spacing: 0.05f, ResponseTime: response));
            return entity;
        }
    }

    [Fact]
    public void FiniteAccelerationBrakesAgainstLocalBoundarySegments()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        using World world = WorldJobs.CreateWorld(scheduler: scheduler);
        var profile = new NavigationProfile([new Vector2(-0.2f), new(0.2f, -0.2f), new(0.2f), new(-0.2f, 0.2f)],
            1, maximumSpeed: 1.6f, maximumAcceleration: 0.8f);
        Entity entity = world.CreateEntity(new TestMotion { Position = new(8, 0, 1.5f), Velocity = new(1, 0, -0.3f) });
        world.Add(entity, new NavigationAgent(profile, new(), 1.6f));
        world.Add(entity, new PathNavigation<NavigationMesh>(Corridor()));
        world.Add(entity, new NavigationGuidance(new(1, 0, -1), 0, 1, true));
        world.Add(entity, default(NavigationSteering));
        NavigationComponents.AddAvoidance(world, entity, new(0.3f, BoundaryTimeHorizon: 4, Spacing: 0.05f, ResponseTime: 0.8f));
        var neighbors = new NavigationNeighborSystem<TestMotion, TestMotionReader>(world, scheduler);
        var avoidance = new NavigationAvoidanceSystem<AccelerationVelocityAvoidance, TestMotion, TestMotionReader>(world, neighbors);

        for (int step = 0; step < 120; step++)
        {
            JobHandle steering = avoidance.Schedule(0.1f, default, neighbors.Schedule(), avoidance.SchedulePathBoundaries<NavigationMesh>());
            new ConsumeResponseSteering(0.1f).ScheduleParallel(world, steering).Complete();
            Vector3 position = world.Read<TestMotion>(entity).Position;
            Assert.InRange(position.X, 8, 11.66f);
            Assert.InRange(position.Z, 0.34f, 1.5f);
        }

        Assert.True(world.Read<TestMotion>(entity).Position.X > 11);
        Assert.True(world.Read<TestMotion>(entity).Position.Z < 0.6f);
        Assert.True(world.Read<TestMotion>(entity).Velocity.Length() < 0.1f);
    }

    // These corridor dimensions are an example used to observe the shared model's public movement result.
    private static NavigationMesh Corridor() => new(
        [new(0, 0, 0), new(12, 0, 0), new(12, 0, 6), new(0, 0, 6)], [0, 1, 2, 3], [new(0, 4, 0, 0)], []);
}

public partial struct ConsumeResponseSteering(float deltaTime) : IJobEntity
{
    private readonly float _deltaTime = deltaTime;

    public void Execute(in NavigationSteering steering, ref TestMotion motion)
    {
        NavigationVelocityState result = steering.Response.Integrate(motion.Position, motion.Velocity, steering.Velocity, _deltaTime);
        motion.Position = result.Position;
        motion.Velocity = result.Velocity;
        if (steering.Active) motion.Heading = steering.Heading;
    }
}
