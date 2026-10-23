using System.Collections.Immutable;
using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.Core.Diagnostics;

namespace SomeEngine.Animation.Pose;

/// <summary>Translation and scale ranges for one bone's unsigned 16-bit samples.</summary>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct QuantizedBoneRange(Vector4 TranslationMinimum, Vector4 TranslationExtent,
    Vector4 ScaleMinimum, Vector4 ScaleExtent);

/// <summary>Uniform-time TRS samples shared by the CPU sampler and GPU pose evaluation.</summary>
public sealed class QuantizedClipData
{
    private readonly QuantizedBoneRange[] _ranges;
    private readonly uint[] _samples;
    public float Duration { get; }
    public int SampleCount { get; }
    public int BoneCount => _ranges.Length;
    public ReadOnlySpan<QuantizedBoneRange> Ranges => _ranges;
    /// <summary>Five words per sample/bone: Tx Ty, Tz Qx, Qy Qz, Qw Sx, Sy Sz.</summary>
    public ReadOnlySpan<uint> Samples => _samples;
    public ImmutableArray<ClipEvent> Events { get; }
    public short RootMotionBone { get; }

    internal QuantizedClipData(float duration, int sampleCount, QuantizedBoneRange[] ranges, uint[] samples,
        ImmutableArray<ClipEvent> events, short rootMotionBone) =>
        (Duration, SampleCount, _ranges, _samples, Events, RootMotionBone) =
        (duration, sampleCount, ranges, samples, events, rootMotionBone);

    public AnimationClip CreateClip(Skeleton skeleton) => new(skeleton, Duration,
        ClipSampler.Create<QuantizedClipData, QuantizedClipSampler>(this), Events, RootMotionBone);

    /// <summary>Samples a ready clip uniformly, then quantizes each bone's complete TRS values.</summary>
    public static QuantizedClipData Encode(AnimationClip clip, float sampleRate = 30)
    {
        DevelopmentCheck.Require(sampleRate > 0 && float.IsFinite(sampleRate), "Uniform sampling requires a positive finite rate.");
        int count = Math.Max(1, (int)MathF.Ceiling(clip.Duration * sampleRate) + 1);
        int bones = clip.Skeleton.BoneCount;
        var poses = new BoneTransform[count * bones];
        for (int sample = 0; sample < count; sample++)
            clip.SampleLocal(count == 1 ? 0 : clip.Duration * sample / (count - 1), poses.AsSpan(sample * bones, bones));
        var ranges = new QuantizedBoneRange[bones];
        for (int bone = 0; bone < bones; bone++)
        {
            Vector3 translationMin = poses[bone].Translation, translationMax = translationMin;
            Vector3 scaleMin = poses[bone].Scale, scaleMax = scaleMin;
            for (int sample = 1; sample < count; sample++)
            {
                ref BoneTransform value = ref poses[sample * bones + bone];
                translationMin = Vector3.Min(translationMin, value.Translation);
                translationMax = Vector3.Max(translationMax, value.Translation);
                scaleMin = Vector3.Min(scaleMin, value.Scale);
                scaleMax = Vector3.Max(scaleMax, value.Scale);
            }
            ranges[bone] = new(new(translationMin, 0), new(translationMax - translationMin, 0),
                new(scaleMin, 0), new(scaleMax - scaleMin, 0));
        }
        var words = new uint[count * bones * 5];
        for (int sample = 0; sample < count; sample++)
        for (int bone = 0; bone < bones; bone++)
        {
            BoneTransform value = poses[sample * bones + bone];
            QuantizedBoneRange range = ranges[bone];
            Quaternion rotation = Quaternion.Normalize(value.Rotation);
            uint tx = Encode(value.Translation.X, range.TranslationMinimum.X, range.TranslationExtent.X);
            uint ty = Encode(value.Translation.Y, range.TranslationMinimum.Y, range.TranslationExtent.Y);
            uint tz = Encode(value.Translation.Z, range.TranslationMinimum.Z, range.TranslationExtent.Z);
            uint sx = Encode(value.Scale.X, range.ScaleMinimum.X, range.ScaleExtent.X);
            uint sy = Encode(value.Scale.Y, range.ScaleMinimum.Y, range.ScaleExtent.Y);
            uint sz = Encode(value.Scale.Z, range.ScaleMinimum.Z, range.ScaleExtent.Z);
            int index = (sample * bones + bone) * 5;
            words[index] = tx | ty << 16;
            words[index + 1] = tz | Encode(rotation.X, -1, 2) << 16;
            words[index + 2] = Encode(rotation.Y, -1, 2) | Encode(rotation.Z, -1, 2) << 16;
            words[index + 3] = Encode(rotation.W, -1, 2) | sx << 16;
            words[index + 4] = sy | sz << 16;
        }
        return new(clip.Duration, count, ranges, words, [.. clip.Events], clip.RootMotionBone);
    }

