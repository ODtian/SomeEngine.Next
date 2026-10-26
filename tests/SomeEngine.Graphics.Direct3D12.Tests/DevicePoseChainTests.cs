using System.Numerics;
using SlangShaderSharp;
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
    public void GameSecondaryChainMovesItsOriginalBodiesAndMapsTheCompletedBone()
    {
        using var backend = D3D12GraphicsBackend.Create();
        using Device device = CreateDevice(backend);
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
        var stages = physics.Upload().Then(sources.Evaluate()).Then(sources.Readback()).Then(results.Readback(64));
        var simulation = PhysicsSimulation.Create(world, QueryDefinition.Empty, stages, 1);
        simulation.Schedule(0, 0, .1).Complete();
        input.InputNumber = 2; input.CalculationTime = .2; world.Replace(target, input);

        simulation.Schedule(1, .1, .1).Complete();

        // One .1-second gravity prediction followed by the unit-distance constraint gives this worked result.
        Vector3 expected = new(.99820485f, -.05989229f, 0);
        Assert.InRange(Vector3.Distance(expected, world.Read<BodyPose>(moving).Position), 0, .001f);
        Assert.InRange(Vector3.Distance(new(-.0179515f, -.5989229f, 0), world.Read<BodyVelocity>(moving).Linear), 0, .001f);
        Assert.InRange(Vector3.Distance(expected, sources.ReadModels(target)[1].Translation), 0, .001f);
        Assert.Equal(1, world.Read<BodyStepState>(moving).Number);
        Assert.Equal(.2, world.Read<BodyStepState>(moving).Time, 8);
        world.ReleaseQuery(members); world.ReleaseQuery(targets);
    }

    private DevicePoseKernels CompileDevicePosePhysics()
    {
        D3D12TestShader shader = shaderCompiler.CompileWithSearchPaths("game_pose_physics", "#include \"gpu_pose_physics.slang\"",
            [new("AdvancePoseStates", SlangStage.Compute), new("SamplePoseLevel", SlangStage.Compute), new("TranslatePose", SlangStage.Compute),
             new("SolveTwoBone", SlangStage.Compute), new("SolveAim", SlangStage.Compute), new("WarpStride", SlangStage.Compute),
             new("SimulateDevicePoseChain", SlangStage.Compute)], "sm_6_0", ShaderDirectory());
        ComputePipelineDesc Entry(int index) => new(shader.SlangCode, shader.Reflection, shader.GetEntryPoint(index), shader.GetEntryCode(index));
        return new(Entry(0), Entry(1), Entry(2), Entry(3), Entry(4), Entry(5), Entry(6));
    }
}
