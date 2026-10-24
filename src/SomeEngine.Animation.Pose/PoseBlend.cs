using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.Job;

namespace SomeEngine.Animation.Pose;

/// <summary>A pose source, its weight multiplier and an optional GraphParameters index.</summary>
public readonly record struct PoseBlendInput(PoseResultId Result, float Weight = 1, int Parameter = -1);

/// <summary>Shared source numbers and weights for a concrete pose blend.</summary>
public sealed class PoseBlend(ReadOnlySpan<PoseBlendInput> inputs)
{
    private readonly PoseBlendInput[] _inputs = inputs.ToArray();
    public ReadOnlySpan<PoseBlendInput> Inputs => _inputs;
}

public readonly record struct PoseBlendParameters(PoseBlend Blend, PoseResultId Output, MotionBoundary Boundary);

[BufferCapacity(1)]
public struct PoseBlendInstance : IBufferElement
{
    public AnimationLeafBatch<PoseBlendParameters> Definition;
    public int Index;
    public AnimationLeafPlacement Placement;
    public PoseResultReference Output;
    internal PoseResultReference[] Inputs;
    internal PoseNode Node;
    internal PoseBlendSamples Samples;
    internal uint Starts;
    internal bool Active, Sampled, Finished, Running;
    internal float Weight;
    internal double Time, Phase;
}

internal sealed class PoseBlendSamples
{
    internal readonly int Stride;
    internal readonly BoneTransform[] Inputs, Output;
    internal readonly float[] Weights;
    internal readonly JobResourceAccess[] Accesses;
    internal PoseBlendSamples(int bones, int inputs)
    {
        Stride = bones + 1;
        Inputs = new BoneTransform[Stride * inputs];
        Output = new BoneTransform[Stride];
        Weights = new float[inputs];
        Accesses = [JobResourceAccess.Read(Inputs), JobResourceAccess.Read(Weights), JobResourceAccess.Write(Output)];
    }
}

public readonly struct PoseBlendLeaf : IAnimationLeaf<PoseBlendParameters>
{
    public static MotionBoundary Describe(in PoseBlendParameters parameters) => parameters.Boundary;

    public static void Install(World world, Entity animation, Entity target,
        AnimationLeafBatch<PoseBlendParameters> definitions, int index, in AnimationLeafPlacement placement)
    {
        if (!world.HasBuffer<PoseBlendInstance>(animation)) world.AddBuffer<PoseBlendInstance>(animation);
        if (!world.Has<GraphParameters>(animation)) world.Add(animation, new GraphParameters());
        PoseBlendParameters parameters = definitions.Parameters[index];
        PoseResultReference output = PoseAnimation.Resolve(world, target, parameters.Output);
        var inputs = new PoseResultReference[parameters.Blend.Inputs.Length];
        for (int input = 0; input < inputs.Length; input++)
            inputs[input] = PoseAnimation.Resolve(world, target, parameters.Blend.Inputs[input].Result);
        var instance = new PoseBlendInstance
        {
            Definition = definitions, Index = index, Placement = placement, Output = output, Inputs = inputs,
            Node = PoseAnimation.RegisterNode(world, animation, output, inputs, in placement),
            Samples = new PoseBlendSamples(output.Range.Count, inputs.Length),
        };
        world.ExecuteBufferWrite<PoseBlendInstance, PoseBlendInstance>(animation, ref instance,
            static (instances, ref instance) => instances.Add(instance));
    }

    public static void Release(World world, Entity animation)
    {
        world.ExecuteBufferWrite<PoseBlendInstance>(animation, static instances => instances.Clear());
        PoseAnimation.ReleaseNodes(world, animation);
    }

    public static void Complete(World world, Entity animation) =>
        PoseAnimation.CompleteNodes(world, animation);
}

public static partial class PoseGraphExtensions
{
    public static void BlendPose(this AnimationBuilder builder, Entity target, in PoseBlendParameters parameters, byte layer = 0) =>
        builder.Leaf<PoseBlendParameters, PoseBlendLeaf>(target, in parameters, layer);
}
