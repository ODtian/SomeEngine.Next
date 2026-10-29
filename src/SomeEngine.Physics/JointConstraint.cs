using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Relations;

namespace SomeEngine.Physics;

public readonly record struct JointFrame(Vector3 Position, Quaternion Rotation)
{
    public static JointFrame Identity => new(Vector3.Zero, Quaternion.Identity);
}

public enum JointAxisMotion : byte
{
    Free,
    Locked,
    Limited,
}

/// <summary>Linear bounds use meters; angular bounds use relative rotation-vector components in radians.</summary>
public readonly record struct JointAxisLimit(JointAxisMotion Motion, float Minimum, float Maximum)
{
    public static JointAxisLimit Free => default;
    public static JointAxisLimit Locked => new(JointAxisMotion.Locked, 0, 0);
}

/// <summary>Local source/target frames and their relative coordinates; ECS holds the directed endpoints.</summary>
[RelationSchema(RelationDirection.Directed)]
public struct JointConstraint : IComponent
{
    public JointFrame SourceFrame;
    public JointFrame TargetFrame;
    public JointAxisLimit LinearX;
    public JointAxisLimit LinearY;
    public JointAxisLimit LinearZ;
    public JointAxisLimit AngularX;
    public JointAxisLimit AngularY;
    public JointAxisLimit AngularZ;
    public Vector3 LinearStiffness;
    public Vector3 AngularStiffness;
}

public struct AvbdJointState : IComponent
{
    public Vector3 LinearMultiplier;
    public Vector3 AngularMultiplier;
    public Vector3 LinearPenalty;
    public Vector3 AngularPenalty;
    public Vector3 InitialLinearError;
    public Vector3 InitialAngularError;
}

public struct XpbdJointState : IComponent
{
    public Vector3 LinearMultiplier;
    public Vector3 AngularMultiplier;
}

public static class JointConstraints
{
    /// <summary>Leaves translation along frame X free while aligning the other coordinates.</summary>
    public static JointConstraint CreateSlider(JointFrame sourceFrame, JointFrame targetFrame,
        float linearStiffness, float angularStiffness)
    {
        JointConstraint joint = CreateFixed(sourceFrame, targetFrame, linearStiffness, angularStiffness);
        joint.LinearX = JointAxisLimit.Free;
        return joint;
    }

    /// <summary>Locks the frame origins and their Y/Z rotation components, leaving rotation about frame X free.</summary>
    public static JointConstraint CreateHinge(JointFrame sourceFrame, JointFrame targetFrame,
        float linearStiffness, float angularStiffness)
    {
        JointConstraint joint = CreateFixed(sourceFrame, targetFrame, linearStiffness, angularStiffness);
        joint.AngularX = JointAxisLimit.Free;
        return joint;
    }

    public static JointConstraint CreateBall(JointFrame sourceFrame, JointFrame targetFrame, float linearStiffness)
    {
        JointConstraint joint = CreateFixed(sourceFrame, targetFrame, linearStiffness, 0);
        joint.AngularX = joint.AngularY = joint.AngularZ = JointAxisLimit.Free;
        return joint;
    }

    public static JointConstraint CreateFixed(JointFrame sourceFrame, JointFrame targetFrame,
        float linearStiffness, float angularStiffness) => new()
    {
        SourceFrame = sourceFrame,
        TargetFrame = targetFrame,
        LinearX = JointAxisLimit.Locked,
        LinearY = JointAxisLimit.Locked,
        LinearZ = JointAxisLimit.Locked,
        AngularX = JointAxisLimit.Locked,
        AngularY = JointAxisLimit.Locked,
        AngularZ = JointAxisLimit.Locked,
        LinearStiffness = new(linearStiffness),
        AngularStiffness = new(angularStiffness),
    };
}
