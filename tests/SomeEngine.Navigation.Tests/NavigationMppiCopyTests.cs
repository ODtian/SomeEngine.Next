using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Navigation.Tests;

public sealed class NavigationMppiCopyTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void CopiedConfigurationKeepsEachActorsSelectedTrajectory(int workerCount)
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig
        {
            WorkerCount = workerCount,
            ManagedPayloadPolicy = ManagedPayloadPolicy.Allow,
        });
        using World world = WorldJobs.CreateWorld(scheduler: scheduler);
        var space = new CopySpace();
        Entity first = AddActor(world, space, Vector3.Zero, 11);
        Entity second = AddActor(world, space, new(20, 0, 0), 22);
        world.Replace(second, world.Read<NavigationMppi<CopySpace, CopyModel>>(first));
        var neighbors = new NavigationNeighborSystem<TestMotion, TestMotionReader>(world, scheduler);
        var mppi = new NavigationAvoidanceSystem<CopySpace, CopyModel, TestMotion, TestMotionReader>(world, neighbors);

        mppi.Schedule(0, default, neighbors.Schedule()).Complete();

        Assert.Equal(NavigationMppiStatus.Ready, world.Read<NavigationMppiResult>(first).Status);
        Assert.Equal(NavigationMppiStatus.Ready, world.Read<NavigationMppiResult>(second).Status);
        Assert.Equal(11UL, world.Read<NavigationMppiResult>(first).RequestVersion);
        Assert.Equal(22UL, world.Read<NavigationMppiResult>(second).RequestVersion);
        Assert.Equal(Vector3.Zero, ReadTrajectory(world, first)[0].Position);
        Assert.Equal(new Vector3(20, 0, 0), ReadTrajectory(world, second)[0].Position);
    }

    [Fact]
    public void CopyingUsedConfigurationKeepsTheLaterActorIndependent()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig
        {
            WorkerCount = 0,
            ManagedPayloadPolicy = ManagedPayloadPolicy.Allow,
        });
        using World world = WorldJobs.CreateWorld(scheduler: scheduler);
        var space = new CopySpace();
        Entity first = AddActor(world, space, Vector3.Zero, 31);
        var neighbors = new NavigationNeighborSystem<TestMotion, TestMotionReader>(world, scheduler);
        var mppi = new NavigationAvoidanceSystem<CopySpace, CopyModel, TestMotion, TestMotionReader>(world, neighbors);
        mppi.Schedule(0, default, neighbors.Schedule()).Complete();
        Entity second = AddActor(world, space, new(20, 0, 0), 32);
        world.Replace(second, world.Read<NavigationMppi<CopySpace, CopyModel>>(first));

        mppi.Schedule(0.1f, default, neighbors.Schedule()).Complete();

        Assert.Equal(31UL, world.Read<NavigationMppiResult>(first).RequestVersion);
        Assert.Equal(32UL, world.Read<NavigationMppiResult>(second).RequestVersion);
        Assert.Equal(Vector3.Zero, ReadTrajectory(world, first)[0].Position);
        Assert.Equal(new Vector3(20, 0, 0), ReadTrajectory(world, second)[0].Position);
    }

    [Fact]
    public void CopiedInactiveConfigurationKeepsEachActorsCurrentMotion()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig
        {
            WorkerCount = 0,
            ManagedPayloadPolicy = ManagedPayloadPolicy.Allow,
        });
        using World world = WorldJobs.CreateWorld(scheduler: scheduler);
        var space = new CopySpace();
        Entity first = AddActor(world, space, Vector3.Zero, 41);
        Entity second = AddActor(world, space, new(20, 0, 0), 42);
        world.Replace(first, new NavigationGuidance(Vector3.Zero, 0, 41, false));
        world.Replace(second, new NavigationGuidance(Vector3.Zero, 0, 42, false));
        world.Replace(second, world.Read<NavigationMppi<CopySpace, CopyModel>>(first));
        var neighbors = new NavigationNeighborSystem<TestMotion, TestMotionReader>(world, scheduler);
        var mppi = new NavigationAvoidanceSystem<CopySpace, CopyModel, TestMotion, TestMotionReader>(world, neighbors);

        mppi.Schedule(0, default, neighbors.Schedule()).Complete();

        Assert.Equal(new NavigationMppiResult(NavigationMppiStatus.Idle, 0, 41), world.Read<NavigationMppiResult>(first));
        Assert.Equal(new NavigationMppiResult(NavigationMppiStatus.Idle, 0, 42), world.Read<NavigationMppiResult>(second));
        Assert.Equal(Vector3.Zero, Assert.Single(ReadTrajectory(world, first)).Position);
        Assert.Equal(new Vector3(20, 0, 0), Assert.Single(ReadTrajectory(world, second)).Position);
    }

    private static Entity AddActor(World world, CopySpace space, Vector3 position, ulong requestVersion)
    {
        var profile = new NavigationProfile(
            [new Vector2(-0.1f), new(0.1f, -0.1f), new(0.1f), new(-0.1f, 0.1f)], 1);
        Entity actor = world.CreateEntity(new TestMotion { Position = position });
        world.Add(actor, new NavigationAgent(profile, new NavigationPolicy(), 1));
        world.Add(actor, new NavigationGoal(position + Vector3.UnitZ * 5));
        world.Add(actor, default(NavigationPathResult));
        world.Add(actor, new NavigationGuidance(Vector3.UnitZ, 0, requestVersion, true));
        world.Add(actor, default(NavigationSteering));
        NavigationComponents.AddMppi<CopySpace, CopyModel>(world, actor, space,
            new() { CandidateCount = 2, HorizonSteps = 1, Iterations = 2, NoiseStandardDeviation = Vector2.Zero },
            new(0.1f));
        return actor;
    }

    private static NavigationTrajectoryPoint[] ReadTrajectory(World world, Entity entity)
    {
        NavigationTrajectoryPoint[] result = [];
        world.ExecuteBufferRead<NavigationTrajectoryPoint>(entity, buffer => result = buffer.AsSpan().ToArray());
        return result;
    }

    private sealed class CopySpace : INavigationSpace<CopySpace>
    {
        public static void CollectResources(CopySpace space, ref JobResourceAccessBuilder resources) { }
    }

    private readonly struct CopyModel : INavigationMppiModel<CopySpace>
    {
        public static Vector2 ReferenceControl(in NavigationMotion actual, NavigationPose reference,
            float desiredSpeed, NavigationProfile profile) => new(desiredSpeed, 0);
        public static Vector2 Constrain(Vector2 control, in NavigationMotion actual,
            NavigationProfile profile, float timeStep) => control;
        public static NavigationMotion Integrate(in NavigationMotion actual, Vector2 control,
            NavigationProfile profile, float time) => actual with
            {
                Position = actual.Position + Vector3.UnitZ * control.X * time,
                Velocity = Vector3.UnitZ * control.X,
            };
        public static bool TryStep(CopySpace space, in NavigationMotion actual, Vector2 control,
            NavigationProfile profile, NavigationPolicy policy, float time, out NavigationMotion next, out float cost)
        {
            next = Integrate(in actual, control, profile, time);
            cost = 0;
            return true;
        }
        public static NavigationSteering Steering(Vector2 control, in NavigationMotion actual,
            in NavigationMotion next, NavigationProfile profile, in NavigationGuidance guidance, float timeStep) =>
            new(next.Velocity, next.Heading, guidance.RequestVersion, guidance.Active);
    }
}
