using DefaultGame;
using System.Numerics;
using SomeEngine.Core.ECS.Components;
using SomeEngine.ECS;
using SomeEngine.ECS.Systems;
using SomeEngine.Graphics;
using SomeEngine.Graphics.Direct3D12;
using SomeEngine.Job;
using SomeEngine.Physics;

namespace SomeEngine.Runtime.Tests;

public sealed class PhysicsDeviceSupportGroupSceneTests
{
    [Fact]
    public void TwoPyramidsKeepTheirSupportAtTheFirstArrayRows()
    {
        using var backend = D3D12GraphicsBackend.Create();
        backend.TryEnumerateAdapters(new(AdapterPreference.HighPerformance, IncludeSoftware: false), [], out int count);
        var adapters = new AdapterInfo[count];
        backend.TryEnumerateAdapters(new(AdapterPreference.HighPerformance, IncludeSoftware: false), adapters, out _);
        using Device device = backend.CreateDevice(new(adapters.First(adapter => adapter.HardwareAccelerated).Id,
            [new(QueueType.Compute)]));
        using var scheduler = new Scheduler(new() { WorkerCount = 2 });
        using World world = WorldJobs.CreateWorld(scheduler: scheduler);
        PhysicsSceneDescription description = PhysicsScenes.All.Single(scene => scene.Id == "bepu-pyramids");
        PhysicsSceneExample example = PhysicsSceneExample.CreateDevice(description, backend, device);
        using PhysicsSceneSimulation scene = example.CreateSimulation(world);
        world.Replace(scene.Bodies.Span[0], new BoxGeometry(new Vector3(243, 63, .5f)));
        for (int index = 0; index < scene.Bodies.Length; index++)
        {
            var body = scene.Bodies.Span[index];
            ContactMaterial material = world.Read<ContactMaterial>(body);
            material.StaticFriction = material.DynamicFriction = .6f;
            world.Replace(body, material);
            if (index == 0) continue;
            BodyPose pose = world.Read<BodyPose>(body);
            int row = (index - 1) / 210;
            pose.Position = new(pose.Position.X - 228, -60 + row * 6, pose.Position.Z);
            world.Replace(body, pose);
        }

        PhysicsStepResult completed = default;
        for (int number = 0; number < 180; number++)
        {
            scene.AdvanceTime(scene.StepSeconds);
            completed = scene.CompleteForRead();
            foreach (var body in scene.Bodies.Span[1..])
            {
                float speed = world.Read<BodyVelocity>(body).Linear.Length();
                Assert.True(speed <= 1,
                    $"Step={number}; Body={body}; Speed={speed}; Pose={world.Read<BodyPose>(body).Position}; Rotation={world.Read<BodyPose>(body).Rotation}");
            }
        }

        foreach (var body in scene.Bodies.Span[1..])
        {
            Assert.True(world.Read<BodyActivity>(body).Sleeping,
                $"Body={body}; QuietSeconds={world.Read<BodyActivity>(body).QuietSeconds}; Velocity={world.Read<BodyVelocity>(body).Linear}; Angular={world.Read<BodyVelocity>(body).Angular}; DeviceAwake={scene.DeviceAwakeBodies}");
            Assert.InRange(world.Read<BodyVelocity>(body).Linear.Length(), 0, .01f);
            Assert.InRange(world.Read<BodyPose>(body).Position.Z, .499f, 19.501f);
        }
        Assert.Equal(179, completed.Number);
        Assert.Equal(0, scene.DeviceAwakeBodies);
        Assert.Equal(210, scene.DeviceLargestIslandBodies);
    }

    [Fact]
    public void TheOriginalTwoPyramidsKeepTheirOwnSupportAndSleepOnTheSharedFloor()
    {
        using var backend = D3D12GraphicsBackend.Create();
        backend.TryEnumerateAdapters(new(AdapterPreference.HighPerformance, IncludeSoftware: false), [], out int count);
        var adapters = new AdapterInfo[count];
        backend.TryEnumerateAdapters(new(AdapterPreference.HighPerformance, IncludeSoftware: false), adapters, out _);
        using Device device = backend.CreateDevice(new(adapters.First(adapter => adapter.HardwareAccelerated).Id,
            [new(QueueType.Compute)]));
        using var scheduler = new Scheduler(new() { WorkerCount = 2 });
        using World world = WorldJobs.CreateWorld(scheduler: scheduler);
        PhysicsSceneDescription description = PhysicsScenes.All.Single(scene => scene.Id == "bepu-pyramids");
        PhysicsSceneExample example = PhysicsSceneExample.CreateDevice(description, backend, device);
        using PhysicsSceneSimulation scene = example.CreateSimulation(world);
        PhysicsStepResult completed = default;

        for (int number = 0; number < 180; number++)
        {
            scene.AdvanceTime(scene.StepSeconds * (number == 0 ? 1.5 : 1));
            completed = scene.CompleteForRead();
        }

        Assert.Equal(421, scene.Bodies.Length);
        foreach (var body in scene.Bodies.Span[1..])
        {
            Assert.True(world.Read<BodyActivity>(body).Sleeping,
                $"Body={body}; Pose={world.Read<BodyPose>(body).Position}; Velocity={world.Read<BodyVelocity>(body).Linear}; AngularVelocity={world.Read<BodyVelocity>(body).Angular}; Awake={scene.DeviceAwakeBodies}; LargestIsland={scene.DeviceLargestIslandBodies}");
            Assert.InRange(world.Read<BodyVelocity>(body).Linear.Length(), 0, .01f);
            Assert.InRange(world.Read<BodyPose>(body).Position.Z, .499f, 19.501f);
            Assert.Equal(world.Read<BodyPose>(body).Position, world.Read<LocalTransform>(body).Value.Position);
        }
        Assert.Equal(179, completed.Number);
        Assert.Equal(180 * scene.StepSeconds, completed.EndTime, 12);
        Assert.Equal(0, scene.DeviceAwakeBodies);
        Assert.Equal(210, scene.DeviceLargestIslandBodies);

    }
}
