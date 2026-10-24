using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;

namespace SomeEngine.Animation.Pose;

public readonly record struct PbikParameters(PbikRig Rig, PoseResultId Source, PoseResultId Output);

[BufferCapacity(1)]
public struct PbikInstance : IBufferElement
{
    public AnimationLeafBatch<PbikParameters> Definition;
    public int Index;
    public AnimationLeafPlacement Placement;
    public PoseResultReference Source, Output;
    public PbikTarget[] Targets;
    internal PoseNode Node;
    internal PbikSamples Samples;
    internal PoseResult Basis;
    internal float Weight;
    internal bool Active, Sampled;
}

public readonly struct PbikLeaf : IAnimationLeaf<PbikParameters>
{
    public static MotionBoundary Describe(in PbikParameters parameters) => new(StageEnd.Settled);

    public static void Install(World world, Entity animation, Entity target,
        AnimationLeafBatch<PbikParameters> definitions, int index, in AnimationLeafPlacement placement)
    {
        if (!world.HasBuffer<PbikInstance>(animation)) world.AddBuffer<PbikInstance>(animation);
        PbikParameters parameters = definitions.Parameters[index];
        SkeletonInstance skeleton = world.Read<PoseTarget>(target).Skeleton;
        var instance = new PbikInstance
        {
            Definition = definitions, Index = index, Placement = placement,
            Source = PoseAnimation.Resolve(world, target, parameters.Source),
            Output = PoseAnimation.Resolve(world, target, parameters.Output),
            Targets = new PbikTarget[parameters.Rig.Effectors.Length],
            Samples = new(parameters.Rig, skeleton),
        };
        for (int effector = 0; effector < instance.Targets.Length; effector++)
            instance.Targets[effector] = new(skeleton.ModelPose.GetBone(
                skeleton.Skeleton.InheritedBoneCount + parameters.Rig.Effectors[effector]).Transform.Translation);
        instance.Node = PoseAnimation.RegisterNode(world, animation, instance.Output, [instance.Source], in placement);
        world.ExecuteBufferWrite<PbikInstance, PbikInstance>(animation, ref instance,
            static (instances, ref instance) => instances.Add(instance));
    }

    public static void Complete(World world, Entity animation) => PoseAnimation.CompleteNodes(world, animation);

    public static void Release(World world, Entity animation)
    {
        world.ExecuteBufferWrite<PbikInstance>(animation, static instances => instances.Clear());
        PoseAnimation.ReleaseNodes(world, animation);
    }
}

public static partial class PoseGraphExtensions
{
    public static void SolvePbik(this AnimationBuilder builder, Entity target, in PbikParameters parameters, byte layer = 0) =>
        builder.Leaf<PbikParameters, PbikLeaf>(target, in parameters, layer);
}
