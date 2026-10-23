using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.Job;

namespace SomeEngine.Animation.Pose;

/// <summary>A masked local-pose layer using its placement's common AnimationLayer mode and weight.</summary>
public readonly record struct PoseLayerParameters(PoseResultId Base, PoseResultId Source, PoseResultId Output, BoneMask Mask);

[BufferCapacity(1)]
public struct PoseLayerInstance : IBufferElement
{
    public AnimationLeafBatch<PoseLayerParameters> Definition;
    public int Index;
    public AnimationLeafPlacement Placement;
    public PoseResultReference Base, Source, Output;
    internal PoseNode Node;
    internal PoseLayerSamples Samples;
    internal PoseResult Basis;
    internal float Weight;
    internal bool Active, Sampled;
}

internal sealed class PoseLayerSamples
{
    internal readonly BoneTransform[] Base, Source, Output;
    internal readonly Skeleton Skeleton;
    internal readonly BoneMask Mask;
    internal readonly JobResourceAccess[] Accesses;

    internal PoseLayerSamples(Skeleton skeleton, BoneMask mask)
    {
        Skeleton = skeleton;
        Mask = mask;
        Base = new BoneTransform[skeleton.BoneCount];
        Source = new BoneTransform[skeleton.BoneCount];
        Output = new BoneTransform[skeleton.BoneCount];
        Accesses = [JobResourceAccess.Read(Base), JobResourceAccess.Read(Source), JobResourceAccess.Write(Output),
            JobResourceAccess.Read(JobSystem.Default.GetResourceToken(skeleton)),
            JobResourceAccess.Read(JobSystem.Default.GetResourceToken(mask))];
    }
}

public readonly struct PoseLayerLeaf : IAnimationLeaf<PoseLayerParameters>
{
    public static MotionBoundary Describe(in PoseLayerParameters parameters) => new(StageEnd.Settled);

    public static void Install(World world, Entity animation, Entity target,
        AnimationLeafBatch<PoseLayerParameters> definitions, int index, in AnimationLeafPlacement placement)
    {
        if (!world.HasBuffer<PoseLayerInstance>(animation)) world.AddBuffer<PoseLayerInstance>(animation);
        PoseLayerParameters parameters = definitions.Parameters[index];
        var instance = new PoseLayerInstance
        {
            Definition = definitions, Index = index, Placement = placement,
            Base = PoseAnimation.Resolve(world, target, parameters.Base),
            Source = PoseAnimation.Resolve(world, target, parameters.Source),
            Output = PoseAnimation.Resolve(world, target, parameters.Output),
            Samples = new(world.Read<PoseTarget>(target).Skeleton.Skeleton, parameters.Mask),
        };
        instance.Node = PoseAnimation.RegisterNode(world, animation, instance.Output, [instance.Base, instance.Source], in placement);
        world.ExecuteBufferWrite<PoseLayerInstance, PoseLayerInstance>(animation, ref instance,
            static (instances, ref instance) => instances.Add(instance));
    }

    public static void Complete(World world, Entity animation) => PoseAnimation.CompleteNodes(world, animation);

    public static void Release(World world, Entity animation)
    {
        world.ExecuteBufferWrite<PoseLayerInstance>(animation, static instances => instances.Clear());
        PoseAnimation.ReleaseNodes(world, animation);
    }
}

public static partial class PoseGraphExtensions
{
    public static void LayerPose(this AnimationBuilder builder, Entity target, in PoseLayerParameters parameters, byte layer = 0) =>
        builder.Leaf<PoseLayerParameters, PoseLayerLeaf>(target, in parameters, layer);
}
