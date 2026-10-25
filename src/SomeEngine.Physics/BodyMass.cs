using System.Numerics;
using SomeEngine.ECS;

namespace SomeEngine.Physics;

public enum BodyMotion : byte
{
    Static,
    Kinematic,
    Dynamic,
}

public struct BodyMass : IComponent
{
    public BodyMotion Motion;
    public float InverseMass;

    /// <summary>The full body-space inverse inertia tensor about the center of mass, in the upper 3×3.</summary>
    public Matrix4x4 InverseInertia;

    /// <summary>The center of mass relative to the body's local origin.</summary>
    public Vector3 CenterOfMass;

    public readonly Vector3 ApplyInverseInertia(Quaternion rotation, Vector3 torque)
    {
        Vector3 localTorque = Vector3.Transform(torque, Quaternion.Conjugate(rotation));
        return Vector3.Transform(Vector3.TransformNormal(localTorque, InverseInertia), rotation);
    }
}
