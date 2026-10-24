using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.Job;

namespace SomeEngine.Animation.Pose;

/// <summary>Local translation, parent-frame angular, and logarithmic scale rates per calculation second.</summary>
public readonly record struct BoneVelocity(Vector3 Translation, Vector3 Angular, Vector3 LogScale);

public readonly record struct InertialPoseParameters(PoseResultId Source, PoseResultId Output,
    float DecayRate = 12, float Tolerance = .00001f);

[BufferCapacity(1)]
public struct InertialPoseInstance : IBufferElement
{
    public AnimationLeafBatch<InertialPoseParameters> Definition;
    public int Index;
    public AnimationLeafPlacement Placement;
    public PoseResultReference Source, Output;
    /// <summary>Caller-provided action identity; changing it starts a transition from the current visible result.</summary>
    public ulong Action;
    public Span<BoneVelocity> SourceVelocities => Samples.SourceVelocities;
    public ReadOnlySpan<BoneVelocity> Velocities => Samples.Velocities;
    internal InertialPoseSamples Samples;
    internal PoseNode Node;
    internal PoseResult InputResult;
    internal uint Starts;
    internal ulong PreviousAction;
    internal float Weight;
    internal bool Initialized, Active, Sampled;
}

internal sealed class InertialPoseSamples
{
    internal readonly BoneTransform[] Source, Output;
    internal readonly BoneVelocity[] SourceVelocities, Velocities;
    internal readonly InertialBoneOffset[] Offsets;
    internal readonly JobResourceAccess[] Accesses;

    internal InertialPoseSamples(int bones)
    {
        Source = new BoneTransform[bones];
        Output = new BoneTransform[bones];
        SourceVelocities = new BoneVelocity[bones];
        Velocities = new BoneVelocity[bones];
        Offsets = new InertialBoneOffset[bones];
        Accesses = [JobResourceAccess.Read(Source), JobResourceAccess.Read(SourceVelocities),
            JobResourceAccess.Write(Output), JobResourceAccess.Write(Velocities), JobResourceAccess.Write(Offsets)];
    }
}

internal struct InertialBoneOffset
{
    internal Vector3 Translation, Rotation, LogScale;
    internal Vector3 TranslationRate, RotationRate, LogScaleRate;
    internal bool Moving;
}

public readonly struct InertialPoseLeaf : IAnimationLeaf<InertialPoseParameters>
{
    public static MotionBoundary Describe(in InertialPoseParameters parameters) => new(StageEnd.Settled);

    public static void Install(World world, Entity animation, Entity target,
        AnimationLeafBatch<InertialPoseParameters> definitions, int index, in AnimationLeafPlacement placement)
    {
        if (!world.HasBuffer<InertialPoseInstance>(animation)) world.AddBuffer<InertialPoseInstance>(animation);
        InertialPoseParameters parameters = definitions.Parameters[index];
        var instance = new InertialPoseInstance
        {
            Definition = definitions, Index = index, Placement = placement,
            Source = PoseAnimation.Resolve(world, target, parameters.Source),
            Output = PoseAnimation.Resolve(world, target, parameters.Output),
        };
        instance.Samples = new(instance.Output.Range.Count);
        instance.Node = PoseAnimation.RegisterNode(world, animation, instance.Output, [instance.Source], in placement);
        world.ExecuteBufferWrite<InertialPoseInstance, InertialPoseInstance>(animation, ref instance,
            static (instances, ref instance) => instances.Add(instance));
    }

    public static void Complete(World world, Entity animation) => PoseAnimation.CompleteNodes(world, animation);

    public static void Release(World world, Entity animation)
    {
        world.ExecuteBufferWrite<InertialPoseInstance>(animation, static instances => instances.Clear());
        PoseAnimation.ReleaseNodes(world, animation);
    }
}

public static partial class PoseGraphExtensions
{
    public static void InertializePose(this AnimationBuilder builder, Entity target, in InertialPoseParameters parameters, byte layer = 0) =>
        builder.Leaf<InertialPoseParameters, InertialPoseLeaf>(target, in parameters, layer);
}
