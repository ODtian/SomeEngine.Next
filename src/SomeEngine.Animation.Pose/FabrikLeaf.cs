using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;

namespace SomeEngine.Animation.Pose;

public readonly record struct FabrikParameters(FabrikChain Chain, PoseResultId Source, PoseResultId Output);

[BufferCapacity(1)]
public struct FabrikInstance : IBufferElement
{
    public AnimationLeafBatch<FabrikParameters> Definition;
    public int Index;
    public AnimationLeafPlacement Placement;
    public PoseResultReference Source, Output;
    /// <summary>The requested chain endpoint in model space.</summary>
    public Vector3 Target;
    internal PoseNode Node;
    internal FabrikSamples Samples;
    internal PoseResult Basis;
    internal float Weight;
    internal bool Active, Sampled;
}

public readonly struct FabrikLeaf : IAnimationLeaf<FabrikParameters>
{
    public static MotionBoundary Describe(in FabrikParameters parameters) => new(StageEnd.Settled);

    public static void Install(World world, Entity animation, Entity target,
        AnimationLeafBatch<FabrikParameters> definitions, int index, in AnimationLeafPlacement placement)
    {
        if (!world.HasBuffer<FabrikInstance>(animation)) world.AddBuffer<FabrikInstance>(animation);
        FabrikParameters parameters = definitions.Parameters[index];
        SkeletonInstance skeleton = world.Read<PoseTarget>(target).Skeleton;
        var instance = new FabrikInstance
        {
            Definition = definitions, Index = index, Placement = placement,
            Source = PoseAnimation.Resolve(world, target, parameters.Source),
            Output = PoseAnimation.Resolve(world, target, parameters.Output),
            Target = skeleton.ModelPose.GetBone(skeleton.Skeleton.InheritedBoneCount + parameters.Chain.Bones[^1]).Transform.Translation,
            Samples = new(parameters.Chain, skeleton),
        };
        short[] bones = parameters.Chain.Bones.ToArray().Select(static bone => (short)bone).ToArray();
        instance.Node = PoseAnimation.RegisterNode(world, animation, instance.Output, [instance.Source], in placement, bones);
        world.ExecuteBufferWrite<FabrikInstance, FabrikInstance>(animation, ref instance,
            static (instances, ref instance) => instances.Add(instance));
    }

    public static void Complete(World world, Entity animation) => PoseAnimation.CompleteNodes(world, animation);

    public static void Release(World world, Entity animation)
    {
        world.ExecuteBufferWrite<FabrikInstance>(animation, static instances => instances.Clear());
        PoseAnimation.ReleaseNodes(world, animation);
    }
}

public static partial class PoseGraphExtensions
{
    public static void SolveFabrik(this AnimationBuilder builder, Entity target, in FabrikParameters parameters, byte layer = 0) =>
        builder.Leaf<FabrikParameters, FabrikLeaf>(target, in parameters, layer);
}
