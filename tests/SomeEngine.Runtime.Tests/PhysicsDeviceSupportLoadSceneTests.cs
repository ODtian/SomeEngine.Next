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

public sealed class PhysicsDeviceSupportLoadSceneTests
{
    [Fact]
    public void TheDeviceSceneTransfersTheSharedTowerLoadThroughBothSupportsToTheFloor()
    {
        using var backend = D3D12GraphicsBackend.Create();
        backend.TryEnumerateAdapters(new(AdapterPreference.HighPerformance, IncludeSoftware: false), [], out int count);
        var adapters = new AdapterInfo[count];
        backend.TryEnumerateAdapters(new(AdapterPreference.HighPerformance, IncludeSoftware: false), adapters, out _);
        using Device device = backend.CreateDevice(new(adapters.First(adapter => adapter.HardwareAccelerated).Id,
            [new(QueueType.Compute)]));
        using var scheduler = new Scheduler(new() { WorkerCount = 2 });
        using World world = WorldJobs.CreateWorld(scheduler: scheduler);
        var received = new List<PhysicsEvent>();
        PhysicsSceneDescription description = PhysicsScenes.All.Single(scene => scene.Id == "3d-stack");
        using PhysicsSceneSimulation scene = PhysicsScenes.CreateDevice(world, description, backend, device,
            new PhysicsSceneSettings(16, 2, 1000d / 60)
            {
                GeometryInputsFixed = true,
                EquationBindingsFixed = true,
                ContactNormalPenalty = 100, ContactTangentPenalty = 100,
                ContactSupports = new(Vector3.UnitZ, 1e-5f, 1e-4f, 1e-4f, 1e-4f, .99f, .99f, 1e-6f),
            }, new(1, 16), new(11, 88, 177, 352, 1), 352, .02f,
            Enumerable.Range(0, 11).ToArray(), Path.Combine(AppContext.BaseDirectory, "PhysicsShaders"),
            new RecordContacts(received), [JobResourceAccess.Write(scheduler.GetResourceToken(received))]);
        var positions = new Vector3[11];
        for (int index = 1; index < scene.Bodies.Length; index++)
        {
            var body = scene.Bodies.Span[index];
            BodyPose pose = world.Read<BodyPose>(body);
            // Two lower supports carry a common tower; upper bodies run in reverse entity order.
            pose.Position = index < 3 ? new(index == 1 ? -.25f : .25f, 0, 1) : new(0, 0, 12 - index);
            positions[index] = pose.Position;
            world.Replace(body, pose);
            if (index < 3) world.Replace(body, new BoxGeometry(new(.25f, .5f, .5f)));
        }

        scene.AdvanceTime(scene.StepSeconds);
        PhysicsStepResult completed = scene.CompleteForRead();

        var floor = scene.Bodies.Span[0];
        float floorForce = received.Where(value => value.First == floor || value.Second == floor)
            .Sum(value => value.Second == floor ? -value.Force.Z : value.Force.Z);
        // Ten original unit masses under 10 m/s² gravity transfer 100 N to the floor.
        Assert.InRange(floorForce, 99.9f, 100.1f);
        for (int index = 1; index < scene.Bodies.Length; index++)
        {
            var body = scene.Bodies.Span[index];
            Vector3 position = world.Read<BodyPose>(body).Position;
            Assert.InRange(Vector3.Distance(position, positions[index]), 0, .001f);
            Assert.InRange(world.Read<BodyVelocity>(body).Linear.Length(), 0, .01f);
            Assert.Equal(position, world.Read<LocalTransform>(body).Value.Position);
        }
        Assert.Equal(0, completed.Number);
        Assert.Equal(10, scene.DeviceLargestIslandBodies);
    }

    private readonly struct RecordContacts(List<PhysicsEvent> received) : IPhysicsEventConsumer
    {
        public void Consume(scoped in PhysicsStepEvents step, scoped ReadOnlySpan<PhysicsEvent> events, ref JobCommandWriter commands)
        {
            foreach (PhysicsEvent value in events)
                if (value.Kind == PhysicsEventKind.Contact) received.Add(value);
        }
    }
}
