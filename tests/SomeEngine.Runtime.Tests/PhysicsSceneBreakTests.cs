using DefaultGame;
using SomeEngine.Core.ECS.Components;
using SomeEngine.ECS;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;
using SomeEngine.Physics;

namespace SomeEngine.Runtime.Tests;

public sealed class PhysicsSceneBreakTests
{
    [Fact]
    public void TheCompletedSceneReleasesItsOverloadedJointAndDeliversTheBreakForce()
    {
        using var scheduler = new Scheduler(new() { WorkerCount = 2 });
        using World world = WorldJobs.CreateWorld(scheduler: scheduler);
        var received = new List<(long Number, PhysicsEvent Value)>();
        PhysicsSceneDescription description = PhysicsScenes.All.Single(scene => scene.Id == "angular-locked-train");
        using var scene = PhysicsScenes.CreateCpu(world, description,
            new ConvexCollision<BoxGeometry, BoxGeometry>(new(1e-5f, 32, 64)),
            new PhysicsSceneSettings(Iterations: 32, BodyIterations: 3, StepBudgetMilliseconds: 1000d / 60),
            new RecordBreaks(received), [JobResourceAccess.Write(scheduler.GetResourceToken(received))]);
        var joint = scene.Joints.Span[0];
        var body = scene.Bodies.Span[1];
        world.Add(joint, new ConstraintBreakSettings(1, float.PositiveInfinity));

        scene.Frame.AdvanceTime(scene.StepSeconds);
        PhysicsStepResult result = scene.CompleteForRead();

        var observed = Assert.Single(received);
        Assert.Equal(0, result.Number);
        Assert.Equal(0, observed.Number);
        Assert.Equal(PhysicsEventKind.Break, observed.Value.Kind);
        Assert.Equal(joint, observed.Value.Subject);
        Assert.Equal(scene.StepSeconds, observed.Value.Time);
        Assert.True(observed.Value.Force.Length() > 1);
        Assert.False(world.IsAlive(joint));
        float supportedHeight = world.Read<BodyPose>(body).Position.Z;

        scene.Frame.AdvanceTime(scene.StepSeconds);
        scene.CompleteForRead();

        Assert.True(world.Read<BodyPose>(body).Position.Z < supportedHeight);
        Assert.Equal(world.Read<BodyPose>(body).Position, world.Read<LocalTransform>(body).Value.Position);
    }

    private readonly struct RecordBreaks(List<(long Number, PhysicsEvent Value)> received) : IPhysicsEventConsumer
    {
        public void Consume(scoped in PhysicsStepEvents step, scoped ReadOnlySpan<PhysicsEvent> events, ref JobCommandWriter commands)
        {
            foreach (PhysicsEvent value in events)
                if (value.Kind == PhysicsEventKind.Break) received.Add((step.Number, value));
        }
    }
}
