using DefaultGame;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;
using SomeEngine.Graphics;
using SomeEngine.Graphics.Direct3D12;
using SomeEngine.Job;
using SomeEngine.Physics;

namespace SomeEngine.Runtime.Tests;

public sealed class PhysicsMixedSiloTests
{
    [Fact]
    public void MixedSiloDropsPilesAndDrainsThroughTheCompletedSceneFrame()
    {
        using var backend = D3D12GraphicsBackend.Create();
        backend.TryEnumerateAdapters(new(AdapterPreference.HighPerformance, IncludeSoftware: false), [], out int count);
        var adapters = new AdapterInfo[count];
        backend.TryEnumerateAdapters(new(AdapterPreference.HighPerformance, IncludeSoftware: false), adapters, out _);
        using Device device = backend.CreateDevice(new(adapters.First(adapter => adapter.HardwareAccelerated).Id,
            [new(QueueType.Compute)]));
        using var scheduler = new Scheduler(new() { WorkerCount = 2 });
        using World world = WorldJobs.CreateWorld(scheduler: scheduler);
        PhysicsSceneExample example = PhysicsSceneExample.CreateSiloDevice(24, backend, device);
        using PhysicsSceneSimulation scene = example.CreateSimulation(world);
        Entity[] particles = scene.Bodies.ToArray().Where(body => world.Read<BodyMass>(body).Motion == BodyMotion.Dynamic).ToArray();
        Assert.Equal(24, particles.Length);
        Assert.Equal(8, particles.Count(world.Has<SphereGeometry>));
        Assert.Equal(8, particles.Count(world.Has<BoxGeometry>));
        Assert.Equal(8, particles.Count(world.Has<ConvexHullGeometry>));
        Entity[] observed =
        [
            particles.First(world.Has<SphereGeometry>), particles.First(world.Has<BoxGeometry>),
            particles.First(world.Has<ConvexHullGeometry>),
        ];
        float[] released = observed.Select(body => world.Read<BodyPose>(body).Position.Z).ToArray();
        for (int number = 0; number < 180; number++) Advance(number);
        for (int index = 0; index < observed.Length; index++)
        {
            float height = world.Read<BodyPose>(observed[index]).Position.Z;
            Assert.InRange(height, .1f, released[index] - .1f);
            Assert.InRange(world.Read<BodyVelocity>(observed[index]).Linear.Length(), 0, .15f);
        }
        for (int number = 180; number < example.StepCount; number++) Advance(number);
        Assert.InRange(world.Read<BodyPose>(scene.Bodies.Span[0]).Position.X, 4, float.MaxValue);
        foreach (Entity body in observed)
            Assert.True(world.Read<BodyPose>(body).Position.Z < -2);

        void Advance(int number)
        {
            scene.AdvanceTime(scene.StepSeconds * (number == 0 ? 1.5 : 1));
            Assert.Equal(number, scene.CompleteForRead().Number);
        }
    }
}
