using System.Numerics;
using SomeEngine.ECS.Components;

namespace SomeEngine.Animation.Pose;

/// <summary>A shared GPU pose result that can feed a concrete model-space operation.</summary>
public abstract class GpuPoseSource
{
    private protected GpuPoseSource() { }
    public abstract Skeleton Skeleton { get; }
}

/// <summary>Three own bones and the permitted angle between their two outgoing segments, in radians.</summary>
public sealed class GpuTwoBoneChain(Skeleton skeleton, int root, int middle, int tip,
    float minimumBend = 0, float maximumBend = MathF.PI)
{
    public Skeleton Skeleton { get; } = skeleton;
    public int Root { get; } = root;
    public int Middle { get; } = middle;
    public int Tip { get; } = tip;
    public float MinimumBend { get; } = minimumBend;
    public float MaximumBend { get; } = maximumBend;
}

public readonly record struct GpuAimBone(int Bone, Vector3 Axis, float Weight = 1, float MaximumAngle = MathF.PI);

/// <summary>Ordered aim joints, their local axes, weights and angular limits.</summary>
public sealed class GpuAimChain
{
    private readonly GpuAimBone[] _bones;
    public Skeleton Skeleton { get; }
    public ReadOnlySpan<GpuAimBone> Bones => _bones;
    public GpuAimChain(Skeleton skeleton, ReadOnlySpan<GpuAimBone> bones)
    {
        Skeleton = skeleton;
        _bones = bones.ToArray();
        for (int bone = 0; bone < _bones.Length; bone++)
            _bones[bone] = _bones[bone] with { Axis = Vector3.Normalize(_bones[bone].Axis) };
    }
}

public sealed class GpuTwoBonePose(GpuPoseSource source, GpuTwoBoneChain chain, int goal) : GpuPoseSource
{
    public GpuPoseSource Source { get; } = source;
    public GpuTwoBoneChain Chain { get; } = chain;
    public int Goal { get; } = goal;
    public override Skeleton Skeleton => Source.Skeleton;
}

public sealed class GpuAimPose(GpuPoseSource source, GpuAimChain chain, int goal) : GpuPoseSource
{
    public GpuPoseSource Source { get; } = source;
    public GpuAimChain Chain { get; } = chain;
    public int Goal { get; } = goal;
    public override Skeleton Skeleton => Source.Skeleton;
}

/// <summary>Actor-model-space input read at the normal Game-world extraction boundary.</summary>
[BufferCapacity(4)]
public struct GpuPoseGoal : IBufferElement
{
    public Vector3 Position;
    public float PositionWeight;
    public Vector3 BendDirection;
    public float OrientationWeight;
    public Quaternion Orientation;
}

/// <summary>Shared source composition and dependency-ordered model-space operations.</summary>
public sealed class GpuPoseRig
{
    private readonly GpuPoseSource[] _steps;
    public GpuPoseBlend Source { get; }
    public ReadOnlySpan<GpuPoseSource> Steps => _steps;
    public int GoalCount { get; }
    public int TranslationBone { get; }

    public GpuPoseRig(GpuPoseSource output, int translationBone = -1)
    {
        TranslationBone = translationBone;
        var steps = new List<GpuPoseSource>();
        while (output is GpuTwoBonePose || output is GpuAimPose || output is GpuStridePose || output is GpuXpbdPose)
        {
            if (output is GpuXpbdPose xpbd)
            {
                steps.Add(xpbd);
                output = xpbd.Source;
                continue;
            }
            if (output is GpuStridePose stride)
            {
                steps.Add(stride);
                foreach (GpuStrideLeg leg in stride.Rig.Legs) GoalCount = Math.Max(GoalCount, leg.Goal + 1);
                output = stride.Source;
                continue;
            }
            steps.Add(output);
            int goal = output is GpuTwoBonePose limb ? limb.Goal : ((GpuAimPose)output).Goal;
            GoalCount = Math.Max(GoalCount, goal + 1);
            output = output is GpuTwoBonePose twoBone ? twoBone.Source : ((GpuAimPose)output).Source;
        }
        Source = (GpuPoseBlend)output;
        steps.Reverse();
        _steps = steps.ToArray();
    }
}
