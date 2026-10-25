using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;

namespace SomeEngine.Physics;

/// <summary>A point distance and its accumulated XPBD multiplier for the current substep.</summary>
public struct XpbdLength : IComponent
{
    public Entity First;
    public Entity Second;
    public float RestLength;
    public float Compliance;
    public float Multiplier;
}

public readonly struct XpbdLengthProjection : IXpbdConstraint<XpbdLength>
{
    public static void Initialize(in XpbdLength constraint, scoped QueryRow row) =>
        row.ReadWrite<XpbdLength>().Multiplier = 0;

    public static void Project(in XpbdLength constraint, scoped QueryRow row,
        scoped PhysicsBodies bodies, float duration)
    {
        Vector3 difference = bodies.ReadPose(constraint.Second).Position - bodies.ReadPose(constraint.First).Position;
        float distance = difference.Length();
        Vector3 gradient = difference / distance;
        ref readonly BodyMass firstMass = ref bodies.ReadMass(constraint.First);
        ref readonly BodyMass secondMass = ref bodies.ReadMass(constraint.Second);
        float firstWeight = firstMass.Motion == BodyMotion.Dynamic ? firstMass.InverseMass : 0;
        float secondWeight = secondMass.Motion == BodyMotion.Dynamic ? secondMass.InverseMass : 0;
        float alpha = constraint.Compliance / (duration * duration);
        float change = (-(distance - constraint.RestLength) - alpha * constraint.Multiplier) /
            (firstWeight + secondWeight + alpha);
        row.ReadWrite<XpbdLength>().Multiplier += change;
        if (firstWeight > 0) bodies.WritePose(constraint.First).Position -= firstWeight * gradient * change;
        if (secondWeight > 0) bodies.WritePose(constraint.Second).Position += secondWeight * gradient * change;
    }
}
