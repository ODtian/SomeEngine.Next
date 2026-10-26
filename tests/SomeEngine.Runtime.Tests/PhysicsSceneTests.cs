using System.Numerics;
using DefaultGame;
using SomeEngine.Core.ECS.Components;
using SomeEngine.ECS;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;
using SomeEngine.Physics;

namespace SomeEngine.Runtime.Tests;

public sealed class PhysicsSceneTests
{
    [Fact]
    public void TheRestingSceneSleepsAndItsTimedHopReachesTheCompletedEventConsumer()
    {
        using var scheduler = new Scheduler(new() { WorkerCount = 2 });
        using World world = WorldJobs.CreateWorld(scheduler: scheduler);
        var observed = new List<(long Number, PhysicsEvent Value)>();
        PhysicsSceneDescription description = PhysicsScenes.All.Single(scene => scene.Id == "recontact-hops");
        using var scene = PhysicsScenes.CreateCpu(world, description, new ConvexCollision<BoxGeometry, BoxGeometry>(new(1e-5f, 32, 64)),
            new PhysicsSceneSettings(Iterations: 16, BodyIterations: 3, StepBudgetMilliseconds: 1000d / 60)
            {
                Activity = new BodyActivitySettings(.1f, .05f, .001f, .001f),
            }, new RecordCompletedSceneEvents(observed), [JobResourceAccess.Write(scheduler.GetResourceToken(observed))]);
        var body = scene.Bodies.Span[1];

        scene.Frame.AdvanceTime(61.5 * scene.StepSeconds);
        PhysicsStepResult result = scene.CompleteForRead();

        Assert.Equal(60, result.Number);
        int sleep = observed.FindIndex(item => item.Value.Kind == PhysicsEventKind.Sleep && item.Value.Subject == body);
        int wake = observed.FindIndex(item => item.Value.Kind == PhysicsEventKind.Wake && item.Value.Subject == body);
        Assert.True(sleep >= 0);
        Assert.True(wake > sleep);
        Assert.Equal(60, observed[wake].Number);
        Assert.Equal(1, observed[wake].Value.Time);
        Assert.Contains(observed, item => item.Number == 60 && item.Value.Kind == PhysicsEventKind.Contact && item.Value.Phase == PhysicsEventPhase.End);
        Assert.False(world.Read<BodyActivity>(body).Sleeping);
        Assert.True(world.Read<LocalTransform>(body).Value.Position.Z > .52f);
        QueryHandle events = world.Query(new QueryDefinitionBuilder().All<ScenePhysicsEventStream>());
        Assert.False(world.HasQueryMembers(events));
        world.ReleaseQuery(events);
    }

    private readonly struct RecordCompletedSceneEvents(List<(long Number, PhysicsEvent Value)> output) : IPhysicsEventConsumer
    {
        public void Consume(scoped in PhysicsStepEvents step, scoped ReadOnlySpan<PhysicsEvent> events, ref JobCommandWriter commands)
        {
            foreach (PhysicsEvent value in events) output.Add((step.Number, value));
        }
    }

    [Fact]
    public void TheCounterweightKeepsTheOriginalRelativeRotationOfItsLockedConnection()
    {
        using var scheduler = new Scheduler(new() { WorkerCount = 2 });
        using World world = WorldJobs.CreateWorld(scheduler: scheduler);
        PhysicsSceneDescription description = PhysicsScenes.All.Single(scene => scene.Id == "counterweighted-joint");
        using var scene = PhysicsScenes.CreateCpu(world, description, new ConvexCollision<BoxGeometry, BoxGeometry>(new(1e-5f, 32, 64)),
            new PhysicsSceneSettings(Iterations: 32, BodyIterations: 3, StepBudgetMilliseconds: 1000d / 60));
        var ends = world.Read<DirectedRelationEndpoints<JointConstraint>>(scene.Joints.Span[1]);
        Quaternion initial = Quaternion.Conjugate(world.Read<BodyPose>(ends.Source).Rotation) * world.Read<BodyPose>(ends.Target).Rotation;

        scene.Frame.AdvanceTime(scene.StepSeconds);
        scene.CompleteForRead();

        Quaternion relative = Quaternion.Conjugate(world.Read<BodyPose>(ends.Source).Rotation) * world.Read<BodyPose>(ends.Target).Rotation;
        Assert.InRange(MathF.Abs(Quaternion.Dot(initial, relative)), .99999f, 1.000001f);
    }

