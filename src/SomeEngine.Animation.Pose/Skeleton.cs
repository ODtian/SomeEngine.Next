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
    private readonly short[] _inheritedParents;

    public Skeleton? Parent { get; }
    public int BoneCount => _parents.Length;
    public int InheritedBoneCount { get; }
    public int TotalBoneCount => InheritedBoneCount + BoneCount;
    public ReadOnlySpan<short> Parents => _parents;
    /// <summary>Parent-definition model bone for each own root, or -1 for a bone governed by Parents.</summary>
    public ReadOnlySpan<short> InheritedParents => _inheritedParents;
    public ReadOnlySpan<BoneTransform> ReferencePose => _referencePose;
    public ReadOnlySpan<uint> NameHashes => _nameHashes;
    /// <summary>Bone index for each source skin joint, in the original joint order.</summary>
    public ReadOnlySpan<short> JointBones => _jointBones;

    public Skeleton(ReadOnlySpan<short> parents, ReadOnlySpan<BoneTransform> referencePose,
        ReadOnlySpan<uint> nameHashes, ReadOnlySpan<short> jointBones = default)
        : this(parents.ToArray(), referencePose.ToArray(), nameHashes.ToArray(), jointBones.ToArray()) { }

    public Skeleton(Skeleton parent, ReadOnlySpan<short> parents, ReadOnlySpan<BoneTransform> referencePose,
        ReadOnlySpan<uint> nameHashes, ReadOnlySpan<short> inheritedParents, ReadOnlySpan<short> jointBones = default)
        : this(parent, parents.ToArray(), referencePose.ToArray(), nameHashes.ToArray(),
            jointBones.ToArray(), inheritedParents.ToArray()) { }

    internal Skeleton(short[] parents, BoneTransform[] referencePose, uint[] nameHashes, short[] jointBones)
        : this(null, parents, referencePose, nameHashes, jointBones, CreateRootLinks(parents.Length)) { }

    private Skeleton(Skeleton? parent, short[] parents, BoneTransform[] referencePose, uint[] nameHashes,
        short[] jointBones, short[] inheritedParents)
    {
        Parent = parent;
        InheritedBoneCount = parent?.TotalBoneCount ?? 0;
        CheckContent(parents, referencePose, nameHashes, jointBones, inheritedParents, InheritedBoneCount);
        _parents = parents;
        _referencePose = referencePose;
        _nameHashes = nameHashes;
        _jointBones = jointBones;
        _inheritedParents = inheritedParents;
    }

    private static short[] CreateRootLinks(int count)
    {
        var links = new short[count];
        Array.Fill(links, (short)-1);
        return links;
    }

    [System.Diagnostics.Conditional("DEBUG")]
    private static void CheckContent(ReadOnlySpan<short> parents, ReadOnlySpan<BoneTransform> referencePose,
        ReadOnlySpan<uint> nameHashes, ReadOnlySpan<short> jointBones, ReadOnlySpan<short> inheritedParents,
        int inheritedBoneCount)
    {
        DevelopmentCheck.Require(inheritedBoneCount + parents.Length <= short.MaxValue + 1, "Bone indices must fit in a signed 16-bit value.");
        DevelopmentCheck.Require(referencePose.Length == parents.Length && nameHashes.Length == parents.Length,
            "Each bone requires a parent index, reference transform and name hash.");
        DevelopmentCheck.Require(inheritedParents.Length == parents.Length, "Each own bone requires an inherited-parent entry.");
        for (int bone = 0; bone < parents.Length; bone++)
        {
            DevelopmentCheck.Require(parents[bone] >= -1 && parents[bone] < bone, "A bone's parent must precede it, or be -1 for a root.");
            DevelopmentCheck.Require(inheritedParents[bone] >= -1 && inheritedParents[bone] < inheritedBoneCount,
                "An inherited parent must reference the parent definition's model pose.");
            DevelopmentCheck.Require(inheritedParents[bone] < 0 || parents[bone] < 0,
                "An own bone has either an own parent or an inherited parent.");
        }
        foreach (short bone in jointBones)
            DevelopmentCheck.Require(bone >= 0 && bone < inheritedBoneCount + parents.Length, "Each source joint must reference a bone.");
    }
}
