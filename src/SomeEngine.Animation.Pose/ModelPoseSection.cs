using System.Numerics;

namespace SomeEngine.Animation.Pose;

/// <summary>One producing instance's continuous model matrices and calculation metadata.</summary>
public readonly struct ModelPoseSection
{
    public SkeletonInstance Instance { get; }
    public int BoneStart => Instance.Skeleton.InheritedBoneCount;
    public PoseRange Range => Instance.Range;
    public ReadOnlySpan<Matrix4x4> Transforms => Instance.ModelTransforms;
    public ulong Version => Instance.Version;
    public double Time => Instance.Time;
    public double SampleTime => Instance.SampleTime;

    internal ModelPoseSection(SkeletonInstance instance) => Instance = instance;
}
