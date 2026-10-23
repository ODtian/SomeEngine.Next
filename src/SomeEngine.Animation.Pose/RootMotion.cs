using System.Numerics;

namespace SomeEngine.Animation.Pose;

public enum RootMotionMode : byte { SourcePose, Extract }

/// <summary>Current-frame movement. Multiply Transform before the actor's preceding model transform.</summary>
public readonly record struct RootMotionDelta(Matrix4x4 Transform)
{
    public static RootMotionDelta Identity => new(Matrix4x4.Identity);
    public Vector3 Translation => Transform.Translation;
    public Quaternion Rotation => Quaternion.CreateFromRotationMatrix(Transform);
}

internal readonly record struct ClipRootMotion(Matrix4x4 InverseInitial, Matrix4x4 Cycle)
{
    internal static ClipRootMotion Create(AnimationClip clip)
    {
        var sample = new BoneTransform[clip.Skeleton.BoneCount];
        clip.SampleLocal(0, sample);
        Matrix4x4.Invert(RigidModel(clip, sample), out Matrix4x4 inverseInitial);
        clip.SampleLocal(clip.Duration, sample);
        return new(inverseInitial, inverseInitial * RigidModel(clip, sample));
    }

    internal RootMotionDelta Extract(in ClipParameters parameters, ref ClipInstance instance, float elapsed)
    {
        Matrix4x4 phase = InverseInitial * RigidModel(parameters.Clip, instance.Samples);
        int cycles = parameters.Loop ? (int)(elapsed / parameters.Clip.Duration) : 0;
        Matrix4x4 motion = phase * Power(Cycle, cycles);
        Matrix4x4.Invert(instance.PreviousMotion, out Matrix4x4 inversePrevious);
        var delta = new RootMotionDelta(motion * inversePrevious);
        instance.PreviousMotion = motion;

        Matrix4x4.Invert(phase, out Matrix4x4 inversePhase);
        Quaternion rotation = Quaternion.CreateFromRotationMatrix(inversePhase);
        ReadOnlySpan<short> parents = parameters.Clip.Skeleton.Parents;
        for (int bone = 0; bone < parents.Length; bone++)
            if (parents[bone] < 0)
            {
                ref BoneTransform root = ref instance.Samples[bone];
                root.Translation = Vector3.Transform(root.Translation, inversePhase);
                root.Rotation = Quaternion.Concatenate(root.Rotation, rotation);
            }
        return delta;
    }

    private static Matrix4x4 RigidModel(AnimationClip clip, ReadOnlySpan<BoneTransform> pose)
    {
        int bone = clip.RootMotionBone;
        Matrix4x4 model = pose[bone].ToMatrix();
        for (int parent = clip.Skeleton.Parents[bone]; parent >= 0; parent = clip.Skeleton.Parents[parent])
            model *= pose[parent].ToMatrix();
        Matrix4x4.Decompose(model, out _, out Quaternion rotation, out Vector3 translation);
        return Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(translation);
    }

    private static Matrix4x4 Power(Matrix4x4 value, int count)
    {
        Matrix4x4 result = Matrix4x4.Identity;
        while (count > 0)
        {
            if ((count & 1) != 0) result *= value;
            value *= value;
            count >>= 1;
        }
        return result;
    }
}
