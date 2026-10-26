using System.Numerics;
using DefaultGame;
using SomeEngine.ECS;
using SomeEngine.ECS.Systems;
using SomeEngine.Graphics;
using SomeEngine.Graphics.Direct3D12;
using SomeEngine.Job;
using SomeEngine.Physics;

namespace SomeEngine.Runtime.Tests;

public sealed class DevicePhysicsScaleSceneTests
{
    [Fact]
    public void TheDeviceEntryCreatesTheFullBrickStructureAndItsTimedImpact()
    {
        using var backend = D3D12GraphicsBackend.Create();
        backend.TryEnumerateAdapters(new(AdapterPreference.HighPerformance, IncludeSoftware: false), [], out int count);
        var adapters = new AdapterInfo[count];
        backend.TryEnumerateAdapters(new(AdapterPreference.HighPerformance, IncludeSoftware: false), adapters, out _);
        using Device device = backend.CreateDevice(new(adapters.First(adapter => adapter.HardwareAccelerated).Id,
            [new(QueueType.Compute)], label: "DefaultGame large device physics"));
        using var scheduler = new Scheduler(new() { WorkerCount = 2 });
        using World world = WorldJobs.CreateWorld(scheduler: scheduler);
        PhysicsSceneDescription description = PhysicsScaleScenes.All.Single(scene => scene.Id == "bricks-110000");
        using var scene = PhysicsScenes.CreateDevice(world, description, backend, device,
            new PhysicsSceneSettings(Iterations: 8, BodyIterations: 1, StepBudgetMilliseconds: 1000d / 60),
            new DeviceGridSettings(1, 8), new DeviceGridCapacity(110002, 880016, 220007, 16, 1),
            contactCapacity: 16, contactDistance: .02f, readbackBodyIndices: [110001],
            shaderDirectory: Path.Combine(AppContext.BaseDirectory, "PhysicsShaders"));

        Assert.Equal(110002, scene.Bodies.Length);
        var impactor = scene.Bodies.Span[^1];
        Assert.Equal(new Vector3(-60, 0, 1), world.Read<BodyPose>(impactor).Position);
        Assert.Equal(.002f, world.Read<BodyMass>(impactor).InverseMass);
        world.ExecuteBufferRead<BodyImpulse>(impactor, impulses =>
        {
            Assert.Equal(1, impulses.Count);
            Assert.Equal(new Vector3(12500, 0, 0), impulses[0].Linear);
            Assert.Equal(2, impulses[0].Time);
        });
    }
}
