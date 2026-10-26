using System.Numerics;
using DefaultGame;
using SomeEngine.Core.ECS.Components;
using SomeEngine.ECS;
using SomeEngine.ECS.Systems;
using SomeEngine.Graphics;
using SomeEngine.Graphics.Direct3D12;
using SomeEngine.Job;
using SomeEngine.Physics;

namespace SomeEngine.Runtime.Tests;

public sealed class PhysicsDeviceStackSceneTests
{
    [Fact]
    public void TheOriginalSpatialStackSettlesAndSleepsThroughTheDeviceSceneFrame()
    {
        using var backend = D3D12GraphicsBackend.Create();
        backend.TryEnumerateAdapters(new(AdapterPreference.HighPerformance, IncludeSoftware: false), [], out int count);
        var adapters = new AdapterInfo[count];
        backend.TryEnumerateAdapters(new(AdapterPreference.HighPerformance, IncludeSoftware: false), adapters, out _);
        using Device device = backend.CreateDevice(new(adapters.First(adapter => adapter.HardwareAccelerated).Id,
            [new(QueueType.Compute)]));
        using var scheduler = new Scheduler(new() { WorkerCount = 2 });
        using World world = WorldJobs.CreateWorld(scheduler: scheduler);
        PhysicsSceneDescription description = PhysicsScenes.All.Single(scene => scene.Id == "3d-stack");
        PhysicsSceneExample example = PhysicsSceneExample.CreateDevice(description, backend, device);
        using PhysicsSceneSimulation scene = example.CreateSimulation(world);
        PhysicsStepResult completed = default;

        for (int number = 0; number < 180; number++)
        {
            scene.AdvanceTime(scene.StepSeconds * (number == 0 ? 1.5 : 1));
            completed = scene.CompleteForRead();
        }

        for (int index = 1; index < scene.Bodies.Length; index++)
        {
            var body = scene.Bodies.Span[index];
            Vector3 position = world.Read<BodyPose>(body).Position;
            Assert.InRange(Vector3.Distance(position, new(0, 0, index)), 0, .01f);
            Assert.InRange(world.Read<BodyVelocity>(body).Linear.Length(), 0, .01f);
            Assert.True(world.Read<BodyActivity>(body).Sleeping);
            Assert.Equal(position, world.Read<LocalTransform>(body).Value.Position);
        }
        Assert.Equal(179, completed.Number);
        Assert.Equal(180 * scene.StepSeconds, completed.EndTime, 12);
        Assert.Equal(0, scene.DeviceAwakeBodies);
        Assert.Equal(scene.Bodies.Length - 1, scene.DeviceLargestIslandBodies);
    }
}
