using System.Numerics;
using SomeEngine.Core.Diagnostics;
using SomeEngine.Serialization;

namespace SomeEngine.Animation.Pose;

/// <summary>Ready, immutable inverse bind matrices and bone indices in source joint order.</summary>
[AssetUse]
public sealed class SkinBinding
{
    private readonly Matrix4x4[] _inverseBindMatrices;
    private readonly short[] _jointBones;

    public int JointCount => _jointBones.Length;
    public ReadOnlySpan<Matrix4x4> InverseBindMatrices => _inverseBindMatrices;
    public ReadOnlySpan<short> JointBones => _jointBones;

    /// <summary>Resolves source joints to the instance's shared model-pose sections.</summary>
    public SkinBindingInstance Bind(SkeletonInstance instance) => new(this, instance);

    public SkinBinding(ReadOnlySpan<Matrix4x4> inverseBindMatrices, ReadOnlySpan<short> jointBones)
        : this(inverseBindMatrices.ToArray(), jointBones.ToArray()) { }

    internal SkinBinding(Matrix4x4[] inverseBindMatrices, short[] jointBones)
    {
        DevelopmentCheck.Require(inverseBindMatrices.Length == jointBones.Length,
            "Each source joint requires an inverse bind matrix and a bone index.");
        _inverseBindMatrices = inverseBindMatrices;
        _jointBones = jointBones;
    }
}
