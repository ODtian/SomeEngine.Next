using DefaultGame;
using SomeEngine.Core.ECS.Components;
using SomeEngine.ECS;
using SomeEngine.ECS.Systems;
using SomeEngine.Graphics;
using SomeEngine.Graphics.Direct3D12;
using SomeEngine.Job;
using SomeEngine.Physics;

namespace SomeEngine.Runtime.Tests;

public sealed class DevicePhysicsSceneTests
{
    [Fact]
    public void TheDeviceSceneHoldsItsOriginalLockedTrainThroughTheSharedConstraintGraph()
    {
        using var backend = D3D12GraphicsBackend.Create();
        using Device device = CreateDevice(backend);
        using var scheduler = new Scheduler(new() { WorkerCount = 2 });
        using World world = WorldJobs.CreateWorld(scheduler: scheduler);
        PhysicsSceneDescription description = PhysicsScenes.All.Single(scene => scene.Id == "angular-locked-train");
        using var scene = PhysicsScenes.CreateDevice(world, description, backend, device,
            new PhysicsSceneSettings(Iterations: 128, BodyIterations: 3, StepBudgetMilliseconds: 1000d / 60),
            new DeviceGridSettings(2, 3), new DeviceGridCapacity(8, 512, 1031, 128, 1), contactCapacity: 128, contactDistance: .02f,
            readbackBodyIndices: [0, 1, 2, 3, 4, 5, 6, 7], shaderDirectory: Path.Combine(AppContext.BaseDirectory, "PhysicsShaders"));
        PhysicsStepResult result = default;

        for (int number = 0; number < 12; number++)
        {
            scene.Frame.AdvanceTime(scene.StepSeconds * (number == 0 ? 1.5 : 1));
            result = scene.CompleteForRead();
        }

        var last = scene.Bodies.Span[^1];
        BodyPose pose = world.Read<BodyPose>(last);
        Assert.InRange(pose.Position.Z, 9.98f, 10.02f);
        Assert.InRange(world.Read<BodyVelocity>(last).Linear.Length(), 0, .05f);
        Assert.Equal(pose.Position, world.Read<LocalTransform>(last).Value.Position);
        Assert.Equal(pose.Rotation, world.Read<LocalTransform>(last).Value.Rotation);
        Assert.Equal(11, result.Number);
        Assert.Equal(.2, result.EndTime, 12);
    }

    [Fact]
    public void TheDeviceSceneLandsItsSourceBoxAndDeliversTheSelectedTransformThroughItsFrame()
    {
        using var backend = D3D12GraphicsBackend.Create();
        using Device device = CreateDevice(backend);
        using var scheduler = new Scheduler(new() { WorkerCount = 2 });
        using World world = WorldJobs.CreateWorld(scheduler: scheduler);
        PhysicsSceneDescription description = PhysicsScenes.All.Single(scene => scene.Id == "face-drop");
        using var scene = PhysicsScenes.CreateDevice(world, description, backend, device,
            new PhysicsSceneSettings(Iterations: 16, BodyIterations: 2, StepBudgetMilliseconds: 1000d / 60),
            new DeviceGridSettings(2, 3), new DeviceGridCapacity(2, 256, 257, 16, 1), contactCapacity: 16, contactDistance: .02f,
            readbackBodyIndices: [1], shaderDirectory: Path.Combine(AppContext.BaseDirectory, "PhysicsShaders"));
        var box = scene.Bodies.Span[1];
        PhysicsStepResult result = default;

        for (int number = 0; number < 36; number++)
        {
            scene.Frame.AdvanceTime(scene.StepSeconds);
            result = scene.CompleteForRead();
        }

        BodyPose pose = world.Read<BodyPose>(box);
        Assert.InRange(pose.Position.Z, .399f, .401f);
        Assert.InRange(world.Read<BodyVelocity>(box).Linear.Z, -.001f, .001f);
        Assert.Equal(pose.Position, world.Read<LocalTransform>(box).Value.Position);
        Assert.Equal(pose.Rotation, world.Read<LocalTransform>(box).Value.Rotation);
        Assert.Equal(35, result.Number);
        Assert.Equal(.6, result.EndTime, 12);
        Assert.Equal(35, world.Read<BodyStepState>(box).Number);
    }

    private static Device CreateDevice(IGraphicsBackend backend)
    {
        backend.TryEnumerateAdapters(new(AdapterPreference.HighPerformance, IncludeSoftware: false), [], out int count);
        var adapters = new AdapterInfo[count];
        backend.TryEnumerateAdapters(new(AdapterPreference.HighPerformance, IncludeSoftware: false), adapters, out _);
        return backend.CreateDevice(new(adapters.First(adapter => adapter.HardwareAccelerated).Id,
            [new(QueueType.Compute)], label: "DefaultGame device physics"));
    }
}
