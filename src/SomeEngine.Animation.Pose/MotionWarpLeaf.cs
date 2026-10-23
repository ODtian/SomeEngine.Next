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
    internal int Level;
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
        world.ExecuteBufferWrite<MotionWarpInstance, MotionWarpInstance>(animation, ref instance,
            static (instances, ref instance) => instances.Add(instance));
    }

    public static void Complete(World world, Entity animation) =>
        world.ExecuteBufferWrite<MotionWarpInstance>(animation, static instances =>
        {
            Span<MotionWarpInstance> warps = instances.AsSpan();
            foreach (ref MotionWarpInstance warp in warps) warp.Level = -1;
            for (int index = 0; index < warps.Length; index++) AssignLevel(warps, index);
        });

    private static int AssignLevel(Span<MotionWarpInstance> warps, int index)
    {
        if (warps[index].Level >= 0) return warps[index].Level;
        int level = 0;
        for (int producer = 0; producer < warps.Length; producer++)
            if (producer != index && warps[producer].Output == warps[index].Source)
                level = Math.Max(level, AssignLevel(warps, producer) + 1);
        return warps[index].Level = level;
    }

    public static void Release(World world, Entity animation) =>
        world.ExecuteBufferWrite<MotionWarpInstance>(animation, static instances => instances.Clear());
}

public static partial class PoseGraphExtensions
{
    public static void WarpMotion(this AnimationBuilder builder, Entity target, in MotionWarpParameters parameters, byte layer = 0) =>
        builder.Leaf<MotionWarpParameters, MotionWarpLeaf>(target, in parameters, layer);
}