    [Fact]
    public void TheOriginalMovingFloorWritesItsTimedPoseBeforePredictionAndFrameConsumption()
    {
        using var scheduler = new Scheduler(new() { WorkerCount = 2 });
        using World world = WorldJobs.CreateWorld(scheduler: scheduler);
        PhysicsSceneDescription description = PhysicsScenes.All.Single(scene => scene.Id == "moving-floor");
        using var scene = PhysicsScenes.CreateCpu(world, description, new ConvexCollision<BoxGeometry, BoxGeometry>(new(1e-5f, 32, 64)),
            new PhysicsSceneSettings(Iterations: 0, BodyIterations: 1, StepBudgetMilliseconds: 1000d / 60));
        var floor = scene.Bodies.Span[0];

        scene.Frame.AdvanceTime(92.5 * scene.StepSeconds);
        PhysicsStepResult result = scene.CompleteForRead();

        Assert.Equal(91, result.Number);
        Assert.Equal(new Vector3(.005f, 0, -.5f), world.Read<BodyPose>(floor).Position);
        Assert.Equal(new Vector3(.005f, 0, -.5f), world.Read<BodyStepState>(floor).StartCenterOfMass);
        Assert.Equal(new Vector3(.005f, 0, -.5f), world.Read<LocalTransform>(floor).Value.Position);
    }

    [Fact]
    public void TheOriginalSpatialChainAppliesItsMassScaledImpulseOnStepNinety()
    {
        using var scheduler = new Scheduler(new() { WorkerCount = 2 });
        using World world = WorldJobs.CreateWorld(scheduler: scheduler);
        PhysicsSceneDescription description = PhysicsScenes.All.Single(scene => scene.Id == "3d-spatial-chain");
        using var scene = PhysicsScenes.CreateCpu(world, description, new ConvexCollision<BoxGeometry, BoxGeometry>(new(1e-5f, 32, 64)),
            new PhysicsSceneSettings(Iterations: 0, BodyIterations: 1, StepBudgetMilliseconds: 1000d / 60));
        var last = scene.Bodies.Span[^1];
        Assert.Equal(.008f, world.Read<BodyMass>(last).InverseMass);
        for (int number = 0; number < 90; number++)
        {
            scene.Frame.AdvanceTime(scene.StepSeconds);
            scene.CompleteForRead();
        }
        Assert.Equal(0, world.Read<BodyVelocity>(last).Linear.Y);

        scene.Frame.AdvanceTime(scene.StepSeconds);
        PhysicsStepResult result = scene.CompleteForRead();

        Assert.Equal(90, result.Number);
        Assert.InRange(world.Read<BodyVelocity>(last).Linear.Y, 1.99999f, 2.00001f);
        Assert.InRange(world.Read<LocalTransform>(last).Value.Position.Y, .03333332f, .03333335f);
    }

    [Fact]
    public void TheManualChainCreatesItsEightRadialEdgesAndProducesTheirReachReaction()
    {
        using var scheduler = new Scheduler(new() { WorkerCount = 2 });
        using World world = WorldJobs.CreateWorld(scheduler: scheduler);
        PhysicsSceneDescription description = PhysicsScenes.All.Single(scene => scene.Id == "2d-manual-chain");
        using var scene = PhysicsScenes.CreateCpu(world, description, new PlanarBoxCollision(1e-5f),
            new PhysicsSceneSettings(Iterations: 32, BodyIterations: 3, StepBudgetMilliseconds: 1000d / 60));
        Assert.Equal(8, scene.DistanceRanges.Length);
        var rangeEntity = scene.DistanceRanges.Span[3];
        var ends = world.Read<UndirectedRelationEndpoints<DistanceRange>>(rangeEntity);
        DistanceRange range = world.Read<DistanceRange>(rangeEntity);
        Assert.Equal(scene.Bodies.Span[0], ends.EndpointA);
        Assert.Equal(scene.Bodies.Span[19], ends.EndpointB);
        Assert.Equal(18, range.Maximum);
        foreach (var body in scene.Bodies.Span)
        {
            BodyPose pose = world.Read<BodyPose>(body);
            pose.Position.X *= 1.01f;
            world.Replace(body, pose);
        }

        scene.Frame.AdvanceTime(scene.StepSeconds);
        scene.CompleteForRead();

        BodyPose first = world.Read<BodyPose>(ends.EndpointA), second = world.Read<BodyPose>(ends.EndpointB);
        Vector3 start = first.Position + Vector3.Transform(range.FirstAnchor, first.Rotation);
        Vector3 finish = second.Position + Vector3.Transform(range.SecondAnchor, second.Rotation);
        Assert.InRange(Vector3.Distance(start, finish), 0, 18.18f);
        Assert.True(world.Read<AvbdDistanceRangeState>(rangeEntity).Multiplier.X > 0);
        Assert.Equal(0, second.Position.Z);
    }

