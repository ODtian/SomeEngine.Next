using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;

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
    internal int Level;
    internal PoseBlendSamples Samples;
    internal uint Starts;
    internal bool Active, Sampled, Finished, Running;
    internal float Weight;
    internal double Time, Phase;
}

internal sealed class PoseBlendSamples(int bones, int inputs)
{
    internal readonly int Stride = bones + 1;
    internal readonly BoneTransform[] Inputs = new BoneTransform[(bones + 1) * inputs];
    internal readonly BoneTransform[] Output = new BoneTransform[bones + 1];
    internal readonly float[] Weights = new float[inputs];
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
            Samples = new PoseBlendSamples(output.Range.Count, inputs.Length),
        };
        world.ExecuteBufferWrite<PoseBlendInstance, PoseBlendInstance>(animation, ref instance,
            static (instances, ref instance) => instances.Add(instance));
    }

    public static void Release(World world, Entity animation) =>
        world.ExecuteBufferWrite<PoseBlendInstance>(animation, static instances => instances.Clear());

    public static void Complete(World world, Entity animation) =>
        world.ExecuteBufferWrite<PoseBlendInstance>(animation, static instances =>
        {
            Span<PoseBlendInstance> blends = instances.AsSpan();
            foreach (ref PoseBlendInstance blend in blends) blend.Level = -1;
            for (int index = 0; index < blends.Length; index++) AssignLevel(blends, index);
        });

    private static int AssignLevel(Span<PoseBlendInstance> blends, int index)
    {
        if (blends[index].Level >= 0) return blends[index].Level;
        int level = 0;
        foreach (PoseResultReference source in blends[index].Inputs)
            for (int producer = 0; producer < blends.Length; producer++)
                if (producer != index && blends[producer].Output == source)
                    level = Math.Max(level, AssignLevel(blends, producer) + 1);
        return blends[index].Level = level;
    }
}

public static partial class PoseGraphExtensions
{
    public static void BlendPose(this AnimationBuilder builder, Entity target, in PoseBlendParameters parameters, byte layer = 0) =>
        builder.Leaf<PoseBlendParameters, PoseBlendLeaf>(target, in parameters, layer);
}
