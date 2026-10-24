using System.Numerics;

namespace SomeEngine.Animation.Pose;

/// <summary>A bone's translation, rotation and scale relative to its parent.</summary>
public struct BoneTransform
{
    public Vector3 Translation;
    public Quaternion Rotation;
    public Vector3 Scale;

    /// <summary>Returns the local matrix using System.Numerics row-vector multiplication.</summary>
    public readonly Matrix4x4 ToMatrix()
        => Matrix4x4.CreateScale(Scale) * Matrix4x4.CreateFromQuaternion(Rotation)
            * Matrix4x4.CreateTranslation(Translation);
}
