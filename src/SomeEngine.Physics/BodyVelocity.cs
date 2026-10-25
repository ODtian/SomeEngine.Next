using System.Numerics;
using SomeEngine.ECS;

namespace SomeEngine.Physics;

/// <summary>World-space center-of-mass velocity in m/s and angular velocity in rad/s.</summary>
public struct BodyVelocity : IComponent
{
    public Vector3 Linear;
    public Vector3 Angular;
}
