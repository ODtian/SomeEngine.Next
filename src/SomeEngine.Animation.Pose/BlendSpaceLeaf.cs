using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.Job;

namespace SomeEngine.Animation.Pose;

public readonly record struct BlendSpaceParameters(BlendSpace Space, PoseResultId Output, byte ParameterX, byte ParameterY = 0);

[BufferCapacity(1)]
public struct BlendSpaceInstance : IBufferElement
{
    public AnimationLeafBatch<BlendSpaceParameters> Definition;
    public int Index;
    public AnimationLeafPlacement Placement;
    public PoseResultReference Output;
    internal BlendSpaceSamples Samples;
    internal uint Starts;
    internal bool Active;
    internal float Weight;
    internal double Time;
}

internal sealed class BlendSpaceSamples
{
    internal readonly float[] Weights;
    internal readonly BoneTransform[] Inputs, Output;
    internal readonly JobResourceAccess[] Accesses;
    internal double Phase;

    internal BlendSpaceSamples(Scheduler scheduler, BlendSpace space, int bones)
    {
        Weights = new float[space.Samples.Length];
        Inputs = new BoneTransform[bones * space.Samples.Length];
        Output = new BoneTransform[bones];
        Accesses = new JobResourceAccess[space.Samples.Length + 3];
        Accesses[0] = JobResourceAccess.Read(scheduler, Weights);
        Accesses[1] = JobResourceAccess.Write(scheduler, Inputs);
        Accesses[2] = JobResourceAccess.Write(scheduler, Output);
        for (int sample = 0; sample < space.Samples.Length; sample++)
            Accesses[sample + 3] = JobResourceAccess.Read(scheduler.GetResourceToken(space.Samples[sample].Clip));
    }
}

public readonly struct BlendSpaceLeaf : IAnimationLeaf<BlendSpaceParameters>
{
    public static MotionBoundary Describe(in BlendSpaceParameters parameters) => new(StageEnd.Settled);

    public static void Install(World world, Entity animation, Entity target,
        AnimationLeafBatch<BlendSpaceParameters> definitions, int index, in AnimationLeafPlacement placement)
    {
        if (!world.HasBuffer<BlendSpaceInstance>(animation)) world.AddBuffer<BlendSpaceInstance>(animation);
        if (!world.Has<GraphParameters>(animation)) world.Add(animation, new GraphParameters());
        BlendSpaceParameters parameters = definitions.Parameters[index];
        PoseResultReference output = PoseAnimation.Resolve(world, target, parameters.Output);
        var instance = new BlendSpaceInstance
        {
            Definition = definitions, Index = index, Placement = placement, Output = output,
            Samples = new BlendSpaceSamples(PoseAnimation.GetScheduler(world), parameters.Space, output.Range.Count),
        };
        world.ExecuteBufferWrite<BlendSpaceInstance, BlendSpaceInstance>(animation, ref instance,
            static (instances, ref instance) => instances.Add(instance));
    }

    public static void Release(World world, Entity animation) =>
        world.ExecuteBufferWrite<BlendSpaceInstance>(animation, static instances => instances.Clear());
}

public static partial class PoseGraphExtensions
{
    public static void BlendPose1D(this AnimationBuilder builder, Entity target, BlendSpace space, PoseResultId output,
        byte parameter, byte layer = 0) =>
        builder.Leaf<BlendSpaceParameters, BlendSpaceLeaf>(target, new(space, output, parameter), layer);

    public static void BlendPose2D(this AnimationBuilder builder, Entity target, BlendSpace space, PoseResultId output,
        byte parameterX, byte parameterY, byte layer = 0) =>
        builder.Leaf<BlendSpaceParameters, BlendSpaceLeaf>(target, new(space, output, parameterX, parameterY), layer);
}
