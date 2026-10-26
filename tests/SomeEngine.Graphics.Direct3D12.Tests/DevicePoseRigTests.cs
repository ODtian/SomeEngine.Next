using System.Numerics;
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
    public void GameTwoBoneAndAimGoalsProduceTheirCompletedDeviceModelPose()
    {
        using var backend = D3D12GraphicsBackend.Create();
        using Device device = CreateDevice(backend);
        using var jobs = new Scheduler(new() { WorkerCount = 2 });
        using World world = WorldJobs.CreateWorld(scheduler: jobs);
        BoneTransform Bone(Vector3 position) => new() { Translation = position, Rotation = Quaternion.Identity, Scale = Vector3.One };
        var skeleton = new Skeleton([-1, 0, 1, 2, 3, 0, 5, 6, 7],
            [Bone(Vector3.Zero), Bone(Vector3.Zero), Bone(Vector3.UnitY), Bone(Vector3.UnitY), Bone(new(0, .25f, 0)),
             Bone(Vector3.UnitY), Bone(new(0, .5f, 0)), Bone(new(0, .5f, 0)), Bone(new(0, .3f, 0))],
            [1, 2, 3, 4, 5, 6, 7, 8, 9]);
        using SkeletonInstance instance = new PoseStorage().CreateInstance(skeleton);
        using SkeletonInstance expected = new PoseStorage().CreateInstance(skeleton);
        var clip = QuantizedClipData.Encode(new AnimationClip(skeleton, 1,
            ClipSampler.Create<BoneTransform[], FixedDevicePoseSampler>(skeleton.ReferencePose.ToArray())));
        var blend = new GpuPoseBlend(skeleton, new GpuBlendClip(clip));
        var limb = new GpuTwoBoneChain(skeleton, 1, 2, 3, maximumBend: MathF.PI / 2);
        var aim = new GpuAimChain(skeleton,
            [new(5, Vector3.UnitY, .25f, MathF.PI / 4), new(6, Vector3.UnitY, .5f, MathF.PI / 4), new(7, Vector3.UnitY, 1, MathF.PI / 4)]);
        Entity target = GpuPoseAnimation.CreateTarget(world, instance);
        GpuPoseTarget input = world.Read<GpuPoseTarget>(target);
        input.Blend = blend; input.Rig = new(new GpuAimPose(new GpuTwoBonePose(blend, limb, 0), aim, 1));
        input.Playing = true; input.InputNumber = 1;
        world.Replace(target, input);
        world.AddBuffer<GpuPoseGoal>(target);
        world.ExecuteBufferWrite<GpuPoseGoal>(target, static goals =>
        {
            goals.Add(new() { Position = new(1, 1, 0), PositionWeight = 1, BendDirection = Vector3.UnitX,
                Orientation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 6), OrientationWeight = 1 });
            goals.Add(new() { Position = new(100, 1, 0), PositionWeight = 1 });
        });
        QueryHandle targets = world.Query(world.QueryDefinition().Read<GpuPoseTarget>());
        using var sources = new DevicePoseSources(backend, device, world, targets, CompileDevicePoseSources(), new(1, 9, 1, 9, 1, 8192, Goals: 2, Operations: 2));

        PhysicsSimulation.Create(world, QueryDefinition.Empty, sources.Evaluate().Then(sources.Readback()), 1).Schedule(0, 0, .1).Complete();

        BoneTransform[] local = skeleton.ReferencePose.ToArray();
        local[1].Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -MathF.PI / 2);
        local[2].Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2);
        local[3].Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 6);
        local[5].Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -MathF.PI / 16);
        local[6].Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -MathF.PI / 8);
        local[7].Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -MathF.PI / 4);
        expected.Update(local, .1);
        for (int bone = 0; bone < skeleton.BoneCount; bone++)
        {
            Matrix4x4 actual = sources.ReadModels(target)[bone], reference = expected.ModelPose.Transforms[bone];
            Assert.InRange(Vector3.Distance(Vector3.Transform(Vector3.UnitX, reference), Vector3.Transform(Vector3.UnitX, actual)), 0, .001f);
            Assert.InRange(Vector3.Distance(Vector3.Transform(Vector3.UnitY, reference), Vector3.Transform(Vector3.UnitY, actual)), 0, .001f);
        }
        world.ReleaseQuery(targets);
    }
}