    [Fact]
    public void TheOriginalReleaseEventDestroysItsRootConnectionAtStepNinety()
    {
        using var scheduler = new Scheduler(new() { WorkerCount = 2 });
        using World world = WorldJobs.CreateWorld(scheduler: scheduler);
        PhysicsSceneDescription description = PhysicsScenes.All.Single(scene => scene.Id == "3d-release-chain");
        using var scene = PhysicsScenes.CreateCpu(world, description, new ConvexCollision<BoxGeometry, BoxGeometry>(new(1e-5f, 32, 64)),
            new PhysicsSceneSettings(Iterations: 0, BodyIterations: 1, StepBudgetMilliseconds: 1000d / 60));
        for (int number = 0; number < 90; number++)
        {
            scene.Frame.AdvanceTime(scene.StepSeconds);
            scene.CompleteForRead();
        }
        Assert.True(world.IsAlive(scene.Joints.Span[0]));

        scene.Frame.AdvanceTime(scene.StepSeconds);
        PhysicsStepResult result = scene.CompleteForRead();

        Assert.Equal(90, result.Number);
        Assert.False(world.IsAlive(scene.Joints.Span[0]));
        Assert.True(world.IsAlive(scene.Joints.Span[1]));
    }

    [Fact]
    public void TheOriginalChainHoldsItsFirstAnchorThroughThePublicFrame()
    {
        using var scheduler = new Scheduler(new() { WorkerCount = 2 });
        using World world = WorldJobs.CreateWorld(scheduler: scheduler);
        PhysicsSceneDescription description = PhysicsScenes.All.Single(scene => scene.Id == "3d-release-chain");
        using var scene = PhysicsScenes.CreateCpu(world, description, new ConvexCollision<BoxGeometry, BoxGeometry>(new(1e-5f, 32, 64)),
            new PhysicsSceneSettings(Iterations: 32, BodyIterations: 3, StepBudgetMilliseconds: 1000d / 60));
        Assert.Equal(20, scene.Bodies.Length);
        Assert.Equal(19, scene.Joints.Length);
        var joint = scene.Joints.Span[0];
        var ends = world.Read<DirectedRelationEndpoints<JointConstraint>>(joint);
        Assert.Equal(scene.Bodies.Span[0], ends.Source);
        Assert.Equal(scene.Bodies.Span[1], ends.Target);

        scene.Frame.AdvanceTime(scene.StepSeconds);
        scene.CompleteForRead();

        BodyPose source = world.Read<BodyPose>(ends.Source), target = world.Read<BodyPose>(ends.Target);
        JointConstraint connection = world.Read<JointConstraint>(joint);
        Vector3 first = source.Position + Vector3.Transform(connection.SourceFrame.Position, source.Rotation);
        Vector3 second = target.Position + Vector3.Transform(connection.TargetFrame.Position, target.Rotation);
        Assert.Equal(new Vector3(.5f, 0, 10), first);
        Assert.InRange(Vector3.Distance(first, second), 0, 3e-4f);
        Assert.True(target.Position.Z < 10);
    }

    [Fact]
    public void TheOriginalPlanarPyramidCreatesItsAreaMassAndAdvancesThroughTheFrameConsumer()
    {
        using var scheduler = new Scheduler(new() { WorkerCount = 2 });
        using World world = WorldJobs.CreateWorld(scheduler: scheduler);
        PhysicsSceneDescription description = PhysicsScenes.All.Single(scene => scene.Id == "2d-pyramid");
        using var scene = PhysicsScenes.CreateCpu(world, description, new PlanarBoxCollision(1e-5f),
            new PhysicsSceneSettings(Iterations: 8, BodyIterations: 1, StepBudgetMilliseconds: 1000d / 60));
        Assert.Equal(211, scene.Bodies.Length);
        var first = scene.Bodies.Span[1];
        BodyMass mass = world.Read<BodyMass>(first);
        Assert.Equal(2, mass.InverseMass);
        Assert.Equal(19.2f, mass.InverseInertia.M33);
        Assert.Equal(0, mass.InverseInertia.M11); Assert.Equal(0, mass.InverseInertia.M22);
        Assert.Equal(new Vector3(.5f, .25f, .125f), world.Read<BoxGeometry>(first).HalfExtents);

        scene.Frame.AdvanceTime(scene.StepSeconds);
        PhysicsStepResult step = scene.CompleteForRead();

        var highest = scene.Bodies.Span[^1];
        BodyPose pose = world.Read<BodyPose>(highest);
        Assert.InRange(pose.Position.Y, 16.14721f, 16.14724f);
        Assert.Equal(0, pose.Position.Z);
        Assert.Equal(pose.Position, world.Read<LocalTransform>(highest).Value.Position);
        Assert.Equal(0, step.Number);
        Assert.Equal((double)(1f / 60f), step.EndTime);
        Assert.Equal(step.EndTime, world.Read<BodyStepState>(highest).Time);
    }
}
