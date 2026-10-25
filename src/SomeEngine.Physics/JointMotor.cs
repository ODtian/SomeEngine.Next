using System.Numerics;
using SomeEngine.ECS;

namespace SomeEngine.Physics;

public enum JointMotorSpace : byte
{
    /// <summary>Coordinates relative to the current source joint frame.</summary>
    Source,
    /// <summary>Coordinates in the fixed world frame.</summary>
    World,
}

public enum JointMotorResponse : byte
{
    /// <summary>Applies the drive to the relation's target body.</summary>
    Target,
    /// <summary>Also applies the opposite reaction to the relation's source body.</summary>
    Both,
}

/// <summary>Spring and damping about a timed target on the same JointConstraint relation.</summary>
public struct JointMotor : IComponent
{
    public JointMotorSpace Space;
    public JointMotorResponse Response;
    /// <summary>Per-axis spring coefficients in N/m.</summary>
    public Vector3 LinearStiffness;
    /// <summary>Per-axis damping coefficients in N·s/m.</summary>
    public Vector3 LinearDamping;
    /// <summary>Per-axis generalized force limits in N.</summary>
    public Vector3 MaximumForce;
    /// <summary>Per-axis rotation-vector spring coefficients in N·m/rad.</summary>
    public Vector3 AngularStiffness;
    /// <summary>Per-axis rotation-vector damping coefficients in N·m·s/rad.</summary>
    public Vector3 AngularDamping;
    /// <summary>Per-axis generalized torque limits in N·m.</summary>
    public Vector3 MaximumTorque;
}

/// <summary>A frame and its velocities at Time, expressed in the motor's selected reference space.</summary>
public struct JointMotorTarget : IComponent
{
    public JointFrame Frame;
    /// <summary>Frame-origin velocity in m/s.</summary>
    public Vector3 LinearVelocity;
    /// <summary>Spatial angular velocity in rad/s, in the selected reference coordinates.</summary>
    public Vector3 AngularVelocity;
    public double Time;
}

public struct AvbdMotorState : IComponent
{
    /// <summary>Constraint forces in N.</summary>
    public Vector3 LinearMultiplier;
    public Vector3 LinearPenalty;
    /// <summary>Constraint torques in N·m.</summary>
    public Vector3 AngularMultiplier;
    public Vector3 AngularPenalty;
}

public struct XpbdMotorState : IComponent
{
    /// <summary>Displacement multipliers in N·s²; divide by substep duration for impulses.</summary>
    public Vector3 LinearMultiplier;
    /// <summary>Angular displacement multipliers in N·m·s².</summary>
    public Vector3 AngularMultiplier;
}
