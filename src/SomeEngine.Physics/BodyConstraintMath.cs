using System.Numerics;
using SomeEngine.ECS.Entities;

namespace SomeEngine.Physics;

internal static class BodyConstraintMath
{
    internal static float GetWeight(in BodyMass mass, in BodyPose pose, Vector3 linear, Vector3 angular) =>
        mass.Motion == BodyMotion.Dynamic ? mass.InverseMass * linear.LengthSquared()
            + Vector3.Dot(angular, mass.ApplyInverseInertia(pose.Rotation, angular)) : 0;

    internal static void ApplyCorrection(scoped PhysicsBodies bodies, Entity body, in BodyMass mass,
        Vector3 linear, Vector3 angular, float change)
    {
        if (mass.Motion != BodyMotion.Dynamic) return;
        ref BodyPose pose = ref bodies.WritePose(body);
        Vector3 center = pose.GetCenterOfMass(in mass) + linear * (mass.InverseMass * change);
        Vector3 rotation = mass.ApplyInverseInertia(pose.Rotation, angular) * change;
        pose.Rotation = BodyRotation.ApplyChange(pose.Rotation, rotation);
        pose.SetCenterOfMass(center, in mass);
    }
}
