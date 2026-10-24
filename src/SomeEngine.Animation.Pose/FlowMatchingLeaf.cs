using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;

namespace SomeEngine.Animation.Pose;

public readonly record struct FlowMatchingParameters(FlowMatchingModel Model, PoseResultId Output,
    byte ControlParameter = 0, int NoiseParameter = -1, uint Seed = 1,
    RootMotionMode RootMotion = RootMotionMode.SourcePose, ReadOnlyMemory<float> InitialHistory = default);

[BufferCapacity(1)]
public struct FlowMatchingInstance : IBufferElement
{
    public AnimationLeafBatch<FlowMatchingParameters> Definition;
    public int Index;
    public AnimationLeafPlacement Placement;
    public PoseResultReference Output;
    public FlowMatchingState State;
    internal PoseNode Node;
    internal uint Starts;
    internal bool Active, Sampled;
    internal float Weight;
    internal double Time;
}

public readonly struct FlowMatchingLeaf : IAnimationLeaf<FlowMatchingParameters>
{
    public static MotionBoundary Describe(in FlowMatchingParameters parameters) => new(StageEnd.Settled);

    public static void Install(World world, Entity animation, Entity target,
        AnimationLeafBatch<FlowMatchingParameters> definitions, int index, in AnimationLeafPlacement placement)
    {
        if (!world.HasBuffer<FlowMatchingInstance>(animation)) world.AddBuffer<FlowMatchingInstance>(animation);
        if (!world.Has<GraphParameters>(animation)) world.Add(animation, new GraphParameters());
        FlowMatchingParameters parameters = definitions.Parameters[index];
        PoseResultReference output = PoseAnimation.Resolve(world, target, parameters.Output);
        var instance = new FlowMatchingInstance
        {
            Definition = definitions, Index = index, Placement = placement, Output = output,
            State = new(parameters.Model, world.Read<PoseTarget>(target).Skeleton.Skeleton),
            Node = PoseAnimation.RegisterNode(world, animation, output, [], in placement),
        };
        world.ExecuteBufferWrite<FlowMatchingInstance, FlowMatchingInstance>(animation, ref instance,
            static (instances, ref instance) => instances.Add(instance));
        FlowMatchingGroups.For(world).Add(animation, instance.State);
    }

    public static void Complete(World world, Entity animation) => PoseAnimation.CompleteNodes(world, animation);

    public static void Release(World world, Entity animation)
    {
        FlowMatchingGroups.For(world).Remove(animation);
        world.ExecuteBufferWrite<FlowMatchingInstance>(animation, static instances => instances.Clear());
        PoseAnimation.ReleaseNodes(world, animation);
    }
}

public static partial class PoseGraphExtensions
{
    public static void GenerateFlowPose(this AnimationBuilder builder, Entity target,
        in FlowMatchingParameters parameters, byte layer = 0) =>
        builder.Leaf<FlowMatchingParameters, FlowMatchingLeaf>(target, in parameters, layer);
}
