using System.Buffers;

namespace SomeEngine.Animation.Pose;

/// <summary>A declared weight over path seconds, using the common deterministic curve and copied keyframes.</summary>
public sealed class PoseWeightCurve
{
    private readonly Keyframe[] _keyframes;
    public float From { get; }
    public float To { get; }
    public float Duration { get; }
    public float Start { get; }
    public Curve Curve { get; }

    public PoseWeightCurve(float from, float to, float duration, Curve curve = default,
        float start = 0, ReadOnlySpan<Keyframe> keyframes = default)
    {
        From = from;
        To = to;
        Duration = duration;
        Start = start;
        Curve = curve;
        _keyframes = keyframes.ToArray();
    }

    public float Sample(float pathTime)
    {
        float progress = Curve.Sample(Math.Clamp((pathTime - Start) / Duration, 0, 1), _keyframes);
        return From + (To - From) * progress;
    }
}

public readonly record struct PosePathInput(PosePath Path, PoseWeightCurve Weight);

public sealed partial class PosePath
{
    /// <summary>Combines selected paths at the same path time with declared weight curves.</summary>
    public static PosePath Blend(ReadOnlySpan<PosePathInput> inputs)
    {
        float duration = 0;
        foreach (PosePathInput input in inputs) duration = Math.Max(duration, input.Path.Duration);
        var data = new BlendData(inputs.ToArray());
        return new(new AnimationClip(inputs[0].Path.Skeleton, duration,
            ClipSampler.Create<BlendData, BlendSampler>(data)), data);
    }

    /// <summary>Applies the common masked override or reference-relative additive operation.</summary>
    public static PosePath Layer(PosePath basis, PosePath source, BoneMask mask, LayerMode mode, PoseWeightCurve weight)
    {
        var data = new LayerData(basis, source, mask, mode, weight);
        return new(new AnimationClip(basis.Skeleton, Math.Max(basis.Duration, source.Duration),
            ClipSampler.Create<LayerData, LayerSampler>(data)), data);
    }

    private sealed record BlendData(PosePathInput[] Inputs);
    private sealed record LayerData(PosePath Base, PosePath Source, BoneMask Mask, LayerMode Mode, PoseWeightCurve Weight);

    private static void SampleOne<TData, TSampler>(TData data, float time, Span<BoneTransform> destination)
        where TData : class where TSampler : struct, IClipSampler<TData>
    {
        Span<short> bones = stackalloc short[destination.Length];
        for (int bone = 0; bone < bones.Length; bone++) bones[bone] = (short)bone;
        var samples = new ClipSamples([time], bones, destination, destination.Length);
        TSampler.SampleLocalBatch<TSampler>(data, ref samples);
    }

    private static void SampleChild(AnimationClip clip, ref ClipSamples samples, Span<float> times, Span<BoneTransform> local)
    {
        for (int row = 0; row < times.Length; row++) times[row] = Math.Clamp(samples.Times[row], 0, clip.Duration);
        var child = new ClipSamples(times, samples.Bones, local, samples.BoneCount);
        clip.SampleLocal(ref child);
    }

    private readonly struct BlendSampler : IClipSampler<BlendData>
    {
        public static void SampleLocal(BlendData data, float time, Span<BoneTransform> destination) =>
            SampleOne<BlendData, BlendSampler>(data, time, destination);

        public static void SampleLocalBatch<TSampler>(BlendData data, ref ClipSamples samples)
            where TSampler : struct, IClipSampler<BlendData>
        {
            int sourceCount = data.Inputs.Length;
            int rows = samples.Count, bones = samples.BoneCount, selected = samples.Bones.Length;
            int sourceSize = rows * bones, sourceTotal = sourceCount * sourceSize;
            int compactSize = sourceCount * selected;
            BoneTransform[] storage = ArrayPool<BoneTransform>.Shared.Rent(sourceTotal + compactSize + selected);
            float[] times = ArrayPool<float>.Shared.Rent(rows);
            float[] weights = ArrayPool<float>.Shared.Rent(sourceCount);
            try
            {
                for (int source = 0; source < sourceCount; source++)
                    SampleChild(data.Inputs[source].Path.Clip, ref samples, times.AsSpan(0, rows),
                        storage.AsSpan(source * sourceSize, sourceSize));
                Span<BoneTransform> compact = storage.AsSpan(sourceTotal, compactSize);
                Span<BoneTransform> output = storage.AsSpan(sourceTotal + compactSize, selected);
                for (int row = 0; row < rows; row++)
                {
                    for (int source = 0; source < sourceCount; source++)
                    {
                        weights[source] = data.Inputs[source].Weight.Sample(samples.Times[row]);
                        for (int bone = 0; bone < selected; bone++)
                            compact[source * selected + bone] = storage[source * sourceSize + row * bones + samples.Bones[bone]];
                    }
                    BoneTransformOps.Weighted(compact, selected, weights.AsSpan(0, sourceCount), output);
                    for (int bone = 0; bone < selected; bone++) samples.LocalPose(row)[samples.Bones[bone]] = output[bone];
                }
            }
            finally
            {
                ArrayPool<BoneTransform>.Shared.Return(storage);
                ArrayPool<float>.Shared.Return(times);
                ArrayPool<float>.Shared.Return(weights);
            }
        }
    }

    private readonly struct LayerSampler : IClipSampler<LayerData>
    {
        public static void SampleLocal(LayerData data, float time, Span<BoneTransform> destination) =>
            SampleOne<LayerData, LayerSampler>(data, time, destination);

        public static void SampleLocalBatch<TSampler>(LayerData data, ref ClipSamples samples)
            where TSampler : struct, IClipSampler<LayerData>
        {
            int size = samples.Count * samples.BoneCount;
            BoneTransform[] storage = ArrayPool<BoneTransform>.Shared.Rent(size * 2);
            float[] times = ArrayPool<float>.Shared.Rent(samples.Count);
            try
            {
                SampleChild(data.Base.Clip, ref samples, times.AsSpan(0, samples.Count), storage.AsSpan(0, size));
                SampleChild(data.Source.Clip, ref samples, times.AsSpan(0, samples.Count), storage.AsSpan(size, size));
                ReadOnlySpan<BoneTransform> reference = data.Base.Skeleton.ReferencePose;
                for (int row = 0; row < samples.Count; row++)
                {
                    float weight = data.Weight.Sample(samples.Times[row]);
                    foreach (short bone in samples.Bones)
                    {
                        ref BoneTransform basis = ref storage[row * samples.BoneCount + bone];
                        ref BoneTransform source = ref storage[size + row * samples.BoneCount + bone];
                        float boneWeight = weight * data.Mask.Weights[bone];
                        samples.LocalPose(row)[bone] = data.Mode == LayerMode.Override
                            ? BoneTransformOps.Lerp(in basis, in source, boneWeight)
                            : BoneTransformOps.Additive(in basis, in source, in reference[bone], boneWeight);
                    }
                }
            }
            finally
            {
                ArrayPool<BoneTransform>.Shared.Return(storage);
                ArrayPool<float>.Shared.Return(times);
            }
        }
    }
}
