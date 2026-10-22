using System.Numerics;

namespace SomeEngine.Animation.Pose;

/// <summary>The coordinates used by a bone target's position and orientation.</summary>
public enum BoneTargetSpace : byte
{
    /// <summary>Relative to the bone's parent.</summary>
    Local,
    /// <summary>In the skeleton's common model space.</summary>
    Model,
    /// <summary>In world space.</summary>
    World,
}

/// <summary>A bone goal consumed by pose solvers, with independent position and orientation weights.</summary>
public struct BoneTarget
{
    public int Bone;
    public Vector3 Position;
    public Quaternion Orientation;
    public BoneTargetSpace Space;
    public float PositionWeight;
    public float OrientationWeight;
}
