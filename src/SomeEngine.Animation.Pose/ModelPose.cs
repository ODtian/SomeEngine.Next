namespace SomeEngine.Animation.Pose;

/// <summary>A live read-only view of the inherited and own model-space result sections.</summary>
public readonly struct ModelPose
{
    private readonly SkeletonInstance _instance;

    public Skeleton Skeleton => _instance.Skeleton;
    public ModelTransforms Transforms => new(_instance.BoneSources);
    /// <summary>The own section's latest matrix-write version.</summary>
    public ulong Version => _instance.Version;
    /// <summary>The calculation time of the own section's latest matrix write.</summary>
    public double Time => _instance.Time;
    /// <summary>The local-source time accompanying the own section's latest write.</summary>
    public double SampleTime => _instance.SampleTime;
    public int SectionCount => _instance.Sections.Length;

    internal ModelPose(SkeletonInstance instance) => _instance = instance;

    public BonePose GetBone(int bone) => new(_instance.BoneSources, bone);
    public ModelPoseSection GetSection(int section) => new(_instance.Sections[section]);
}