    private static uint Encode(float value, float minimum, float extent) => extent == 0 ? 0
        : (uint)Math.Clamp(MathF.Round((value - minimum) / extent * 65535), 0, 65535);

    internal BoneTransform Sample(int bone, float time)
    {
        float sample = Duration == 0 ? 0 : Math.Clamp(time / Duration * (SampleCount - 1), 0, SampleCount - 1);
        int first = (int)sample;
        BoneTransform a = Decode(bone, first), b = Decode(bone, Math.Min(first + 1, SampleCount - 1));
        float amount = sample - first;
        return new()
        {
            Translation = Vector3.Lerp(a.Translation, b.Translation, amount),
            Rotation = Quaternion.Normalize(Quaternion.Slerp(a.Rotation, b.Rotation, amount)),
            Scale = Vector3.Lerp(a.Scale, b.Scale, amount),
        };
    }

    private BoneTransform Decode(int bone, int sample)
    {
        QuantizedBoneRange range = _ranges[bone];
        int index = (sample * BoneCount + bone) * 5;
        uint a = _samples[index], b = _samples[index + 1], c = _samples[index + 2], d = _samples[index + 3], e = _samples[index + 4];
        Vector3 translation = new(a & 65535, a >> 16, b & 65535);
        Vector3 scale = new(d >> 16, e & 65535, e >> 16);
        return new()
        {
            Translation = new Vector3(range.TranslationMinimum.X, range.TranslationMinimum.Y, range.TranslationMinimum.Z)
                + translation * new Vector3(range.TranslationExtent.X, range.TranslationExtent.Y, range.TranslationExtent.Z) / 65535,
            Rotation = Quaternion.Normalize(new Quaternion((b >> 16) * (2f / 65535) - 1,
                (c & 65535) * (2f / 65535) - 1, (c >> 16) * (2f / 65535) - 1, (d & 65535) * (2f / 65535) - 1)),
            Scale = new Vector3(range.ScaleMinimum.X, range.ScaleMinimum.Y, range.ScaleMinimum.Z)
                + scale * new Vector3(range.ScaleExtent.X, range.ScaleExtent.Y, range.ScaleExtent.Z) / 65535,
        };
    }
}

public readonly struct QuantizedClipSampler : IClipSampler<QuantizedClipData>
{
    public static void SampleLocal(QuantizedClipData data, float time, Span<BoneTransform> destination)
    {
        for (int bone = 0; bone < data.BoneCount; bone++) destination[bone] = data.Sample(bone, time);
    }

    public static void SampleLocalBatch<TSampler>(QuantizedClipData data, ref ClipSamples samples)
        where TSampler : struct, IClipSampler<QuantizedClipData>
    {
        foreach (short bone in samples.Bones)
            for (int sample = 0; sample < samples.Count; sample++)
                samples.LocalPose(sample)[bone] = data.Sample(bone, samples.Times[sample]);
    }
}
