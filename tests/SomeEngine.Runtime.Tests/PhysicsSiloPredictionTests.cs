using System.Numerics;
using DefaultGame;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;
using SomeEngine.Graphics;
using SomeEngine.Graphics.Direct3D12;
using SomeEngine.Job;
using SomeEngine.Physics;
using Xunit;
using Xunit.Abstractions;

namespace SomeEngine.Runtime.Tests;

public sealed class PhysicsSiloPredictionTests(ITestOutputHelper output)
{
    [Fact]
    public void ASpinningSiloSphereStaysWithinTheClosedFloorContactDistanceAcrossFrames()
    {
        using var backend = D3D12GraphicsBackend.Create();
        backend.TryEnumerateAdapters(new(AdapterPreference.HighPerformance, IncludeSoftware: false), [], out int count);
        var adapters = new AdapterInfo[count];
        backend.TryEnumerateAdapters(new(AdapterPreference.HighPerformance, IncludeSoftware: false), adapters, out _);
        using Device device = backend.CreateDevice(new(adapters.First(adapter => adapter.HardwareAccelerated).Id,
            [new(QueueType.Compute)]));
        using var scheduler = new Scheduler(new() { WorkerCount = 2 });
        using World world = WorldJobs.CreateWorld(scheduler: scheduler);
        PhysicsSceneExample example = PhysicsSceneExample.CreateSiloDevice(3, backend, device);
        using PhysicsSceneSimulation scene = example.CreateSimulation(world);
        Entity sphere = scene.Bodies.ToArray().Single(world.Has<SphereGeometry>);
        BodyPose initial = world.Read<BodyPose>(sphere);
        initial.Position.Z = .3f;
        world.Replace(sphere, initial);
        world.Replace(sphere, new BodyVelocity
        {
            Linear = new(0, 0, -30),
            Angular = new(97.07641f, -19.222605f, -3.6943278f),
        });
        SphereGeometry geometry = world.Read<SphereGeometry>(sphere);
        for (int number = 0; number < 2; number++)
        {
            scene.AdvanceTime(scene.StepSeconds * (number == 0 ? 1.5 : 1));
            PhysicsStepResult result = scene.CompleteForRead();
            BodyPose pose = world.Read<BodyPose>(sphere);
            BodyVelocity velocity = world.Read<BodyVelocity>(sphere);
            BodyStepState step = world.Read<BodyStepState>(sphere);
            CollisionBounds bounds = SphereGeometry.Bounds(in geometry, in pose);
            output.WriteLine($"Frame={result.Number}; Start={result.StartTime:R}; End={result.EndTime:R}; CentreZ={pose.Position.Z:R}; BottomZ={bounds.Min.Z:R}; Velocity={velocity.Linear}; Angular={velocity.Angular}; Rotation={pose.Rotation}");
            Assert.Equal(number, result.Number);
            Assert.Equal(number, step.Number);
            Assert.Equal(0, step.Substep);
            Assert.InRange(Math.Abs(result.StartTime - number * scene.StepSeconds), 0, 1e-12);
            Assert.InRange(Math.Abs(result.EndTime - (number + 1) * scene.StepSeconds), 0, 1e-12);
            Assert.Equal(result.EndTime, step.Time);
            Assert.InRange(bounds.Min.Z, -scene.Settings.ContactDistance, scene.Settings.ContactDistance);
            Assert.InRange(MathF.Abs(Quaternion.Dot(initial.Rotation, pose.Rotation)), 0, .9999f);
        }
    }

    [Fact]
    public void ASpinningSiloHullStaysWithinTheClosedFloorContactDistanceAcrossFrames()
    {
        using var backend = D3D12GraphicsBackend.Create();
        backend.TryEnumerateAdapters(new(AdapterPreference.HighPerformance, IncludeSoftware: false), [], out int count);
        var adapters = new AdapterInfo[count];
        backend.TryEnumerateAdapters(new(AdapterPreference.HighPerformance, IncludeSoftware: false), adapters, out _);
        using Device device = backend.CreateDevice(new(adapters.First(adapter => adapter.HardwareAccelerated).Id,
            [new(QueueType.Compute)]));
        using var scheduler = new Scheduler(new() { WorkerCount = 2 });
        using World world = WorldJobs.CreateWorld(scheduler: scheduler);
        PhysicsSceneExample example = PhysicsSceneExample.CreateSiloDevice(3, backend, device);
        using PhysicsSceneSimulation scene = example.CreateSimulation(world);
        Entity hull = scene.Bodies.ToArray().Single(world.Has<ConvexHullGeometry>);
        var initial = new BodyPose
        {
            Position = new(0, 0, .3f),
            Rotation = new(-.65796155f, -.29533523f, -.57100254f, .39219853f),
        };
        world.Replace(hull, initial);
        world.Replace(hull, new BodyVelocity
        {
            Linear = new(0, 0, -30),
            Angular = new(-184.49846f, 23.319624f, -20.924328f),
        });
        ConvexHullGeometry geometry = world.Read<ConvexHullGeometry>(hull);
        output.WriteLine("Case=IsolatedSiloHullRotation; FactoryParticles=3; OriginalFloorMaterialsInertiaAndStep=true");
        for (int number = 0; number < 2; number++)
        {
            scene.AdvanceTime(scene.StepSeconds * (number == 0 ? 1.5 : 1));
            PhysicsStepResult result = scene.CompleteForRead();
            BodyPose pose = world.Read<BodyPose>(hull);
            BodyVelocity velocity = world.Read<BodyVelocity>(hull);
            BodyStepState step = world.Read<BodyStepState>(hull);
            CollisionBounds bounds = ConvexHullGeometry.Bounds(in geometry, in pose);
            output.WriteLine($"Frame={result.Number}; Start={result.StartTime:R}; End={result.EndTime:R}; CentreZ={pose.Position.Z:R}; BottomZ={bounds.Min.Z:R}; Velocity={velocity.Linear}; Angular={velocity.Angular}; Rotation={pose.Rotation}; PredictedCentreZ={step.PredictedCenterOfMass.Z:R}; PredictedRotation={step.PredictedRotation}");
            Assert.Equal(number, result.Number);
            Assert.Equal(number, step.Number);
            Assert.Equal(0, step.Substep);
            Assert.InRange(Math.Abs(result.StartTime - number * scene.StepSeconds), 0, 1e-12);
            Assert.InRange(Math.Abs(result.EndTime - (number + 1) * scene.StepSeconds), 0, 1e-12);
            Assert.Equal(result.EndTime, step.Time);
            Assert.InRange(bounds.Min.Z, -scene.Settings.ContactDistance, scene.Settings.ContactDistance);
        }
    }
}
