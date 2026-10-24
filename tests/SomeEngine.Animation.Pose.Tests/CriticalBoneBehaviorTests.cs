using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;

namespace SomeEngine.Animation.Pose.Tests;

public sealed class CriticalBoneBehaviorTests
{
    [Fact]
    public void CpuSolverConsumesThisUpdatesInheritedModelResultBeforePublishingItsCriticalBones()
    {
        using var world = new World();
        var parentDefinition = new Skeleton([-1], [At(0, 0, 0)], [1]);
        var childDefinition = new Skeleton(parentDefinition, [-1, 0, 1, 0],
            [At(0, 0, 0), At(1, 0, 0), At(1, 0, 0), At(0, 0, 1)], [2, 3, 4, 5], [0, -1, -1, -1]);
        var storage = new PoseStorage();
        using SkeletonInstance parent = storage.CreateInstance(parentDefinition);
        using SkeletonInstance child = storage.CreateInstance(childDefinition, parent);
        AnimationClip parentClip = Clip(parentDefinition, [new(0, BoneTransformProperty.Translation,
            ClipInterpolation.Linear, [0, 1], [new(0, 0, 0, 0), new(2, 0, 0, 0)])]);
        AnimationClip childClip = Clip(childDefinition, []);
        Entity parentTarget = PoseAnimation.CreateTarget(world, parent, 2, new(1));
        Entity childSource = PoseAnimation.CreateTarget(world, child, 2, new(1));
        Entity childTarget = GpuPoseAnimation.CreateTarget(world, child);
        using var parentBuilder = new AnimationBuilder(world);
        using (parentBuilder.Parallel())
        {
            parentBuilder.BlendPose(parentTarget, new(new PoseBlend([new(new(0))]), new(1), new(StageEnd.Settled)));
            parentBuilder.Leaf<ClipParameters, ClipLeaf>(parentTarget, new(parentClip, new(0), Loop: true));
        }
        parentBuilder.Play();
        using var childBuilder = new AnimationBuilder(world);
        using (childBuilder.Parallel())
        {
            childBuilder.SolveFabrik(childSource, new(new FabrikChain(childDefinition, [0, 1, 2], tolerance: .0001f, iterations: 32), new(0), new(1)));
            childBuilder.Leaf<ClipParameters, ClipLeaf>(childSource, new(childClip, new(0), Loop: true));
            childBuilder.Leaf<GpuClipParameters, GpuClipLeaf>(childTarget, new(QuantizedClipData.Encode(childClip)));
        }
        Entity animation = childBuilder.Play();
        var goal = new Vector3(1.5f, 1, 0);
        world.ExecuteBufferWrite<FabrikInstance, Vector3>(animation, ref goal,
            static (chains, ref goal) => chains[0].Target = goal);
        GpuPoseAnimation.SetCpuBones(world, childTarget, new CpuBoneDemand(PoseAnimation.Resolve(world, childSource, new(1)), [2]));
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        GpuPoseAnimation.AddSystems(systems);
        PoseAnimation.AddSystems(systems, nodes: static nodes =>
        {
            nodes.Add(new FabrikSystem());
            nodes.Add(new PoseBlendSystem());
        });
        var frame = new ImmediateSystemContext(world, 0, 0, .25);

        systems.Update(ref frame);

        Near(goal, child.ModelPose.GetBone(3).Transform.Translation);
        Assert.Equal(.25, child.ModelPose.GetBone(3).Time);
        Assert.Equal(.25, child.ModelPose.GetBone(3).SampleTime);
        Assert.Equal(0, child.ModelPose.GetBone(4).Time);
        goal = new(2, 1, 0);
        world.ExecuteBufferWrite<FabrikInstance, Vector3>(animation, ref goal,
            static (chains, ref goal) => chains[0].Target = goal);
        systems.Update(ref frame);
        Near(goal, child.ModelPose.GetBone(3).Transform.Translation);
        Assert.Equal(.5, parent.ModelPose.GetBone(0).Time);
        Assert.Equal(.5, child.ModelPose.GetBone(3).Time);
        Assert.Equal(0, child.ModelPose.GetBone(4).Time);
    }

