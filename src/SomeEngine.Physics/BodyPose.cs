using System.Numerics;
using SomeEngine.ECS;

namespace SomeEngine.Physics;

/// <summary>The world position of the body's local origin and its body-to-world rotation.</summary>
public struct BodyPose : IComponent
{
    public Vector3 Position;
    public Quaternion Rotation;

    public readonly Vector3 GetCenterOfMass(in BodyMass mass) =>
        Position + Vector3.Transform(mass.CenterOfMass, Rotation);

    public void SetCenterOfMass(Vector3 position, in BodyMass mass) =>
        Position = position - Vector3.Transform(mass.CenterOfMass, Rotation);
}
