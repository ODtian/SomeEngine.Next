using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;

namespace SomeEngine.Physics;

/// <summary>A minimum cosine between consecutive edges and its unilateral XPBD multiplier.</summary>
public struct XpbdBend : ITrackedComponent
{
    public float MinimumCosine;
    public float Compliance;
    public float Multiplier;
}

public readonly partial struct XpbdBendProjection
{
    public static PhysicsConstraintError GetActivityError(in XpbdBend constraint, ReadOnlySpan<Entity> links,
        scoped PhysicsBodies bodies, in PhysicsStepContext context)
    {
        ReadBodies(links, bodies, in context, out Entity first, out Entity middle, out Entity last);
        float cosine = GetGradients(first, middle, last, bodies, out _, out _, out _);
        return new(0, MathF.Max(0, MathF.Acos(Math.Clamp(cosine, -1, 1)) - MathF.Acos(constraint.MinimumCosine)));
    }

    public static int GetBodyCount(in XpbdBend constraint, ReadOnlySpan<Entity> links, in PhysicsStepContext context) => 3;
    public static Entity GetBody(in XpbdBend constraint, ReadOnlySpan<Entity> links, int index, in PhysicsStepContext context) =>
        links[index];
    public static int GetReadEntityCount(in XpbdBend constraint, ReadOnlySpan<Entity> links, in PhysicsStepContext context) => 3;
    public static Entity GetReadEntity(in XpbdBend constraint, ReadOnlySpan<Entity> links, int index, in PhysicsStepContext context) =>
        links[index];

    public static void GetBodyForce(in XpbdBend constraint, ReadOnlySpan<Entity> links, Entity body,
        scoped PhysicsBodies bodies, in PhysicsStepContext context, out Vector3 force, out Vector3 torque)
    {
        ReadBodies(links, bodies, in context, out Entity firstBody, out Entity middleBody, out Entity lastBody);
        GetGradients(firstBody, middleBody, lastBody, bodies, out Vector3 first, out Vector3 middle, out Vector3 last);
        Vector3 gradient = default;
        if (body == firstBody) gradient += first;
        if (body == middleBody) gradient += middle;
        if (body == lastBody) gradient += last;
        float duration = (float)context.Duration;
        force = gradient * (constraint.Multiplier / (duration * duration));
        torque = Vector3.Zero;
    }

    public static void Initialize(ref XpbdBend constraint, ReadOnlySpan<Entity> links, in PhysicsStepContext context) =>
        constraint.Multiplier = 0;

    public static void Project(ref XpbdBend constraint, ReadOnlySpan<Entity> links,
        scoped PhysicsBodies bodies, in PhysicsStepContext context)
    {
        float duration = (float)context.Duration;
        ReadBodies(links, bodies, in context, out Entity firstBody, out Entity middleBody, out Entity lastBody);
        float residual = GetGradients(firstBody, middleBody, lastBody, bodies, out Vector3 firstGradient,
            out Vector3 middleGradient, out Vector3 lastGradient) - constraint.MinimumCosine;
        if (residual >= 0 && constraint.Multiplier == 0) return;
        ref readonly BodyMass firstMass = ref bodies.ReadMass(firstBody);
        ref readonly BodyMass middleMass = ref bodies.ReadMass(middleBody);
        ref readonly BodyMass lastMass = ref bodies.ReadMass(lastBody);
        float firstWeight = firstMass.Motion == BodyMotion.Dynamic ? firstMass.InverseMass : 0;
        float middleWeight = middleMass.Motion == BodyMotion.Dynamic ? middleMass.InverseMass : 0;
        float lastWeight = lastMass.Motion == BodyMotion.Dynamic ? lastMass.InverseMass : 0;
        float alpha = constraint.Compliance / (duration * duration);
        float change = (-residual - alpha * constraint.Multiplier) /
            (firstWeight * firstGradient.LengthSquared() + middleWeight * middleGradient.LengthSquared() +
                lastWeight * lastGradient.LengthSquared() + alpha);
        change = MathF.Max(0, constraint.Multiplier + change) - constraint.Multiplier;
        constraint.Multiplier += change;
        if (firstWeight > 0) bodies.WritePose(firstBody).Position += firstWeight * firstGradient * change;
        if (middleWeight > 0) bodies.WritePose(middleBody).Position += middleWeight * middleGradient * change;
        if (lastWeight > 0) bodies.WritePose(lastBody).Position += lastWeight * lastGradient * change;
    }

    private static void ReadBodies(ReadOnlySpan<Entity> links, scoped PhysicsBodies bodies, in PhysicsStepContext context,
        out Entity first, out Entity middle, out Entity last)
    {
        first = links[0];
        middle = links[1];
        last = links[2];
    }

    private static float GetGradients(Entity firstBody, Entity middleBody, Entity lastBody, scoped PhysicsBodies bodies,
        out Vector3 first, out Vector3 middle, out Vector3 last)
    {
        Vector3 firstEdge = bodies.ReadPose(middleBody).Position - bodies.ReadPose(firstBody).Position;
        Vector3 secondEdge = bodies.ReadPose(lastBody).Position - bodies.ReadPose(middleBody).Position;
        float firstLength = firstEdge.Length();
        float secondLength = secondEdge.Length();
        Vector3 firstDirection = firstEdge / firstLength;
        Vector3 secondDirection = secondEdge / secondLength;
        float cosine = Vector3.Dot(firstDirection, secondDirection);
        Vector3 firstDerivative = (secondDirection - cosine * firstDirection) / firstLength;
        Vector3 secondDerivative = (firstDirection - cosine * secondDirection) / secondLength;
        first = -firstDerivative;
        middle = firstDerivative - secondDerivative;
        last = secondDerivative;
        return cosine;
    }
}
