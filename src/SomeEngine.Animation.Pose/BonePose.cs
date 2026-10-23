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
    public ulong Version => Instance.Version;
    public double Time => Instance.Time;
    public double SampleTime => Instance.SampleTime;

    internal BonePose(BoneSource[] sources, int bone) { _sources = sources; _bone = bone; }
}

internal readonly record struct BoneSource(SkeletonInstance Instance, int Bone)
{
    internal Matrix4x4 Transform => Instance.ModelTransforms[Bone];
}
