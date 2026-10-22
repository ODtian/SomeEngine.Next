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
            new NavigationGoal(new(5, 0, 1), 0.1f), desiredSpeed: 2f);

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
        motion.Heading = steering.Heading;
    }
}
