using DefaultGame;
using SomeEngine.Core.ECS.Components;
using SomeEngine.ECS;
using SomeEngine.ECS.Systems;
using SomeEngine.Graphics;
using SomeEngine.Graphics.Direct3D12;
using SomeEngine.Job;
using SomeEngine.Physics;

namespace SomeEngine.Runtime.Tests;

public sealed class PhysicsDeviceSceneBreakTests
{
    [Fact]
    public void TheDeviceSceneDeliversItsCompletedJointBreakAndReleasesTheNextStep()
    {
        using var backend = D3D12GraphicsBackend.Create();
        backend.TryEnumerateAdapters(new(AdapterPreference.HighPerformance, IncludeSoftware: false), [], out int count);
        var adapters = new AdapterInfo[count];
        backend.TryEnumerateAdapters(new(AdapterPreference.HighPerformance, IncludeSoftware: false), adapters, out _);
        using Device device = backend.CreateDevice(new(adapters.First(adapter => adapter.HardwareAccelerated).Id,
            [new(QueueType.Compute)]));
        using var scheduler = new Scheduler(new() { WorkerCount = 2 });
        using World world = WorldJobs.CreateWorld(scheduler: scheduler);
        var received = new List<(long Number, PhysicsEvent Value)>();
        PhysicsSceneDescription description = PhysicsScenes.All.Single(scene => scene.Id == "angular-locked-train");
        using PhysicsSceneSimulation scene = PhysicsScenes.CreateDevice(world, description, backend, device,
            new(32, 3, 1000d / 60), new(1, 16), new(8, 64, 129, 256, 1), 256, .02f,
            Enumerable.Range(0, 8).ToArray(), Path.Combine(AppContext.BaseDirectory, "PhysicsShaders"),
            new RecordBreaks(received), [JobResourceAccess.Write(scheduler.GetResourceToken(received))]);
        var joint = scene.Joints.Span[0];
        var body = scene.Bodies.Span[1];
        world.Add(joint, new ConstraintBreakSettings(1, float.PositiveInfinity));

        scene.AdvanceTime(scene.StepSeconds);
        PhysicsStepResult completed = scene.CompleteForRead();

        var observed = Assert.Single(received);
        Assert.Equal(0, completed.Number);
        Assert.Equal(completed.Number, observed.Number);
        Assert.Equal(PhysicsEventKind.Break, observed.Value.Kind);
        Assert.Equal(joint, observed.Value.Subject);
        Assert.Equal(completed.EndTime, observed.Value.Time);
        Assert.True(observed.Value.Force.Length() > 1);
        Assert.False(world.IsAlive(joint));
        float supportedHeight = world.Read<BodyPose>(body).Position.Z;

        scene.AdvanceTime(scene.StepSeconds);
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
