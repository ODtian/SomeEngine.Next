using System.Numerics;
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
    public void CompletedDeviceLengthsAndBendsProduceTheMappedSkeletonBones()
    {
        using var backend = D3D12GraphicsBackend.Create();
        using Device device = CreateDevice(backend);
        using var jobs = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using World world = WorldJobs.CreateWorld(scheduler: jobs);
        using var bodies = new DeviceBodies(backend, device, 5);
        using var lengths = new DeviceConstraintData<DeviceLengthConstraint>(backend, device, 1, "DeviceLengths");
        using var lengthMultipliers = new DeviceConstraintState<float>(backend, device, 1, "DeviceLengthMultipliers");
        using var bends = new DeviceConstraintData<DeviceBendConstraint>(backend, device, 1, "DeviceBends");
        using var bendMultipliers = new DeviceConstraintState<float>(backend, device, 1, "DeviceBendMultipliers");
        Entity anchor = AddBody(world, bodies, 0, Vector3.Zero, BodyMotion.Static);
        Entity lengthBody = AddBody(world, bodies, 1, new(2, 0, 0), BodyMotion.Dynamic);
        Entity first = AddBody(world, bodies, 2, Vector3.Zero, BodyMotion.Dynamic);
        Entity middle = AddBody(world, bodies, 3, Vector3.UnitX, BodyMotion.Dynamic);
        Entity last = AddBody(world, bodies, 4, new(1, 1, 0), BodyMotion.Dynamic);
        RelationEdge<XpbdLength> edge = world.CreateRelation(anchor, lengthBody,
            new XpbdLength { RestLength = 1, Compliance = .01f });
        world.Add(edge.Entity, new DeviceConstraintBinding<DeviceLengthConstraint>(lengths, 0));
        world.Add(edge.Entity, new DeviceXpbdState<float>(lengthMultipliers));
        Entity bend = ConstraintBodies.Create(world, new XpbdBend { MinimumCosine = .5f }, [first, middle, last]);
        world.Add(bend, new DeviceConstraintBinding<DeviceBendConstraint>(bends, 0));
        world.Add(bend, new DeviceXpbdState<float>(bendMultipliers));
        QueryHandle members = world.Query(world.QueryDefinition().Read<DeviceBodyBinding>());
        QueryHandle lengthRows = world.Query(world.QueryDefinition().Read<XpbdLength>()
            .Read<UndirectedRelationEndpoints<XpbdLength>>().Read<DeviceConstraintBinding<DeviceLengthConstraint>>()
            .All<DeviceXpbdState<float>>());
        QueryHandle bendRows = world.Query(world.QueryDefinition().Read<XpbdBend>().ReadBuffer<ConstraintBodyLink>()
            .Optional<DirectedRelationEndpoints<ConstraintParticipant>>(QueryAccess.Read)
            .Read<DeviceConstraintBinding<DeviceBendConstraint>>().All<DeviceXpbdState<float>>());
        using var writeLengths = lengths.WriteInputs<XpbdLength, DeviceLengthSource>(world, lengthRows, new(), 64);
        using var writeBends = bends.WriteInputs<XpbdBend, DeviceBendSource>(world, bendRows, new(), 64);
        using var dynamics = new DeviceBodyDynamics(backend, device, world, members, bodies, CompileBodyDynamics(), 64);
        using var results = new DeviceBodyResults(backend, device, bodies);
        using var projectLengths = new DeviceXpbdProjection<DeviceLengthConstraint, float>(backend, device, world,
            lengthRows, bodies, lengths, lengthMultipliers, CompileLength(), 64);
        using var projectBends = new DeviceXpbdProjection<DeviceBendConstraint, float>(backend, device, world,
            bendRows, bodies, bends, bendMultipliers, CompileBend(), 64);

        BoneTransform Bone(Vector3 position) => new() { Translation = position, Rotation = Quaternion.Identity, Scale = Vector3.One };
        var skeleton = new Skeleton([-1, -1], [Bone(new(2, 0, 0)), Bone(Vector3.UnitX)], [1, 2]);
        using SkeletonInstance instance = new PoseStorage().CreateInstance(skeleton);
        Entity target = PoseAnimation.CreateTarget(world, instance, resultCount: 2, output: new(1));
        var clip = new AnimationClip(skeleton, 1, ClipSampler.Create<Skeleton, ReferencePoseSampler>(skeleton));
        Entity animation;
        using (var builder = new AnimationBuilder(world))
        {
            using (builder.Parallel())
            {
                builder.BlendPhysicsPose(target, new(new(0), new(1),
                    new PhysicsPoseMap(skeleton, [new(0, Matrix4x4.Identity), new(1, Matrix4x4.Identity)]), new BoneMask([1, 1])));
                builder.Leaf<ClipParameters, ClipLeaf>(target, new(clip, new(0), Loop: true));
            }
            animation = builder.Play();
        }
        QueryHandle poses = world.Query(world.QueryDefinition().Read<BodyPose>());
        var binding = (First: lengthBody, Second: middle, Query: poses, Read: default(ReadPhysicsPose));
        world.ExecuteBufferWrite<PhysicsPoseInstance, (Entity First, Entity Second, QueryHandle Query, ReadPhysicsPose Read)>(
            animation, ref binding, static (instances, ref binding) =>
                binding.Read = instances[0].BindBodies(binding.Query, [binding.First, binding.Second]));
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        PoseAnimation.AddSystems(systems, nodes: static nodes => nodes.Add(new PhysicsPoseSystem()));
        var stages = lengths.BeginInputs().Then(writeLengths).Then(lengths.UploadInputs())
            .Then(bends.BeginInputs()).Then(writeBends).Then(bends.UploadInputs())
            .Then(bodies.WriteInputs(64)).Then(bodies.UploadInputs()).Then(dynamics.Predict(Vector3.Zero))
            .Then(projectLengths.Initialize()).Then(projectBends.Initialize())
            .Then(projectLengths.Iterate(2)).Then(projectBends.Iterate(1)).Then(dynamics.UpdateVelocities())
            .Then(results.Readback(64)).Then(binding.Read);

        PhysicsSimulation.Create(world, world.QueryDefinition().All<DeviceBodyBinding>().Build(), stages, 1)
            .Schedule(3, 1, .1).Complete();
        var frame = new ImmediateSystemContext(world, 0, 0, .1);
        systems.Update(ref frame);

        Near(new(1.5f, 0, 0), instance.LocalPose[0].Translation);
        Near(new(.875f, .125f, 0), instance.LocalPose[1].Translation);
        Near(new(1.5f, 0, 0), instance.ModelPose.Transforms[0].Translation);
        Near(new(.875f, .125f, 0), instance.ModelPose.Transforms[1].Translation);
        world.ReleaseQuery(poses);
        world.ReleaseQuery(bendRows);
        world.ReleaseQuery(lengthRows);
        world.ReleaseQuery(members);
    }

    private readonly struct ReferencePoseSampler : IClipSampler<Skeleton>
    {
        public static void SampleLocal(Skeleton skeleton, float time, Span<BoneTransform> destination) =>
            skeleton.ReferencePose.CopyTo(destination);
    }
}
