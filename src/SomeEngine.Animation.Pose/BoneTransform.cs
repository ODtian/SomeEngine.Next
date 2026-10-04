using System.Numerics;

namespace SomeEngine.Animation.Pose;

/// <summary>A bone's translation, rotation and scale relative to its parent.</summary>
public struct BoneTransform
{
    public Vector3 Translation;
    public Quaternion Rotation;
    public Vector3 Scale;
}
