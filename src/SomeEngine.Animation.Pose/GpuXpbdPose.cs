using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;

namespace SomeEngine.Animation.Pose;

public readonly record struct GpuXpbdBone(int Bone, float InverseMass = 1);

/// <summary>A directly connected secondary chain. The first point follows its source bone.</summary>
public sealed class GpuXpbdChain
{
    private readonly GpuXpbdBone[] _bones;
    public Skeleton Skeleton { get; }
    public ReadOnlySpan<GpuXpbdBone> Bones => _bones;
    public float LengthCompliance { get; }
    public float BendCompliance { get; }
    public float MaximumBend { get; }
    public Vector3 Acceleration { get; }
    public float Damping { get; }
    public float Step { get; }
    public int Iterations { get; }

    public GpuXpbdChain(Skeleton skeleton, ReadOnlySpan<GpuXpbdBone> bones, Vector3 acceleration,
        float lengthCompliance = 0, float bendCompliance = 0, float maximumBend = MathF.PI,
        float damping = .3f, float step = 1f / 60, int iterations = 8)
    {
        Skeleton = skeleton; _bones = bones.ToArray();
        _bones[0] = _bones[0] with { InverseMass = 0 };
        LengthCompliance = lengthCompliance; BendCompliance = bendCompliance; MaximumBend = maximumBend;
        Acceleration = acceleration; Damping = damping; Step = step; Iterations = iterations;
    }
}

public sealed class GpuXpbdPose(GpuPoseSource source, GpuXpbdChain chain) : GpuPoseSource
{
    public GpuPoseSource Source { get; } = source;
    public GpuXpbdChain Chain { get; } = chain;
    public override Skeleton Skeleton => Source.Skeleton;
}

public readonly record struct GpuXpbdParameters(GpuXpbdPose Pose, int TranslationBone = -1,
    float Duration = float.PositiveInfinity);

public readonly struct GpuXpbdLeaf : IAnimationLeaf<GpuXpbdParameters>
{
    public static MotionBoundary Describe(in GpuXpbdParameters parameters) => GpuRigPlayback.Boundary(parameters.Duration);
    public static object Prepare(ReadOnlySpan<GpuXpbdParameters> parameters)
    {
        var rigs = new GpuPoseRig[parameters.Length];
        for (int index = 0; index < rigs.Length; index++) rigs[index] = new(parameters[index].Pose, parameters[index].TranslationBone);
        return rigs;
    }
    public static void Install(World world, Entity animation, Entity target,
        AnimationLeafBatch<GpuXpbdParameters> definitions, int index, in AnimationLeafPlacement placement) =>
        GpuRigPlayback.Install(world, animation, target, ((GpuPoseRig[])definitions.Data!)[index],
            definitions.Parameters[index].Duration, in placement);
    public static void Release(World world, Entity animation) => GpuRigPlayback.Release(world, animation);
}
