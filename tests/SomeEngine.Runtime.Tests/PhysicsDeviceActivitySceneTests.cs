using System.Text.Json.Nodes;
using DefaultGame;
using SomeEngine.ECS;
using SomeEngine.ECS.Systems;
using SomeEngine.Graphics;
using SomeEngine.Graphics.Direct3D12;
using SomeEngine.Job;
using SomeEngine.Physics;

namespace SomeEngine.Runtime.Tests;

public sealed class PhysicsDeviceActivitySceneTests
{
    [Fact]
    public void TheDeviceSceneSleepsItsSettledBodyAndDeliversTheCompletedSleepEvent()
    {
        using var backend = D3D12GraphicsBackend.Create();
        backend.TryEnumerateAdapters(new(AdapterPreference.HighPerformance, IncludeSoftware: false), [], out int count);
        var adapters = new AdapterInfo[count];
        backend.TryEnumerateAdapters(new(AdapterPreference.HighPerformance, IncludeSoftware: false), adapters, out _);
        using Device device = backend.CreateDevice(new(adapters.First(adapter => adapter.HardwareAccelerated).Id,
            [new(QueueType.Compute)]));
        using var scheduler = new Scheduler(new() { WorkerCount = 2 });
        using World world = WorldJobs.CreateWorld(scheduler: scheduler);
        PhysicsSceneDescription description = PhysicsScenes.All.Single(scene => scene.Id == "face-drop");
        PhysicsSceneExample example = PhysicsSceneExample.CreateDevice(description, backend, device);
        using PhysicsSceneSimulation scene = example.CreateSimulation(world);
        PhysicsStepResult result = default;

        for (int number = 0; number < 180; number++)
        {
            scene.Frame.AdvanceTime(scene.StepSeconds * (number == 0 ? 1.5 : 1));
            result = scene.CompleteForRead();
        }

        var body = scene.Bodies.Span[1];
        Assert.InRange(world.Read<BodyPose>(body).Position.Z, .399f, .401f);
        Assert.True(world.Read<BodyActivity>(body).Sleeping);
        Assert.InRange(world.Read<BodyVelocity>(body).Linear.Length(), 0, .001f);
        Assert.InRange(world.Read<BodyVelocity>(body).Angular.Length(), 0, .001f);
        Assert.Equal(179, result.Number);
        Assert.Equal(3, result.EndTime, 12);
        Assert.Equal(0, scene.DeviceAwakeBodies);
        Assert.Equal(4, scene.DeviceContactPoints);
        Assert.Equal(1, scene.DeviceLargestIslandBodies);
        JsonNode sleep = Assert.Single(example.Describe()["CompletedEvents"]!.AsArray(),
            value => value!["Kind"]!.GetValue<string>() == "Sleep")!;
        Assert.Equal(1, sleep["Count"]!.GetValue<int>());
        Assert.Equal(sleep["FirstStep"]!.GetValue<long>(), sleep["LastStep"]!.GetValue<long>());
        Assert.InRange(sleep["LastTime"]!.GetValue<double>(), .5, 1.5);
    }

    [Fact]
    public void DeviceStatisticsIncludeBodiesOutsideTheSelectedReadback()
    {
        using var backend = D3D12GraphicsBackend.Create();
        backend.TryEnumerateAdapters(new(AdapterPreference.HighPerformance, IncludeSoftware: false), [], out int count);
        var adapters = new AdapterInfo[count];
        backend.TryEnumerateAdapters(new(AdapterPreference.HighPerformance, IncludeSoftware: false), adapters, out _);
        using Device device = backend.CreateDevice(new(adapters.First(adapter => adapter.HardwareAccelerated).Id,
            [new(QueueType.Compute)]));
        using var scheduler = new Scheduler(new() { WorkerCount = 2 });
        using World world = WorldJobs.CreateWorld(scheduler: scheduler);
        PhysicsSceneDescription description = PhysicsScenes.All.Single(scene => scene.Id == "face-drop");
        using PhysicsSceneSimulation scene = PhysicsScenes.CreateDevice(world, description, backend, device,
            new(16, 2, 1000d / 60), new(1, 16), new(2, 16, 33, 64, 1), 64, .02f, [0],
            Path.Combine(AppContext.BaseDirectory, "PhysicsShaders"));

        scene.AdvanceTime(scene.StepSeconds);
        PhysicsStepResult completed = scene.CompleteForRead();

        Assert.Equal(0, completed.Number);
        Assert.Equal(scene.Bodies.Span[0], Assert.Single(scene.ReadbackBodies.ToArray()));
        Assert.Equal(1, scene.DeviceAwakeBodies);
        Assert.Equal(0, scene.DeviceContactPoints);
        Assert.Equal(1, scene.DeviceLargestIslandBodies);
    }
}
