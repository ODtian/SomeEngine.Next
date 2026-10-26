using DefaultGame;
using SomeEngine.Core.ECS.Components;
using SomeEngine.ECS;
using SomeEngine.ECS.Systems;
using SomeEngine.Graphics;
using SomeEngine.Graphics.Direct3D12;
using SomeEngine.Job;
using SomeEngine.Physics;

namespace SomeEngine.Runtime.Tests;

public sealed class PhysicsDeviceJointBoundsSceneTests
{
    [Fact]
    public void TheDeviceSceneUsesItsDeclaredJointBoundsForTheHangingRope()
    {
        using var backend = D3D12GraphicsBackend.Create();
        backend.TryEnumerateAdapters(new(AdapterPreference.HighPerformance, IncludeSoftware: false), [], out int count);
        var adapters = new AdapterInfo[count];
        backend.TryEnumerateAdapters(new(AdapterPreference.HighPerformance, IncludeSoftware: false), adapters, out _);
        using Device device = backend.CreateDevice(new(adapters.First(adapter => adapter.HardwareAccelerated).Id,
            [new(QueueType.Compute)]));
        using var scheduler = new Scheduler(new() { WorkerCount = 2 });
        using World ordinary = WorldJobs.CreateWorld(scheduler: scheduler);
        using World bounded = WorldJobs.CreateWorld(scheduler: scheduler);
        PhysicsSceneDescription description = PhysicsScenes.All.Single(scene => scene.Id == "2d-chain");
        PhysicsSceneSettings settings = new(1, 1, 1000d / 60) { MultiplierRetention = 0 };
        using PhysicsSceneSimulation reference = PhysicsScenes.CreateDevice(ordinary, description, backend, device,
            settings, new(1, 16), new(50, 400, 801, 1600, 1), 1600, .02f, [0, 49],
            Path.Combine(AppContext.BaseDirectory, "PhysicsShaders"));
        using PhysicsSceneSimulation scene = PhysicsScenes.CreateDevice(bounded, description, backend, device,
            settings with { JointBounds = new(6, 32, .99f, .99f) }, new(1, 16), new(50, 400, 801, 1600, 1),
            1600, .02f, [0, 49], Path.Combine(AppContext.BaseDirectory, "PhysicsShaders"));
        var referenceEnd = reference.Bodies.Span[49];
        var boundedEnd = scene.Bodies.Span[49];
        var root = scene.Bodies.Span[0];
        BodyPose initial = bounded.Read<BodyPose>(boundedEnd);
        BodyPose rootPose = bounded.Read<BodyPose>(root);

        reference.AdvanceTime(reference.StepSeconds);
        reference.CompleteForRead();
        scene.AdvanceTime(scene.StepSeconds);
        PhysicsStepResult completed = scene.CompleteForRead();

        float ordinaryTravel = MathF.Abs(ordinary.Read<BodyPose>(referenceEnd).Position.Y - initial.Position.Y);
        float boundedTravel = MathF.Abs(bounded.Read<BodyPose>(boundedEnd).Position.Y - initial.Position.Y);
        Assert.InRange(boundedTravel, 0, ordinaryTravel * .5f);
        Assert.InRange(MathF.Abs(bounded.Read<BodyVelocity>(boundedEnd).Linear.Y), 0,
            MathF.Abs(ordinary.Read<BodyVelocity>(referenceEnd).Linear.Y) * .5f);
        Assert.Equal(rootPose.Position, bounded.Read<BodyPose>(root).Position);
        Assert.Equal(bounded.Read<BodyPose>(boundedEnd).Position, bounded.Read<LocalTransform>(boundedEnd).Value.Position);
        Assert.Equal(0, completed.Number);
        Assert.Equal(scene.StepSeconds, completed.EndTime, 12);
    }
}
