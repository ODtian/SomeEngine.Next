using System.Numerics;

namespace SomeEngine.Animation.Pose;

/// <summary>Source skin joints resolved to a skeleton instance's producing model-pose sections.</summary>
public sealed class SkinBindingInstance
{
    private readonly BonePose[] _joints;
    public SkinBinding Binding { get; }
    public SkeletonInstance SkeletonInstance { get; }
    public int JointCount => _joints.Length;

    internal SkinBindingInstance(SkinBinding binding, SkeletonInstance instance)
    {
        Binding = binding;
        SkeletonInstance = instance;
        _joints = new BonePose[binding.JointCount];
        for (int joint = 0; joint < _joints.Length; joint++)
            _joints[joint] = instance.ModelPose.GetBone(binding.JointBones[joint]);
    }

    public BonePose GetJointPose(int joint) => _joints[joint];
    public Matrix4x4 GetModelTransform(int joint) => _joints[joint].Transform;
    public Matrix4x4 GetSkinTransform(int joint) => Binding.InverseBindMatrices[joint] * _joints[joint].Transform;
}
