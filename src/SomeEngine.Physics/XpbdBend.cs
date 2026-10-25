using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;

namespace SomeEngine.Physics;

/// <summary>A minimum cosine between consecutive edges and its unilateral XPBD multiplier.</summary>
public struct XpbdBend : IComponent
{
    public Entity First;
    public Entity Middle;
    public Entity Last;
    public float MinimumCosine;
    public float Compliance;
    public float Multiplier;
}

public readonly struct XpbdBendProjection : IXpbdConstraint<XpbdBend>
{
    public static void Initialize(in XpbdBend constraint, scoped QueryRow row, in PhysicsStepContext context) =>
        row.ReadWrite<XpbdBend>().Multiplier = 0;

    public static void Project(in XpbdBend constraint, scoped QueryRow row,
        scoped PhysicsBodies bodies, in PhysicsStepContext context)
    {
        float duration = (float)context.Duration;
        Vector3 firstEdge = bodies.ReadPose(constraint.Middle).Position - bodies.ReadPose(constraint.First).Position;
        Vector3 secondEdge = bodies.ReadPose(constraint.Last).Position - bodies.ReadPose(constraint.Middle).Position;
        float firstLength = firstEdge.Length();
        float secondLength = secondEdge.Length();
        Vector3 firstDirection = firstEdge / firstLength;
        Vector3 secondDirection = secondEdge / secondLength;
        float cosine = Vector3.Dot(firstDirection, secondDirection);
        float residual = cosine - constraint.MinimumCosine;
        if (residual >= 0 && constraint.Multiplier == 0) return;

        Vector3 firstDerivative = (secondDirection - cosine * firstDirection) / firstLength;
        Vector3 secondDerivative = (firstDirection - cosine * secondDirection) / secondLength;
        Vector3 firstGradient = -firstDerivative;
        Vector3 middleGradient = firstDerivative - secondDerivative;
        Vector3 lastGradient = secondDerivative;
        ref readonly BodyMass firstMass = ref bodies.ReadMass(constraint.First);
        ref readonly BodyMass middleMass = ref bodies.ReadMass(constraint.Middle);
        ref readonly BodyMass lastMass = ref bodies.ReadMass(constraint.Last);
        float firstWeight = firstMass.Motion == BodyMotion.Dynamic ? firstMass.InverseMass : 0;
        float middleWeight = middleMass.Motion == BodyMotion.Dynamic ? middleMass.InverseMass : 0;
        float lastWeight = lastMass.Motion == BodyMotion.Dynamic ? lastMass.InverseMass : 0;
        float alpha = constraint.Compliance / (duration * duration);
        float change = (-residual - alpha * constraint.Multiplier) /
            (firstWeight * firstGradient.LengthSquared() + middleWeight * middleGradient.LengthSquared() +
                lastWeight * lastGradient.LengthSquared() + alpha);
        change = MathF.Max(0, constraint.Multiplier + change) - constraint.Multiplier;
        row.ReadWrite<XpbdBend>().Multiplier += change;
        if (firstWeight > 0) bodies.WritePose(constraint.First).Position += firstWeight * firstGradient * change;
        if (middleWeight > 0) bodies.WritePose(constraint.Middle).Position += middleWeight * middleGradient * change;
        if (lastWeight > 0) bodies.WritePose(constraint.Last).Position += lastWeight * lastGradient * change;
    }
}
