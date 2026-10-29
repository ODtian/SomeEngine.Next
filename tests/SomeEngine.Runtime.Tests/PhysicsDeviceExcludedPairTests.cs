using DefaultGame;
using SomeEngine.ECS;
using SomeEngine.ECS.Systems;
using SomeEngine.Graphics;
using SomeEngine.Graphics.Direct3D12;
using SomeEngine.Job;
using SomeEngine.Physics;

namespace SomeEngine.Runtime.Tests;

public sealed class PhysicsDeviceExcludedPairTests
{
    [Fact]
    public void TheDeviceSceneKeepsItsOriginalPairExclusionWhenTheTwoBodiesOverlap()
    {
        using var backend = D3D12GraphicsBackend.Create();
        backend.TryEnumerateAdapters(new(AdapterPreference.HighPerformance, IncludeSoftware: false), [], out int count);
        var adapters = new AdapterInfo[count];
        backend.TryEnumerateAdapters(new(AdapterPreference.HighPerformance, IncludeSoftware: false), adapters, out _);
        using Device device = backend.CreateDevice(new(adapters.First(adapter => adapter.HardwareAccelerated).Id,
            [new(QueueType.Compute)]));
        using var scheduler = new Scheduler(new() { WorkerCount = 2 });
        using World world = WorldJobs.CreateWorld(scheduler: scheduler);
        PhysicsSceneDescription description = PhysicsScenes.All.Single(scene => scene.Id == "angular-locked-train");
        PhysicsSceneExample example = PhysicsSceneExample.CreateDevice(description, backend, device);
        using PhysicsSceneSimulation scene = example.CreateSimulation(world);
        foreach (var joint in scene.Joints.Span) world.DestroyEntity(joint);
        var moving = scene.Bodies.Span[1];
        world.Replace(moving, world.Read<BodyPose>(scene.Bodies.Span[0]));

        scene.AdvanceTime(scene.StepSeconds);
        PhysicsStepResult completed = scene.CompleteForRead();

        Assert.Equal(0, completed.Number);
        Assert.Equal(0, scene.DeviceContactPoints);
        Assert.InRange(world.Read<BodyPose>(moving).Position.Z, 9.99721f, 9.99724f);
        Assert.Equal(7, example.Describe()["CollisionExclusions"]!.GetValue<int>());
    }
}