    [Fact]
    public void CompletionPublishesDemandedBonesAndCurrentInheritedParentsWithEachBonesOwnTimes()
    {
        using var world = new World();
        var parentDefinition = new Skeleton([-1], [At(0, 0, 0)], [1]);
        var childDefinition = new Skeleton(parentDefinition, [-1, 0, -1],
            [At(0, 1, 0), At(1, 0, 0), At(0, 0, 1)], [2, 3, 4], [0, -1, 0]);
        var storage = new PoseStorage();
        using SkeletonInstance parent = storage.CreateInstance(parentDefinition);
        using SkeletonInstance child = storage.CreateInstance(childDefinition, parent);
        AnimationClip parentClip = Clip(parentDefinition, [new(0, BoneTransformProperty.Translation,
            ClipInterpolation.Linear, [0, 1], [new(0, 0, 0, 0), new(2, 0, 0, 0)])]);
        AnimationClip childClip = Clip(childDefinition,
        [
            new(0, BoneTransformProperty.Translation, ClipInterpolation.Linear, [0, 1], [new(0, 1, 0, 0), new(0, 2, 0, 0)]),
            new(1, BoneTransformProperty.Translation, ClipInterpolation.Linear, [0, 1], [new(1, 0, 0, 0), new(2, 0, 0, 0)]),
            new(2, BoneTransformProperty.Translation, ClipInterpolation.Linear, [0, 1], [new(0, 0, 1, 0), new(0, 0, 2, 0)]),
        ]);
        QuantizedClipData parentData = QuantizedClipData.Encode(parentClip, 4), childData = QuantizedClipData.Encode(childClip, 4);
        Entity parentTarget = GpuPoseAnimation.CreateTarget(world, parent), childTarget = GpuPoseAnimation.CreateTarget(world, child);
        using var builder = new AnimationBuilder(world);
        using (builder.Parallel())
        {
            builder.Leaf<GpuClipParameters, GpuClipLeaf>(childTarget, new(childData));
            builder.Leaf<GpuClipParameters, GpuClipLeaf>(parentTarget, new(parentData));
        }
        builder.Play();
        GpuPoseAnimation.SetCpuBones(world, childTarget, new CpuBoneDemand(childData.CreateClip(childDefinition), [0]));
        var observed = new List<(Vector3 Position, double Time, double SampleTime)>();
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        GpuPoseAnimation.AddSystems(systems);
        AnimationSystems.Add(systems, new ObserveBone(child.ModelPose.GetBone(1), observed), AnimationPhase.Completion);
        var frame = new ImmediateSystemContext(world, 0, 0, .25);

        systems.Update(ref frame);

        Near(new(.5f, 1.25f, 0), observed[0].Position);
        Assert.Equal((.25, .25), (observed[0].Time, observed[0].SampleTime));
        Assert.Equal(.25, parent.ModelPose.GetBone(0).Time);
        Assert.Equal(0, child.ModelPose.GetBone(2).Time);
        ulong untouched = child.ModelPose.GetBone(3).Version;

        GpuPoseAnimation.SetCpuBones(world, childTarget, new CpuBoneDemand(childData.CreateClip(childDefinition), [1]));
        systems.Update(ref frame);

        Near(new(2.5f, 1.5f, 0), child.ModelPose.GetBone(2).Transform.Translation);
        Assert.Equal(.5, child.ModelPose.GetBone(1).Time);
        Assert.Equal(.5, child.ModelPose.GetBone(2).SampleTime);
        Assert.Equal(untouched, child.ModelPose.GetBone(3).Version);
        Assert.Equal(0, child.ModelPose.GetBone(3).Time);

        GpuPoseAnimation.SetCpuBones(world, childTarget, new CpuBoneDemand(childData.CreateClip(childDefinition), [2]));
        systems.Update(ref frame);

        Near(new(1.5f, 0, 1.75f), child.ModelPose.GetBone(3).Transform.Translation);
        Assert.Equal(.75, child.ModelPose.GetBone(3).Time);
        Assert.Equal(.5, child.ModelPose.GetBone(1).Time);
        Assert.Equal(.5, child.ModelPose.GetSection(1).Calculations[0].Time);
    }

    private static AnimationClip Clip(Skeleton skeleton, RawClipChannel[] curves) =>
        new(skeleton, 1, ClipSampler.Create<RawClipData, RawClipSampler>(new RawClipData(1, [.. curves])));
    private static BoneTransform At(float x, float y, float z) => new()
    {
        Translation = new(x, y, z), Rotation = Quaternion.Identity, Scale = Vector3.One,
    };
    private static void Near(Vector3 expected, Vector3 actual) => Assert.InRange(Vector3.Distance(expected, actual), 0, .001f);
    private sealed class ObserveBone(BonePose bone, List<(Vector3, double, double)> observations) : ISystem<ImmediateSystemContext>
    {
        public void OnUpdate(ref ImmediateSystemContext context) => observations.Add((bone.Transform.Translation, bone.Time, bone.SampleTime));
    }
}
