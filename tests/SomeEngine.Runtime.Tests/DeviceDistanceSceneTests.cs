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

public sealed class DeviceDistanceSceneTests
{
    [Fact]
    public void TheDeviceSceneAppliesItsOriginalDistanceEdgesAfterTheJointsAreRemoved()
    {
        using var backend = D3D12GraphicsBackend.Create();
        backend.TryEnumerateAdapters(new(AdapterPreference.HighPerformance, IncludeSoftware: false), [], out int count);
        var adapters = new AdapterInfo[count];
        backend.TryEnumerateAdapters(new(AdapterPreference.HighPerformance, IncludeSoftware: false), adapters, out _);
        using Device device = backend.CreateDevice(new(adapters.First(adapter => adapter.HardwareAccelerated).Id,
            [new(QueueType.Compute)], label: "DefaultGame distance edges"));
        using var scheduler = new Scheduler(new() { WorkerCount = 2 });
        using World world = WorldJobs.CreateWorld(scheduler: scheduler);
        PhysicsSceneDescription description = PhysicsScenes.All.Single(scene => scene.Id == "3d-manual-chain");
        using var scene = PhysicsScenes.CreateDevice(world, description, backend, device,
            new PhysicsSceneSettings(Iterations: 128, BodyIterations: 3, StepBudgetMilliseconds: 1000d / 60),
            new DeviceGridSettings(2, 3), new DeviceGridCapacity(20, 2048, 4099, 512, 1), contactCapacity: 512, contactDistance: .02f,
            readbackBodyIndices: [0, 19], shaderDirectory: Path.Combine(AppContext.BaseDirectory, "PhysicsShaders"));
        foreach (var joint in scene.Joints.Span) world.DestroyEntity(joint);
        var last = scene.Bodies.Span[19];
        BodyPose displaced = world.Read<BodyPose>(last);
        displaced.Position += Vector3.UnitX;
        world.Replace(last, displaced);
        PhysicsStepResult result = default;

        for (int number = 0; number < 8; number++)
        {
            scene.Frame.AdvanceTime(scene.StepSeconds * (number == 0 ? 1.5 : 1));
            result = scene.CompleteForRead();
        }

        DistanceRange range = world.Read<DistanceRange>(scene.DistanceRanges.Span[3]);
        BodyPose firstPose = world.Read<BodyPose>(scene.Bodies.Span[0]);
        BodyPose lastPose = world.Read<BodyPose>(last);
        Vector3 firstAnchor = firstPose.Position + Vector3.Transform(range.FirstAnchor, firstPose.Rotation);
        Vector3 lastAnchor = lastPose.Position + Vector3.Transform(range.SecondAnchor, lastPose.Rotation);
        Assert.InRange(Vector3.Distance(firstAnchor, lastAnchor), range.Minimum, range.Maximum + .005f);
        Assert.Equal(lastPose.Position, world.Read<LocalTransform>(last).Value.Position);
        Assert.Equal(7, result.Number);
        Assert.Equal(scene.StepSeconds * 8, result.EndTime, 12);
    }
}
