using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;

namespace SomeEngine.Animation.Pose;

public readonly record struct EwbikParameters(EwbikRig Rig, PoseResultId Source, PoseResultId Output);

[BufferCapacity(1)]
public struct EwbikInstance : IBufferElement
{
    public AnimationLeafBatch<EwbikParameters> Definition;
    public int Index;
    public AnimationLeafPlacement Placement;
    public PoseResultReference Source, Output;
    public EwbikTarget[] Targets;
    public EwbikSettings Settings;
    public float[] PreferenceWeights;
    internal PoseNode Node;
    internal EwbikSamples Samples;
    internal PoseResult Basis;
    internal float Weight;
    internal bool Active, Sampled;
}

public readonly struct EwbikLeaf : IAnimationLeaf<EwbikParameters>
{
    public static MotionBoundary Describe(in EwbikParameters parameters) => new(StageEnd.Settled);

    public static void Install(World world, Entity animation, Entity target,
        AnimationLeafBatch<EwbikParameters> definitions, int index, in AnimationLeafPlacement placement)
    {
        if (!world.HasBuffer<EwbikInstance>(animation)) world.AddBuffer<EwbikInstance>(animation);
        EwbikParameters parameters = definitions.Parameters[index];
        SkeletonInstance skeleton = world.Read<PoseTarget>(target).Skeleton;
        var instance = new EwbikInstance
        {
            Definition = definitions, Index = index, Placement = placement,
            Source = PoseAnimation.Resolve(world, target, parameters.Source),
            Output = PoseAnimation.Resolve(world, target, parameters.Output),
            Targets = new EwbikTarget[parameters.Rig.Effectors.Length],
            Samples = new(PoseAnimation.GetScheduler(world), parameters.Rig, skeleton), Settings = parameters.Rig.Settings,
            PreferenceWeights = new float[parameters.Rig.Constraints.Length],
        };
        for (int constraint = 0; constraint < instance.PreferenceWeights.Length; constraint++)
            instance.PreferenceWeights[constraint] = parameters.Rig.Constraints[constraint].PreferenceWeight;
        for (int effector = 0; effector < instance.Targets.Length; effector++)
        {
            Matrix4x4 model = skeleton.ModelPose.GetBone(
                skeleton.Skeleton.InheritedBoneCount + parameters.Rig.Effectors[effector].Bone).Transform;
            Matrix4x4.Decompose(model, out _, out Quaternion rotation, out Vector3 position);
            instance.Targets[effector] = new(position, rotation, AxisPriorities: Vector3.One);
        }
        short[] bones = parameters.Rig.Bones;
        instance.Node = PoseAnimation.RegisterNode(world, animation, instance.Output, [instance.Source], in placement, bones);
        world.ExecuteBufferWrite<EwbikInstance, EwbikInstance>(animation, ref instance,
            static (instances, ref instance) => instances.Add(instance));
    }

    public static void Complete(World world, Entity animation) => PoseAnimation.CompleteNodes(world, animation);

    public static void Release(World world, Entity animation)
    {
        world.ExecuteBufferWrite<EwbikInstance>(animation, static instances => instances.Clear());
        PoseAnimation.ReleaseNodes(world, animation);
    }
}

public static partial class PoseGraphExtensions
{
    public static void SolveEwbik(this AnimationBuilder builder, Entity target, in EwbikParameters parameters, byte layer = 0) =>
        builder.Leaf<EwbikParameters, EwbikLeaf>(target, in parameters, layer);
}
