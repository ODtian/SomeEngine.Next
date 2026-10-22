using System.Collections.Immutable;
using System.Numerics;

namespace SomeEngine.Animation.Pose;

/// <summary>Immutable source curves and timed clip metadata shared by samplers and asset branches.</summary>
public sealed class RawClipData(float duration, ImmutableArray<RawClipChannel> channels,
    ImmutableArray<ClipEvent> events = default, short rootMotionBone = -1)
{
    public float Duration { get; } = duration;
    public ImmutableArray<RawClipChannel> Channels { get; } = channels;
    public ImmutableArray<ClipEvent> Events { get; } = events.IsDefault ? [] : events;
    public short RootMotionBone { get; } = rootMotionBone;
}

public readonly struct RawClipSampler : IClipSampler<RawClipData>
{
    public static void SampleLocal(RawClipData data, float time, Span<BoneTransform> destination)
    {
        foreach (RawClipChannel channel in data.Channels.AsSpan())
        {
            Vector4 value = channel.Sample(time);
            ref BoneTransform bone = ref destination[channel.Bone];
            switch (channel.Property)
            {
                case BoneTransformProperty.Translation: bone.Translation = new(value.X, value.Y, value.Z); break;
                case BoneTransformProperty.Rotation: bone.Rotation = new(value.X, value.Y, value.Z, value.W); break;
                case BoneTransformProperty.Scale: bone.Scale = new(value.X, value.Y, value.Z); break;
            }
        }
    }
}
