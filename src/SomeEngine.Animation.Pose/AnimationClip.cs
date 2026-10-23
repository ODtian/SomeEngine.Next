using System.Collections.Immutable;
using SomeEngine.Serialization;

namespace SomeEngine.Animation.Pose;

[BinarySerializable, BinaryValue, BinaryFields(nameof(Time), nameof(Id))]
public readonly partial record struct ClipEvent(float Time, ushort Id);

/// <summary>A skeleton and ready clip data that can be sampled into caller-provided storage.</summary>
[AssetUse]
public sealed class AnimationClip
{
    private readonly ImmutableArray<ClipEvent> _events;
    private readonly ClipSampler _sampler;

    public Skeleton Skeleton { get; }
    public float Duration { get; }
    public short RootMotionBone { get; }
    public ReadOnlySpan<ClipEvent> Events => _events.AsSpan();

    public AnimationClip(Skeleton skeleton, float duration, ClipSampler sampler,
        ImmutableArray<ClipEvent> events = default, short rootMotionBone = -1)
    {
        Skeleton = skeleton;
        Duration = duration;
        RootMotionBone = rootMotionBone;
        _events = events.IsDefault ? [] : events;
        _sampler = sampler;
    }

    /// <summary>Samples seconds from the clip start, retaining reference values for unanimated properties.</summary>
    public void SampleLocal(float time, Span<BoneTransform> destination)
    {
        Span<BoneTransform> pose = destination[..Skeleton.BoneCount];
        Skeleton.ReferencePose.CopyTo(pose);
        _sampler.SampleLocal(Math.Clamp(time, 0, Duration), pose);
    }

    internal void SampleLocal(ref ClipSamples samples)
    {
        for (int sample = 0; sample < samples.Count; sample++)
            Skeleton.ReferencePose.CopyTo(samples.LocalPose(sample));
        _sampler.SampleLocal(ref samples);
    }
}
