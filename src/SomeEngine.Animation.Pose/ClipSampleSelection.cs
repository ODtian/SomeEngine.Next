namespace SomeEngine.Animation.Pose;

/// <summary>A ready clip and the own-bone indices to return for one sample, in caller order.</summary>
public sealed class ClipSampleSelection
{
    private readonly short[] _bones;

    public AnimationClip Clip { get; }
    public ReadOnlySpan<short> Bones => _bones;
    /// <summary>The explicitly selected parent result used by model sampling of a derived skeleton.</summary>
    public ModelPose? ParentPose { get; }

    public ClipSampleSelection(AnimationClip clip, ReadOnlySpan<short> bones, ModelPose? parentPose = null)
    {
        Clip = clip;
        _bones = bones.ToArray();
        ParentPose = parentPose;
    }

    /// <summary>Selects the clip skeleton's complete local pose.</summary>
    public ClipSampleSelection(AnimationClip clip, ModelPose? parentPose = null)
    {
        Clip = clip;
        ParentPose = parentPose;
        _bones = new short[clip.Skeleton.BoneCount];
        for (int bone = 0; bone < _bones.Length; bone++) _bones[bone] = (short)bone;
    }
}
