using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;

namespace SomeEngine.Animation.Pose;

public readonly record struct MotionWarpParameters(RootMotionWarp Warp, PoseResultId Source, PoseResultId Output);

[BufferCapacity(1)]
public struct MotionWarpInstance : IBufferElement
{
    public AnimationLeafBatch<MotionWarpParameters> Definition;
    public int Index;
    public AnimationLeafPlacement Placement;
    public PoseResultReference Source;
    public PoseResultReference Output;
    public MotionWarpInput Input;
    internal Matrix4x4 PreviousMotion;
    internal uint Starts, SourceStarts;
    internal ulong SourceVersion;
    internal PoseNode Node;
    internal bool Finished;
}

public readonly struct MotionWarpLeaf : IAnimationLeaf<MotionWarpParameters>
{
    public static MotionBoundary Describe(in MotionWarpParameters parameters) => new(StageEnd.Duration, parameters.Warp.Source.Duration);

    public static void Install(World world, Entity animation, Entity target,
        AnimationLeafBatch<MotionWarpParameters> definitions, int index, in AnimationLeafPlacement placement)
    {
        if (!world.HasBuffer<MotionWarpInstance>(animation)) world.AddBuffer<MotionWarpInstance>(animation);
        MotionWarpParameters parameters = definitions.Parameters[index];
        var instance = new MotionWarpInstance
        {
            Definition = definitions, Index = index, Placement = placement,
            Source = PoseAnimation.Resolve(world, target, parameters.Source),
            Output = PoseAnimation.Resolve(world, target, parameters.Output),
            Input = parameters.Warp.Bind(Matrix4x4.Identity, parameters.Warp.Source.Sample(parameters.Warp.Window.End)),
            PreviousMotion = Matrix4x4.Identity,
        };
        instance.Node = PoseAnimation.RegisterNode(world, animation, instance.Output, [instance.Source], in placement);
        world.ExecuteBufferWrite<MotionWarpInstance, MotionWarpInstance>(animation, ref instance,
            static (instances, ref instance) => instances.Add(instance));
    }

    public static void Complete(World world, Entity animation) =>
        PoseAnimation.CompleteNodes(world, animation);

    public static void Release(World world, Entity animation)
    {
        world.ExecuteBufferWrite<MotionWarpInstance>(animation, static instances => instances.Clear());
        PoseAnimation.ReleaseNodes(world, animation);
    }
}

public static partial class PoseGraphExtensions
{
    public static void WarpMotion(this AnimationBuilder builder, Entity target, in MotionWarpParameters parameters, byte layer = 0) =>
        builder.Leaf<MotionWarpParameters, MotionWarpLeaf>(target, in parameters, layer);
}
