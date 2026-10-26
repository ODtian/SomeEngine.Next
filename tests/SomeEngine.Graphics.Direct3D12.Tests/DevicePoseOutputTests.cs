using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.Animation.Pose;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;
using SomeEngine.Physics;
using Xunit;

namespace SomeEngine.Graphics.Direct3D12.Tests;

public sealed partial class DeviceXpbdTests
{
    [Fact]
    public void SelectedGraphicsPoseReadRetainsItsBonesWhileBodyResultsAndNewSourceInputsContinue()
    {
        using var backend = D3D12GraphicsBackend.Create();
        backend.TryEnumerateAdapters(new(AdapterPreference.HighPerformance, IncludeSoftware: false), [], out int count);
        var adapters = new AdapterInfo[count];
        backend.TryEnumerateAdapters(new(AdapterPreference.HighPerformance, IncludeSoftware: false), adapters, out _);
        using Device device = backend.CreateDevice(new(adapters.First(value => value.HardwareAccelerated).Id,
            [new(QueueType.Compute), new(QueueType.Graphics)], label: "Game pose and Graphics consumer"));
        using var jobs = new Scheduler(new() { WorkerCount = 2 });
        using World world = WorldJobs.CreateWorld(scheduler: jobs);
        BoneTransform Bone(float x) => new() { Translation = new(x, 0, 0), Rotation = Quaternion.Identity, Scale = Vector3.One };
        var skeleton = new Skeleton([-1, 0], [Bone(0), Bone(1)], [1, 2]);
        using SkeletonInstance instance = new PoseStorage().CreateInstance(skeleton);
        var clip = QuantizedClipData.Encode(new AnimationClip(skeleton, 1,
            ClipSampler.Create<BoneTransform[], FixedDevicePoseSampler>([Bone(0), Bone(1)])));
        var blend = new GpuPoseBlend(skeleton, new GpuBlendClip(clip));
        var chain = new GpuXpbdChain(skeleton, [new(0), new(1)], new(0, -6, 0), damping: 0, step: .1f, iterations: 1);
        Entity target = GpuPoseAnimation.CreateTarget(world, instance);
        GpuPoseTarget input = world.Read<GpuPoseTarget>(target);
        input.Blend = blend; input.Rig = new(new GpuXpbdPose(blend, chain));
        input.Playing = true; input.InputNumber = 1; input.CalculationTime = .1;
        world.Replace(target, input);
        using var physics = new DevicePosePhysics(backend, device, world, new(2, 1, 1));
        physics.Bind(target);
        Entity moving = physics.GetBodies(target, 0).Span[1];
        QueryHandle targets = world.Query(world.QueryDefinition().Read<GpuPoseTarget>());
        QueryHandle members = world.Query(world.QueryDefinition().Read<DeviceBodyBinding>());
        using var sources = new DevicePoseSources(backend, device, world, targets, CompileDevicePosePhysics(),
            new(1, 2, 1, 2, 1, 4096, Operations: 1), physics);
        using var results = new DeviceBodyResults(backend, device, physics.Bodies, world, members, 2);
        var simulation = PhysicsSimulation.Create(world, QueryDefinition.Empty, physics.Upload().Then(sources.Evaluate()).Then(sources.CopyOutput()), 1);
        simulation.Schedule(0, 0, .1).Complete();
        using DevicePoseOutputRead selected = GpuPoseAnimation.ReadDeviceOutput(world, target);

        input.InputNumber = 2; input.CalculationTime = .2; world.Replace(target, input);
        simulation.Schedule(1, .1, .1).Complete();
        input.InputNumber = 3; input.CalculationTime = .3; world.Replace(target, input);
        PhysicsStepResult latest = simulation.Schedule(2, .2, .1);
        // The selected Body resource waits for numerical work and can be consumed before the held Graphics copy.
        PhysicsSimulation.Create(world, QueryDefinition.Empty, results.Readback(64), 1).Schedule(2, .2, .1).Complete();

        Assert.Equal(2, world.Read<BodyStepState>(moving).Number);
        Assert.Equal(.3, world.Read<BodyStepState>(moving).Time, 8);
        Assert.Equal(1, world.Read<DevicePoseOutput>(target).Number);
        Assert.Equal(.2, world.Read<DevicePoseOutput>(target).Time, 8);
        input.InputNumber = 4; input.CalculationTime = .4; world.Replace(target, input);
        Assert.Equal(4ul, world.Read<GpuPoseTarget>(target).InputNumber);

        using Buffer copied = backend.CreateBuffer(device, new(128, BufferUsages.CopyDestination), MemoryType.Readback);
        using CommandContext commands = backend.CreateCommandContext(device, new(QueueType.Graphics, 0, 1, Label: "Copy selected Game pose"));
        backend.Begin(commands, new(0, 0, 2));
        DevicePoseOutput output = selected.Output;
        backend.CopyBuffer(commands, new(output.Buffer, output.Models.Offset, copied, 0, output.Models.Size));
        using RecordedCommands recorded = backend.End(commands);
        QueueCompletion completion = backend.Submit(backend.GetQueue(device, QueueType.Graphics), new([], [], [recorded], [], []));
        selected.CompleteAfter(backend, device, completion);
        latest.Complete();
        using MappedBuffer mapping = backend.Map(copied, MapType.Read, BufferRange.Whole);
        mapping.Invalidate(mapping.Range);

        Assert.InRange(Vector3.Distance(Vector3.UnitX, MemoryMarshal.Cast<byte, Matrix4x4>(mapping.Bytes)[1].Translation), 0, .001f);
        Assert.Equal(0, output.Number);
        Assert.Equal(.1, output.Time, 8);
        Assert.Equal(2, world.Read<DevicePoseOutput>(target).Number);
        Assert.Equal(.3, world.Read<DevicePoseOutput>(target).Time, 8);
        world.ReleaseQuery(members); world.ReleaseQuery(targets);
    }
}
