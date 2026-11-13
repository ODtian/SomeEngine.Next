using System.Numerics;
using DefaultGame;
using SomeEngine.ECS;
using SomeEngine.ECS.Systems;
using SomeEngine.Graphics;
using SomeEngine.Graphics.Direct3D12;
using SomeEngine.Job;
using SomeEngine.Physics;

namespace SomeEngine.Runtime.Tests;

public sealed class PhysicsDeviceContactSceneTests
{
    [Fact]
    public void TheDeviceSceneDeliversItsSleepingSupportForceAndEndsTheRemovedFloorContacts()
    {
        using var backend = D3D12GraphicsBackend.Create();
        backend.TryEnumerateAdapters(new(AdapterPreference.HighPerformance, IncludeSoftware: false), [], out int count);
        var adapters = new AdapterInfo[count];
        backend.TryEnumerateAdapters(new(AdapterPreference.HighPerformance, IncludeSoftware: false), adapters, out _);
        using Device device = backend.CreateDevice(new(adapters.First(adapter => adapter.HardwareAccelerated).Id,
            [new(QueueType.Compute)]));
        using var scheduler = new Scheduler(new() { WorkerCount = 2 });
        using World world = new World(execution: new WorldJobs(scheduler));
        var received = new List<(long Number, PhysicsEvent Value)>();
        PhysicsSceneDescription description = PhysicsScenes.All.Single(scene => scene.Id == "face-drop");
        using PhysicsSceneSimulation scene = PhysicsScenes.CreateDevice(world, description, backend, device,
            new PhysicsSceneSettings(16, 2, 1000d / 60)
            {
                GeometryInputsFixed = true,
                EquationBindingsFixed = true,
                ContactNormalPenalty = 100, ContactTangentPenalty = 100,
                ContactSupports = new(Vector3.UnitZ, 1e-5f, 1e-4f, 1e-4f, 1e-4f, .99f, .99f, 1e-6f),
            }, new(1, 16), new(2, 16, 33, 64, 1), 64, .02f, [0, 1],
            Path.Combine(AppContext.BaseDirectory, "PhysicsShaders"), new RecordContacts(received),
            [JobResourceAccess.Write(scheduler.GetResourceToken(received))]);

        PhysicsStepResult completed = default;
        for (int number = 0; number < 90; number++)
        {
            scene.AdvanceTime(scene.StepSeconds * (number == 0 ? 1.5 : 1));
            completed = scene.CompleteForRead();
        }

        var touching = received.Where(item => item.Number == completed.Number).Select(item => item.Value).ToArray();
        Assert.Single(touching);
        Assert.Equal(4, scene.Contacts!.OpenView().PointCount);
        Assert.All(touching, value => Assert.Equal(PhysicsEventPhase.Stay, value.Phase));
        Assert.InRange(MathF.Abs(touching.Sum(value => value.Force.Z)), 39.9f, 40.1f);
        Assert.True(world.ReadComponentData<BodyActivity>(scene.Bodies.Span[1]).Sleeping);
        var floor = scene.Bodies.Span[0];
        BodyPose lowered = world.ReadComponentData<BodyPose>(floor);
        lowered.Position.Z -= 10;
        world.Set(floor, lowered);

        scene.AdvanceTime(scene.StepSeconds);
        PhysicsStepResult removed = scene.CompleteForRead();

        var ended = received.Where(item => item.Number == removed.Number).Select(item => item.Value).ToArray();
        Assert.Single(ended);
        Assert.Equal(0, scene.Contacts!.OpenView().PointCount);
        Assert.All(ended, value => Assert.Equal(PhysicsEventPhase.End, value.Phase));
        Assert.Equal(touching.Select(value => (value.First, value.Second, value.FirstShape, value.SecondShape)),
            ended.Select(value => (value.First, value.Second, value.FirstShape, value.SecondShape)));
        Assert.All(ended, value => Assert.Equal(removed.EndTime, value.Time));
    }

    private readonly struct RecordContacts(List<(long Number, PhysicsEvent Value)> received) : IScenePhysicsEvents
    {
        public void Consume(ReadOnlySpan<PhysicsEvent> events)
        {
            foreach (PhysicsEvent value in events)
                if (value.Kind == PhysicsEventKind.Contact) received.Add((value.StepNumber, value));
        }
    }
}
