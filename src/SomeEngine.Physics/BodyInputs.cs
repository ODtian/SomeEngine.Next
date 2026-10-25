using System.Numerics;
using SomeEngine.ECS.Components;

namespace SomeEngine.Physics;

/// <summary>A world-space force and torque about the center of mass, active over [StartTime, EndTime).</summary>
public struct BodyForce : IBufferElement
{
    public Vector3 Force;
    public Vector3 Torque;
    public double StartTime;
    public double EndTime;
}

/// <summary>A world-space linear and angular impulse applied in the substep containing Time.</summary>
public struct BodyImpulse : IBufferElement
{
    public Vector3 Linear;
    public Vector3 Angular;
    public double Time;
}

/// <summary>Assigns world-space center-of-mass and angular velocities in the substep containing Time.</summary>
public struct BodyVelocityTarget : IBufferElement
{
    public Vector3 Linear;
    public Vector3 Angular;
    public double Time;
}

/// <summary>A world-space pose trajectory, sampled at each substep end and held at its final pose.</summary>
public struct BodyKinematicTarget : IBufferElement
{
    public BodyPose StartPose;
    public BodyPose EndPose;
    public double StartTime;
    public double EndTime;
}
