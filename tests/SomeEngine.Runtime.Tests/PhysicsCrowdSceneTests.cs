using System.Numerics;
using DefaultGame;
using SomeEngine.Animation.Pose;
using SomeEngine.ECS;
using SomeEngine.ECS.Systems;
using SomeEngine.Graphics;
using SomeEngine.Graphics.Direct3D12;
using SomeEngine.Job;
using SomeEngine.Physics;

namespace SomeEngine.Runtime.Tests;

[Trait("Category", "Hardware")]
[Trait("Category", "Performance")]
public sealed class PhysicsCrowdSceneTests
{
    [Fact]
    public void TwoThousandCharactersAndTheirBoneClothCompleteTheSharedPhysicalFrame()
    {
        using var backend = D3D12GraphicsBackend.Create();
        backend.TryEnumerateAdapters(new(AdapterPreference.HighPerformance, IncludeSoftware: false), [], out int count);
        var adapters = new AdapterInfo[count];
        backend.TryEnumerateAdapters(new(AdapterPreference.HighPerformance, IncludeSoftware: false), adapters, out _);
        using Device device = backend.CreateDevice(new(adapters.First(adapter => adapter.HardwareAccelerated).Id,
            [new(QueueType.Compute)]));
        using var scheduler = new Scheduler(new() { WorkerCount = Environment.ProcessorCount });
        using World world = WorldJobs.CreateWorld(scheduler: scheduler);
        var example = PhysicsCrowdExample.CreateDevice(2000, backend, device);
        using var scene = example.CreateSimulation(world);
        var tip = scene.CharacterBodies[^1].Span[^1];
        Vector3 initial = world.Read<BodyPose>(tip).Position;

        scene.AdvanceTime(scene.StepSeconds);
        PhysicsStepResult completed = scene.CompleteForRead();

        Assert.Equal(2000, scene.CharacterTargets.Count);
        Assert.Equal(2000, scene.ClothTargets.Count);
        Assert.Equal(40001, scene.Bodies.Count);
        Assert.Equal(32000, scene.ClothBodies.Count);
        Assert.Equal(1000, scene.WorldDrivers.Count);
        Assert.Equal(1000, scene.ParentDrivers.Count);
        Assert.Equal(0, completed.Number);
        Assert.Equal(1d / 60, completed.EndTime, 12);
        Assert.Equal(completed.Number, world.Read<BodyStepState>(tip).Number);
        Assert.Equal(completed.EndTime, world.Read<BodyStepState>(tip).Time, 12);
        Assert.True(world.Read<BodyPose>(tip).Position.Z > initial.Z + .001f);
        Assert.True(world.Read<BodyVelocity>(tip).Linear.Z > .1f);
        using DevicePoseOutputRead output = GpuPoseAnimation.ReadDeviceOutput(world, scene.CharacterTargets[^1]);
        Assert.Equal(completed.Number, output.Output.Number);
        Assert.Equal(completed.EndTime, output.Output.Time, 12);
    }
}
