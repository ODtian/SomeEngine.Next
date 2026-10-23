namespace SomeEngine.Animation.Pose;

/// <summary>A concrete codec writes animated values into a pose initialized from its skeleton.</summary>
public interface IClipSampler<TData> where TData : class
{
    static abstract void SampleLocal(TData data, float time, Span<BoneTransform> destination);

    static virtual void SampleLocalBatch<TSampler>(TData data, ref ClipSamples samples)
        where TSampler : struct, IClipSampler<TData>
    {
        for (int sample = 0; sample < samples.Count; sample++)
            TSampler.SampleLocal(data, samples.Times[sample], samples.LocalPose(sample));
    }
}

/// <summary>A concrete sampling function and its ordinary reference to ready clip data.</summary>
public readonly unsafe struct ClipSampler
{
    private readonly object _data;
    private readonly delegate* managed<object, float, Span<BoneTransform>, void> _sample;
    private readonly delegate* managed<object, ref ClipSamples, void> _sampleBatch;

    private ClipSampler(object data, delegate* managed<object, float, Span<BoneTransform>, void> sample,
        delegate* managed<object, ref ClipSamples, void> sampleBatch)
    {
        _data = data;
        _sample = sample;
        _sampleBatch = sampleBatch;
    }

    public static ClipSampler Create<TData, TSampler>(TData data)
        where TData : class where TSampler : struct, IClipSampler<TData>
        => new(data, &Sample<TData, TSampler>, &SampleBatch<TData, TSampler>);

    public void SampleLocal(float time, Span<BoneTransform> destination) => _sample(_data, time, destination);
    public void SampleLocal(ref ClipSamples samples) => _sampleBatch(_data, ref samples);

    private static void Sample<TData, TSampler>(object data, float time, Span<BoneTransform> destination)
        where TData : class where TSampler : struct, IClipSampler<TData>
        => TSampler.SampleLocal((TData)data, time, destination);

    private static void SampleBatch<TData, TSampler>(object data, ref ClipSamples samples)
        where TData : class where TSampler : struct, IClipSampler<TData>
        => TSampler.SampleLocalBatch<TSampler>((TData)data, ref samples);
}
