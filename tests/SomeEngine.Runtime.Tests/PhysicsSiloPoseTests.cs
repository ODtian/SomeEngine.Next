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

public sealed class PhysicsSiloPoseTests
{
    [Fact]
    public void AFullSiloPublishesEveryCompletedBodyPoseToItsSceneTransform()
    {
        using var backend = D3D12GraphicsBackend.Create();
        backend.TryEnumerateAdapters(new(AdapterPreference.HighPerformance, IncludeSoftware: false), [], out int count);
        var adapters = new AdapterInfo[count];
        backend.TryEnumerateAdapters(new(AdapterPreference.HighPerformance, IncludeSoftware: false), adapters, out _);
        using Device device = backend.CreateDevice(new(adapters.First(adapter => adapter.HardwareAccelerated).Id,
            [new(QueueType.Compute)]));
        using var scheduler = new Scheduler(new() { WorkerCount = 2 });
        using World world = WorldJobs.CreateWorld(scheduler: scheduler);
        PhysicsSceneExample example = PhysicsSceneExample.CreateSiloDevice(300, backend, device);
        using PhysicsSceneSimulation scene = example.CreateSimulation(world);
        Assert.Equal(309, scene.Bodies.Length);

        for (int number = 0; number < 2; number++)
        {
            scene.AdvanceTime(scene.StepSeconds * (number == 0 ? 1.5 : 1));
            PhysicsStepResult result = scene.CompleteForRead();
            Assert.Equal(number, result.Number);
            Assert.Equal(scene.Bodies.Length, scene.ReadbackBodies.Length);
            foreach (var body in scene.Bodies.Span)
            {
                BodyPose pose = world.Read<BodyPose>(body);
                LocalTransform transform = world.Read<LocalTransform>(body);
                Assert.InRange(Vector3.Distance(pose.Position, transform.Value.Position), 0, 1e-6f);
                Assert.InRange(MathF.Abs(Quaternion.Dot(pose.Rotation, transform.Value.Rotation)), .99999f, 1.00001f);
                if (world.Read<BodyMass>(body).Motion == BodyMotion.Dynamic)
                {
                    BodyStepState step = world.Read<BodyStepState>(body);
                    Assert.Equal(result.Number, step.Number);
                    Assert.Equal(result.EndTime, step.Time);
                }
            }
        }
    }
}
