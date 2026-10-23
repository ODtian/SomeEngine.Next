namespace SomeEngine.Animation.Pose;

/// <summary>One ready clip's times, requested bones and reference-initialized local pose rows.</summary>
public readonly ref struct ClipSamples
{
    private readonly Span<BoneTransform> _local;
    private readonly int _boneCount;

    public ReadOnlySpan<float> Times { get; }
    /// <summary>Requested skeleton bone indices, in increasing order.</summary>
    public ReadOnlySpan<short> Bones { get; }
    public int Count => Times.Length;

    internal ClipSamples(ReadOnlySpan<float> times, ReadOnlySpan<short> bones,
        Span<BoneTransform> local, int boneCount)
    {
        Times = times;
        Bones = bones;
        _local = local;
        _boneCount = boneCount;
    }

    /// <summary>A full local row indexed by the clip skeleton's own bone indices.</summary>
    public Span<BoneTransform> LocalPose(int sample) => _local.Slice(sample * _boneCount, _boneCount);
}
