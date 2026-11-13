using System.Numerics;
using DefaultGame;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;
using SomeEngine.Graphics;
using SomeEngine.Graphics.Direct3D12;
using SomeEngine.Job;
using SomeEngine.Physics;

namespace SomeEngine.Runtime.Tests;

public sealed class PhysicsDeviceContactRetirementTests
{
    [Fact]
    public void TheDeviceSceneRetiresItsRemovedFloorContactsWithoutAnEventConsumer()
    {
        using var backend = D3D12GraphicsBackend.Create();
        backend.TryEnumerateAdapters(new(AdapterPreference.HighPerformance, IncludeSoftware: false), [], out int count);
        var adapters = new AdapterInfo[count];
        backend.TryEnumerateAdapters(new(AdapterPreference.HighPerformance, IncludeSoftware: false), adapters, out _);
        using Device device = backend.CreateDevice(new(adapters.First(adapter => adapter.HardwareAccelerated).Id,
            [new(QueueType.Compute)]));
        using var scheduler = new Scheduler(new() { WorkerCount = 2 });
        using World world = new World(execution: new WorldJobs(scheduler));
        PhysicsSceneDescription description = PhysicsScenes.All.Single(scene => scene.Id == "face-drop");
        using PhysicsSceneSimulation scene = PhysicsScenes.CreateDevice(world, description, backend, device,
            new PhysicsSceneSettings(16, 2, 1000d / 60)
            {
                GeometryInputsFixed = true, ReadContacts = true,
                ContactNormalPenalty = 100, ContactTangentPenalty = 100,
                ContactSupports = new(Vector3.UnitZ, 1e-5f, 1e-4f, 1e-4f, 1e-4f, .99f, .99f, 1e-6f),
            }, new(1, 16), new(2, 16, 33, 64, 1), 64, .02f, [0, 1],
            Path.Combine(AppContext.BaseDirectory, "PhysicsShaders"));
        for (int frame = 0; frame < 30; frame++)
        {
            scene.AdvanceTime(scene.StepSeconds * (frame == 0 ? 1.5 : 1));
            scene.CompleteForRead();
        }
        Assert.NotNull(scene.Contacts);
        Assert.True(scene.Contacts.OpenView().PointCount > 0);
        int entityCount = world.EntityCount;
        Entity floor = scene.Bodies.Span[0];
        BodyPose lowered = world.ReadComponentData<BodyPose>(floor);
        lowered.Position.Z -= 10;
        world.Set(floor, lowered);

        scene.AdvanceTime(scene.StepSeconds);
        scene.CompleteForRead();

        Assert.Equal(0, scene.Contacts.OpenView().PointCount);
        Assert.Equal(entityCount, world.EntityCount);
        
    }
}
