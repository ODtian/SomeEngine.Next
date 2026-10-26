using System.Numerics;
using SlangShaderSharp;
using SomeEngine.Animation;
using SomeEngine.Animation.Pose;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;
using SomeEngine.Physics;
using Xunit;

namespace SomeEngine.Graphics.Direct3D12.Tests;

public sealed partial class DeviceXpbdTests
{
    [Fact]
    public void GameMaskedBlendFeedsDeviceBodiesAndTheCompletedPhysicsBone()
    {
        using var backend = D3D12GraphicsBackend.Create();
        using Device device = CreateDevice(backend);
        using var jobs = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using World world = WorldJobs.CreateWorld(scheduler: jobs);
        BoneTransform Bone(float x) => new() { Translation = new(x, 0, 0), Rotation = Quaternion.Identity, Scale = Vector3.One };
        var skeleton = new Skeleton([-1, -1], [Bone(0), Bone(2)], [1, 2]);
        using SkeletonInstance sourceInstance = new PoseStorage().CreateInstance(skeleton);
        using SkeletonInstance outputInstance = new PoseStorage().CreateInstance(skeleton);
        var basis = QuantizedClipData.Encode(new AnimationClip(skeleton, 1,
            ClipSampler.Create<BoneTransform[], FixedDevicePoseSampler>([Bone(0), Bone(2)])));
        var overlay = QuantizedClipData.Encode(new AnimationClip(skeleton, 1,
            ClipSampler.Create<BoneTransform[], FixedDevicePoseSampler>([Bone(8), Bone(4)])));
        var blend = new GpuPoseBlend(skeleton, new GpuBlendLayer(new GpuBlendClip(basis), new GpuBlendClip(overlay),
            new BoneMask([0, 1]), WeightParameter: 0));
        Entity source = GpuPoseAnimation.CreateTarget(world, sourceInstance);
        GpuPoseTarget sourceInput = world.Read<GpuPoseTarget>(source);
        sourceInput.Blend = blend; sourceInput.Controls = [.5f]; sourceInput.Playing = true;
        sourceInput.InputNumber = 1; sourceInput.CalculationTime = .1; sourceInput.PlaybackTime = .1f;
        world.Replace(source, sourceInput);
        QueryHandle sources = world.Query(world.QueryDefinition().Read<GpuPoseTarget>());
        using var sampled = new DevicePoseSources(backend, device, world, sources, CompileDevicePoseSources(),
            new(1, 2, 3, 6, 1, 4096));
        using var bodies = new DeviceBodies(backend, device, 2);
        Entity anchor = AddBody(world, bodies, 0, Vector3.Zero, BodyMotion.Static);
        Entity moving = AddBody(world, bodies, 1, Vector3.Zero, BodyMotion.Dynamic);
        using var lengths = new DeviceConstraintData<DeviceLengthConstraint>(backend, device, 1, "DeviceLengths");
        using var multipliers = new DeviceConstraintState<float>(backend, device, 1, "DeviceLengthMultipliers");
        RelationEdge<XpbdLength> edge = world.CreateRelation(anchor, moving, new XpbdLength { RestLength = 1 });
        world.Add(edge.Entity, new DeviceConstraintBinding<DeviceLengthConstraint>(lengths, 0));
        world.Add(edge.Entity, new DeviceXpbdState<float>(multipliers));
        QueryHandle members = world.Query(world.QueryDefinition().Read<DeviceBodyBinding>());
        QueryHandle rows = world.Query(world.QueryDefinition().Read<XpbdLength>().Read<UndirectedRelationEndpoints<XpbdLength>>()
            .Read<DeviceConstraintBinding<DeviceLengthConstraint>>().All<DeviceXpbdState<float>>());
        QueryHandle poses = world.Query(world.QueryDefinition().ReadWrite<BodyPose>());
        QueryHandle poseReads = world.Query(world.QueryDefinition().Read<BodyPose>());
        using var writeLengths = lengths.WriteInputs<XpbdLength, DeviceLengthSource>(world, rows, new(), 64);
        using var dynamics = new DeviceBodyDynamics(backend, device, world, members, bodies, CompileBodyDynamics(), 64);
        using var results = new DeviceBodyResults(backend, device, bodies, world, members, 2);
        using var projection = new DeviceXpbdProjection<DeviceLengthConstraint, float>(backend, device, world,
            rows, bodies, dynamics, lengths, multipliers, CompileLength(), 64);
        Entity target = PoseAnimation.CreateTarget(world, outputInstance, resultCount: 2, output: new(1));
        Entity animation;
        using (var builder = new AnimationBuilder(world))
        {
            using (builder.Parallel())
            {
                builder.BlendPhysicsPose(target, new(new(0), new(1),
                    new PhysicsPoseMap(skeleton, [new(1, Matrix4x4.Identity)]), new BoneMask([0, 1])));
                builder.Leaf<ClipParameters, ClipLeaf>(target, new(basis.CreateClip(skeleton), new(0), Loop: true));
            }
            animation = builder.Play();
        }
        ReadPhysicsPose consume = default;
        var mapped = (Body: moving, Poses: poseReads, Consume: consume);
        world.ExecuteBufferWrite<PhysicsPoseInstance, (Entity Body, QueryHandle Poses, ReadPhysicsPose Consume)>(animation, ref mapped,
            static (instances, ref value) => value.Consume = instances[0].BindBodies(value.Poses, [value.Body]));
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        PoseAnimation.AddSystems(systems, nodes: static nodes => nodes.Add(new PhysicsPoseSystem()));
        var stages = sampled.Evaluate().Then(sampled.Readback()).Then(new CopySourceBody(world, sampled, source, moving, poses))
            .Then(lengths.BeginInputs()).Then(writeLengths).Then(lengths.UploadInputs())
            .Then(bodies.WriteInputs(64)).Then(bodies.UploadInputs()).Then(dynamics.Predict(Vector3.Zero))
            .Then(projection.Initialize()).Then(projection.Iterate(1)).Then(dynamics.UpdateVelocities())
            .Then(results.Readback(64)).Then(mapped.Consume);

        PhysicsStepResult completed = PhysicsSimulation.Create(world, QueryDefinition.Empty, stages, 1).Schedule(0, 0, .1);
        completed.Complete();
        var frame = new ImmediateSystemContext(world, 0, 0, .1);
        systems.Update(ref frame);

        Near(new(3, 0, 0), sampled.ReadModels(source)[1].Translation);
        Near(new(1, 0, 0), world.Read<BodyPose>(moving).Position);
        Near(new(1, 0, 0), outputInstance.ModelPose.Transforms[1].Translation);
        Assert.Equal(.1, world.Read<BodyStepState>(moving).Time, 8);
        world.ReleaseQuery(poseReads); world.ReleaseQuery(poses); world.ReleaseQuery(rows); world.ReleaseQuery(members); world.ReleaseQuery(sources);
    }

