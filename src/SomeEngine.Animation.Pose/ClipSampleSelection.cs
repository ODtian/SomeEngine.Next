namespace SomeEngine.Animation.Pose;

/// <summary>A ready clip and the own-bone indices to return for one sample, in caller order.</summary>
public sealed class ClipSampleSelection
{
    private readonly short[] _bones;

    public AnimationClip Clip { get; }
    public ReadOnlySpan<short> Bones => _bones;

    public ClipSampleSelection(AnimationClip clip, ReadOnlySpan<short> bones)
    {
        Clip = clip;
        _bones = bones.ToArray();
    }

    /// <summary>Selects the clip skeleton's complete local pose.</summary>
    public ClipSampleSelection(AnimationClip clip)
    {
        Clip = clip;
        _bones = new short[clip.Skeleton.BoneCount];
        for (int bone = 0; bone < _bones.Length; bone++) _bones[bone] = (short)bone;
    }
}
