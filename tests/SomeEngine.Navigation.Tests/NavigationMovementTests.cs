using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Navigation.Tests;

public sealed class NavigationMovementTests
{
    [Fact]
    public void MovementConsumerFollowsThePathAroundAnObstacleAndReportsArrival()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig
        {
            WorkerCount = 4,
            ManagedPayloadPolicy = ManagedPayloadPolicy.Allow,
        });
        using World world = WorldJobs.CreateWorld(scheduler: scheduler);
        var profile = new NavigationProfile(
            [new(-0.2f, -0.2f), new(0.2f, -0.2f), new(0.2f, 0.2f), new(-0.2f, 0.2f)],
            1.8f, maximumSpeed: 2f);
        var navigation = new NavigationSystems<NavigationMesh, PolygonNavigationSearch,
            PolygonSearchState, TestMotion, TestMotionReader>(world);
        Entity entity = world.CreateEntity(new TestMotion { Position = new(1, 0, 1) });
        navigation.AddAgent(entity, ObstacleMesh(), profile, new NavigationPolicy(),
            new NavigationGoal(new Vector3(5, 0, 1), 0.1f), desiredSpeed: 2f);

        for (int step = 0; step < 100 && world.Read<NavigationProgress>(entity).Status != NavigationStatus.Arrived; step++)
        {
            NavigationStep scheduled = navigation.Schedule(0.1f, expansionBudget: 2);
            JobHandle moved = new ConsumeSteering(0.1f).ScheduleParallel(world, scheduled.Steering);
            navigation.ScheduleProgress(moved).Complete();
            Vector3 position = world.Read<TestMotion>(entity).Position;
            Assert.False(position.X > 2.001f && position.X < 3.999f && position.Z < 1.999f);
        }

        Assert.Equal(NavigationStatus.Arrived, world.Read<NavigationProgress>(entity).Status);
    }

    [Fact]
    public void ReplacingTheGoalContinuesMovementTowardTheNewPoint()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        using World world = WorldJobs.CreateWorld(scheduler: scheduler);
        var navigation = new NavigationSystems<NavigationMesh, PolygonNavigationSearch,
            PolygonSearchState, TestMotion, TestMotionReader>(world);
        Entity entity = world.CreateEntity(new TestMotion { Position = new(2, 0, 1) });
        navigation.AddAgent(entity, FlatMesh(), Profile(), new NavigationPolicy(),
            new NavigationGoal(position: new(8, 0, 1)), desiredSpeed: 2f);
        Move(navigation, world);
        NavigationPathResult previous = world.Read<NavigationPathResult>(entity);
        Vector3 before = world.Read<TestMotion>(entity).Position;

        world.Replace(entity, new NavigationGoal(position: new(1, 0, 1)));
        Move(navigation, world);

        NavigationPathResult changed = world.Read<NavigationPathResult>(entity);
        Assert.True(changed.RequestVersion > previous.RequestVersion);
        Assert.True(world.Read<TestMotion>(entity).Position.X < before.X);
    }

    [Fact]
    public void EntityGoalUsesTheTargetsCurrentPositionAndReportsDistanceToItsRange()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        using World world = WorldJobs.CreateWorld(scheduler: scheduler);
        var navigation = new NavigationSystems<NavigationMesh, PolygonNavigationSearch,
            PolygonSearchState, TestMotion, TestMotionReader>(world);
        Entity target = world.CreateEntity(new TestMotion { Position = new(5, 0, 1) });
        Entity entity = world.CreateEntity(new TestMotion { Position = new(1, 0, 1) });
        navigation.AddAgent(entity, FlatMesh(), Profile(), new NavigationPolicy(),
            new NavigationGoal(target, arrivalRange: 0.5f), desiredSpeed: 2f);
        Move(navigation, world);

        world.Replace(target, new TestMotion { Position = new(0, 0, 1) });
        Move(navigation, world);

        Assert.Equal(new Vector3(0, 0, 1), world.Read<NavigationPathResult>(entity).TargetPosition);
        Assert.Equal(0.5f, world.Read<NavigationProgress>(entity).Distance, 4);
    }

    [Fact]
    public void GoalEnablementPausesAndResumesMovement()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        using World world = WorldJobs.CreateWorld(scheduler: scheduler);
        var navigation = new NavigationSystems<NavigationMesh, PolygonNavigationSearch,
            PolygonSearchState, TestMotion, TestMotionReader>(world);
        Entity entity = world.CreateEntity(new TestMotion { Position = new(1, 0, 1) });
        navigation.AddAgent(entity, FlatMesh(), Profile(), new NavigationPolicy(),
            new NavigationGoal(position: new(8, 0, 1)), desiredSpeed: 2f);
        Move(navigation, world);
        Vector3 stoppedAt = world.Read<TestMotion>(entity).Position;

        world.Disable<NavigationGoal>(entity);
        Move(navigation, world);
        NavigationProgress paused = world.Read<NavigationProgress>(entity);
        Vector3 pausedAt = world.Read<TestMotion>(entity).Position;
        world.Enable<NavigationGoal>(entity);
        Move(navigation, world);

        Assert.Equal(stoppedAt, pausedAt);
        Assert.Equal(NavigationStatus.Idle, paused.Status);
        Assert.True(world.Read<TestMotion>(entity).Position.X > pausedAt.X);
        Assert.True(world.Read<NavigationProgress>(entity).RequestVersion > paused.RequestVersion);
    }

    [Fact]
    public void ArrivalWaitsForTheMovementConsumersActualHeading()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        using World world = WorldJobs.CreateWorld(scheduler: scheduler);
        var navigation = new NavigationSystems<NavigationMesh, PolygonNavigationSearch,
            PolygonSearchState, TestMotion, TestMotionReader>(world);
        Entity entity = world.CreateEntity(new TestMotion { Position = new(1, 0, 1) });
        navigation.AddAgent(entity, FlatMesh(), Profile(), new NavigationPolicy(),
            new NavigationGoal(position: new(1, 0, 1), heading: MathF.PI / 2), desiredSpeed: 2f);

        NavigationStep step = navigation.Schedule(0.1f, 8);
        JobHandle partlyTurned = new ConsumeHalfHeading().ScheduleParallel(world, step.Steering);
        navigation.ScheduleProgress(partlyTurned).Complete();
        NavigationProgress turning = world.Read<NavigationProgress>(entity);
        Move(navigation, world);

        Assert.Equal(NavigationStatus.Aligning, turning.Status);
        Assert.Equal(MathF.PI / 4, turning.HeadingError, 4);
        Assert.Equal(NavigationStatus.Arrived, world.Read<NavigationProgress>(entity).Status);
    }

    private static void Move(NavigationSystems<NavigationMesh, PolygonNavigationSearch,
        PolygonSearchState, TestMotion, TestMotionReader> navigation, World world)
    {
        NavigationStep step = navigation.Schedule(0.1f, 8);
        navigation.ScheduleProgress(new ConsumeSteering(0.1f).ScheduleParallel(world, step.Steering)).Complete();
    }

    private static NavigationProfile Profile() => new(
        [new(-0.2f, -0.2f), new(0.2f, -0.2f), new(0.2f, 0.2f), new(-0.2f, 0.2f)], 1.8f);

    // The rectangle dimensions are a test example of a completed single-polygon mesh.
    private static NavigationMesh FlatMesh() => new(
        [new(0, 0, 0), new(10, 0, 0), new(10, 0, 2), new(0, 0, 2)],
        [0, 1, 2, 3], [new(0, 4, 0, 0)], []);

    // These dimensions are a movement-test example; mesh topology is supplied through the public data model.
    private static NavigationMesh ObstacleMesh() => new(
        [new(0, 0, 0), new(2, 0, 0), new(2, 0, 4), new(0, 0, 4),
         new(2, 0, 2), new(4, 0, 2), new(4, 0, 4), new(2, 0, 4),
         new(4, 0, 0), new(6, 0, 0), new(6, 0, 4), new(4, 0, 4)],
        [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11],
        [new(0, 4, 0, 1), new(4, 4, 1, 2), new(8, 4, 3, 1)],
        [new(1, new(2, 0, 4), new(2, 0, 2)),
         new(0, new(2, 0, 2), new(2, 0, 4)), new(2, new(4, 0, 4), new(4, 0, 2)),
         new(1, new(4, 0, 2), new(4, 0, 4))]);
}

public struct TestMotion : IComponent
{
    public Vector3 Position;
    public Vector3 Velocity;
    public float Heading;
}

public readonly struct TestMotionReader : INavigationMotionReader<TestMotion>
{
    public static NavigationMotion Read(in TestMotion motion) =>
        new(motion.Position, motion.Velocity, motion.Heading, true);
}

public partial struct ConsumeSteering(float deltaTime) : IJobEntity
{
    private readonly float _deltaTime = deltaTime;

    public void Execute(in NavigationSteering steering, ref TestMotion motion)
    {
        motion.Velocity = steering.Velocity;
        motion.Position += motion.Velocity * _deltaTime;
        if (steering.Active)
            motion.Heading = steering.Heading;
    }
}

public partial struct ConsumeHalfHeading : IJobEntity
{
    public void Execute(in NavigationSteering steering, ref TestMotion motion) =>
        motion.Heading += NavigationHeading.Difference(steering.Heading, motion.Heading) * 0.5f;
}
