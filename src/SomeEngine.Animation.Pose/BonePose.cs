using System.Numerics;

namespace SomeEngine.Animation.Pose;

/// <summary>A resolved model bone and the instance section producing its matrix.</summary>
public readonly struct BonePose
{
    private readonly BoneSource[] _sources;
    private readonly int _bone;

    public SkeletonInstance Instance => _sources[_bone].Instance;
    public int BoneIndex => _sources[_bone].Bone;
    public Matrix4x4 Transform => _sources[_bone].Transform;
    public PoseCalculation Calculation => Instance.Calculations[BoneIndex];
    public ulong Version => Calculation.Version;
    public double Time => Calculation.Time;
    public double SampleTime => Calculation.SampleTime;

    internal BonePose(BoneSource[] sources, int bone) { _sources = sources; _bone = bone; }
}

/// <summary>The version, calculation time and local-source time of one stored model matrix.</summary>
public readonly record struct PoseCalculation(ulong Version, double Time, double SampleTime);

internal readonly record struct BoneSource(SkeletonInstance Instance, int Bone)
{
    internal Matrix4x4 Transform => Instance.ModelTransforms[Bone];
}
