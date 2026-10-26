using System.Numerics;
using DefaultGame;
using SomeEngine.Core.ECS;
using SomeEngine.Core.ECS.Components;
using SomeEngine.ECS;
using SomeEngine.ECS.Systems;
using SomeEngine.Graphics;
using SomeEngine.Graphics.Direct3D12;
using SomeEngine.Job;
using SomeEngine.Physics;

namespace SomeEngine.Runtime.Tests;

public sealed class PhysicsDeviceExampleTests
{
    [Fact]
    public void TheDeviceSceneExampleDeliversItsBodyThroughTheGameFrameTransformStage()
    {
        using var backend = D3D12GraphicsBackend.Create();
        backend.TryEnumerateAdapters(new(AdapterPreference.HighPerformance, IncludeSoftware: false), [], out int count);
        var adapters = new AdapterInfo[count];
        backend.TryEnumerateAdapters(new(AdapterPreference.HighPerformance, IncludeSoftware: false), adapters, out _);
        using Device device = backend.CreateDevice(new(adapters.First(adapter => adapter.HardwareAccelerated).Id,
            [new(QueueType.Compute)], label: "Physics scene platform"));
        using var scheduler = new Scheduler(new() { WorkerCount = 2 });
        using World world = WorldJobs.CreateWorld(scheduler: scheduler);
        using var frames = new GameFrameSystems(world, includeInterface: false);
        PhysicsSceneDescription description = PhysicsScenes.All.Single(scene => scene.Id == "face-drop");
        PhysicsSceneExample example = PhysicsSceneExample.CreateDevice(description, backend, device);
        PhysicsSceneSimulation scene = example.CreateSimulation(world);
        var body = scene.Bodies.Span[1];
        world.Add(body, new WorldTransform());
        frames.Physics = scene;

        frames.UpdateFrame(scene.StepSeconds);

        Assert.InRange(world.Read<BodyPose>(body).Position.Z, 1.3972221f, 1.3972223f);
        Assert.Equal(world.Read<BodyPose>(body).Position, world.Read<WorldTransform>(body).Qvvs.Position);
        Assert.Equal(new Vector3(3, 2, .8f), world.Read<WorldTransform>(body).Qvvs.Stretch);
        Assert.Equal("GPU", example.Describe()["Device"]!.GetValue<string>());
        Assert.Equal(180, example.StepCount);
        Assert.Equal(0, world.Read<BodyStepState>(body).Number);
    }
}
