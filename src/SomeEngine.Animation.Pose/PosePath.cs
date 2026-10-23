using System.Buffers;

namespace SomeEngine.Animation.Pose;

/// <summary>A caller-selected deterministic pose path backed by ready clip sampling.</summary>
public sealed partial class PosePath
{
    internal AnimationClip Clip { get; }
    public Skeleton Skeleton => Clip.Skeleton;
    public float Duration => Clip.Duration;

    private PosePath(AnimationClip clip) => Clip = clip;

    public static PosePath FromClip(AnimationClip clip) => new(clip);

    /// <summary>Joins selected paths in order, each using its own elapsed time.</summary>
    public static PosePath Sequence(ReadOnlySpan<PosePath> parts)
    {
        var sequence = new SequenceData(parts);
        return new(new AnimationClip(parts[0].Skeleton, sequence.Duration,
            ClipSampler.Create<SequenceData, SequenceSampler>(sequence)));
    }

    private sealed class SequenceData
    {
        internal readonly PosePath[] Parts;
        internal readonly float[] Starts;
        internal readonly float Duration;

        internal SequenceData(ReadOnlySpan<PosePath> parts)
        {
            Parts = parts.ToArray();
            Starts = new float[parts.Length];
            for (int part = 0; part < parts.Length; part++)
            {
                Starts[part] = Duration;
                Duration += parts[part].Duration;
            }
        }

        internal int Find(float time)
        {
            int index = Array.BinarySearch(Starts, time);
            return index >= 0 ? index : ~index - 1;
        }
    }

    private readonly struct SequenceSampler : IClipSampler<SequenceData>
    {
        public static void SampleLocal(SequenceData data, float time, Span<BoneTransform> destination)
        {
            int part = data.Find(time);
            data.Parts[part].Clip.SampleLocal(time - data.Starts[part], destination);
        }

        public static void SampleLocalBatch<TSampler>(SequenceData data, ref ClipSamples samples)
            where TSampler : struct, IClipSampler<SequenceData>
        {
            int[] selected = ArrayPool<int>.Shared.Rent(samples.Count);
            int[] rows = ArrayPool<int>.Shared.Rent(samples.Count);
            float[] times = ArrayPool<float>.Shared.Rent(samples.Count);
            BoneTransform[] local = ArrayPool<BoneTransform>.Shared.Rent(samples.Count * samples.BoneCount);
            try
            {
                for (int row = 0; row < samples.Count; row++) selected[row] = data.Find(samples.Times[row]);
                for (int part = 0; part < data.Parts.Length; part++)
                {
                    int count = 0;
                    for (int row = 0; row < samples.Count; row++)
                    {
                        if (selected[row] != part) continue;
                        rows[count] = row;
                        times[count++] = samples.Times[row] - data.Starts[part];
                    }
                    if (count == 0) continue;
                    var source = new ClipSamples(times.AsSpan(0, count), samples.Bones,
                        local.AsSpan(0, count * samples.BoneCount), samples.BoneCount);
                    data.Parts[part].Clip.SampleLocal(ref source);
                    for (int row = 0; row < count; row++)
                        foreach (short bone in samples.Bones)
                            samples.LocalPose(rows[row])[bone] = source.LocalPose(row)[bone];
                }
            }
            finally
            {
                ArrayPool<int>.Shared.Return(selected);
                ArrayPool<int>.Shared.Return(rows);
                ArrayPool<float>.Shared.Return(times);
                ArrayPool<BoneTransform>.Shared.Return(local);
            }
        }
    }
}
