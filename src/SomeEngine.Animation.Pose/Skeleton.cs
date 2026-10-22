using SomeEngine.Core.Diagnostics;
using SomeEngine.Serialization;

namespace SomeEngine.Animation.Pose;

/// <summary>Ready, immutable bone topology, reference transforms and source joint correspondence.</summary>
[AssetUse]
public sealed class Skeleton
{
    private readonly short[] _parents;
    private readonly BoneTransform[] _referencePose;
    private readonly uint[] _nameHashes;
    private readonly short[] _jointBones;

    public int BoneCount => _parents.Length;
    public ReadOnlySpan<short> Parents => _parents;
    public ReadOnlySpan<BoneTransform> ReferencePose => _referencePose;
    public ReadOnlySpan<uint> NameHashes => _nameHashes;
    /// <summary>Bone index for each source skin joint, in the original joint order.</summary>
    public ReadOnlySpan<short> JointBones => _jointBones;

    public Skeleton(ReadOnlySpan<short> parents, ReadOnlySpan<BoneTransform> referencePose,
        ReadOnlySpan<uint> nameHashes, ReadOnlySpan<short> jointBones = default)
        : this(parents.ToArray(), referencePose.ToArray(), nameHashes.ToArray(), jointBones.ToArray()) { }

    internal Skeleton(short[] parents, BoneTransform[] referencePose, uint[] nameHashes, short[] jointBones)
    {
        CheckContent(parents, referencePose, nameHashes, jointBones);
        _parents = parents;
        _referencePose = referencePose;
        _nameHashes = nameHashes;
        _jointBones = jointBones;
    }

    [System.Diagnostics.Conditional("DEBUG")]
    private static void CheckContent(ReadOnlySpan<short> parents, ReadOnlySpan<BoneTransform> referencePose,
        ReadOnlySpan<uint> nameHashes, ReadOnlySpan<short> jointBones)
    {
        DevelopmentCheck.Require(parents.Length <= short.MaxValue + 1, "Bone indices must fit in a signed 16-bit value.");
        DevelopmentCheck.Require(referencePose.Length == parents.Length && nameHashes.Length == parents.Length,
            "Each bone requires a parent index, reference transform and name hash.");
        for (int bone = 0; bone < parents.Length; bone++)
            DevelopmentCheck.Require(parents[bone] >= -1 && parents[bone] < bone, "A bone's parent must precede it, or be -1 for a root.");
        foreach (short bone in jointBones)
            DevelopmentCheck.Require(bone >= 0 && bone < parents.Length, "Each source joint must reference a bone.");
    }
}