    private DevicePoseKernels CompileDevicePoseSources()
    {
        D3D12TestShader shader = shaderCompiler.CompileWithSearchPaths("game_pose_source", "#include \"gpu_pose_sampling.slang\"",
            [new("AdvancePoseStates", SlangStage.Compute), new("SamplePoseLevel", SlangStage.Compute)], "sm_6_0", ShaderDirectory());
        ComputePipelineDesc Entry(int index) => new(shader.SlangCode, shader.Reflection, shader.GetEntryPoint(index), shader.GetEntryCode(index));
        return new(Entry(0), Entry(1));
    }
    private readonly struct FixedDevicePoseSampler : IClipSampler<BoneTransform[]>
    {
        public static void SampleLocal(BoneTransform[] data, float time, Span<BoneTransform> destination) => data.CopyTo(destination);
    }
    private readonly struct CopySourceBody(World world, DevicePoseSources sources, Entity source, Entity body, QueryHandle poses) : IPhysicsStage
    {
        public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency)
        {
            Scheduler jobs = WorldJobs.GetScheduler(world);
            QueryDefinition definition = world.GetQueryDefinition(poses);
            JobResourceAccess[] sourceReads = sources.GetResultReadAccesses(jobs);
            var accesses = new JobResourceAccess[QueryJobAccess.Count(definition) + sourceReads.Length];
            int count = QueryJobAccess.CopyAccesses(world, definition, accesses);
            sourceReads.CopyTo(accesses, count);
            return jobs.Schedule(new CopySourceBodyJob(world, sources, source, body, poses), accesses, dependency);
        }
    }
    private readonly struct CopySourceBodyJob(World world, DevicePoseSources sources, Entity source, Entity body, QueryHandle poses) : IJob
    {
        public void Execute()
        {
            var value = (Body: body, Position: sources.ReadModels(source)[1].Translation);
            world.ExecuteQuery(poses, ref value, static (QueryCursor rows, ref (Entity Body, Vector3 Position) value) =>
            {
                rows.TryGetRow(value.Body, out QueryRow row);
                row.Write<BodyPose>() = new() { Position = value.Position, Rotation = Quaternion.Identity };
            });
        }
    }
}
