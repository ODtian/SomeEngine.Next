using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Relations;

namespace SomeEngine.Physics;

/// <summary>A point distance and its accumulated XPBD multiplier for the current substep.</summary>
[RelationSchema(RelationDirection.Undirected, RelationCardinality.Parallel)]
public struct XpbdLength : ITrackedComponent
{
    public float RestLength;
    public float Compliance;
    public float Multiplier;
}

public readonly partial struct XpbdLengthProjection
{
    public static PhysicsConstraintError GetActivityError(in XpbdLength constraint, in RelationEndpoints<XpbdLength> endpoints,
        scoped PhysicsBodies bodies, in PhysicsStepContext context)
    {
        float distance = Vector3.Distance(bodies.ReadPose(endpoints.Source).Position, bodies.ReadPose(endpoints.Target).Position);
        return new(MathF.Abs(distance - constraint.RestLength), 0);
    }
    public static int GetBodyCount(in XpbdLength constraint, in RelationEndpoints<XpbdLength> endpoints, in PhysicsStepContext context) => 2;
    public static Entity GetBody(in XpbdLength constraint, in RelationEndpoints<XpbdLength> endpoints, int index, in PhysicsStepContext context)
    {
        return index == 0 ? endpoints.Source : endpoints.Target;
    }

    public static void GetBodyForce(in XpbdLength constraint, in RelationEndpoints<XpbdLength> endpoints, Entity body,
        scoped PhysicsBodies bodies, in PhysicsStepContext context, out Vector3 force, out Vector3 torque)
    {
        Vector3 difference = bodies.ReadPose(endpoints.Target).Position - bodies.ReadPose(endpoints.Source).Position;
        float duration = (float)context.Duration;
        Vector3 onSecond = Vector3.Normalize(difference) * (constraint.Multiplier / (duration * duration));
        force = torque = Vector3.Zero;
        if (body == endpoints.Source) force -= onSecond;
        if (body == endpoints.Target) force += onSecond;
    }

    public static void Initialize(ref XpbdLength constraint, in RelationEndpoints<XpbdLength> endpoints, in PhysicsStepContext context) =>
        constraint.Multiplier = 0;

    public static void Project(ref XpbdLength constraint, in RelationEndpoints<XpbdLength> endpoints,
        scoped PhysicsBodies bodies, in PhysicsStepContext context)
    {
        float duration = (float)context.Duration;
        Vector3 difference = bodies.ReadPose(endpoints.Target).Position - bodies.ReadPose(endpoints.Source).Position;
        float distance = difference.Length();
        Vector3 gradient = difference / distance;
        ref readonly BodyMass firstMass = ref bodies.ReadMass(endpoints.Source);
        ref readonly BodyMass secondMass = ref bodies.ReadMass(endpoints.Target);
        float firstWeight = firstMass.Motion == BodyMotion.Dynamic ? firstMass.InverseMass : 0;
        float secondWeight = secondMass.Motion == BodyMotion.Dynamic ? secondMass.InverseMass : 0;
        float alpha = constraint.Compliance / (duration * duration);
        float change = (-(distance - constraint.RestLength) - alpha * constraint.Multiplier) /
            (firstWeight + secondWeight + alpha);
        constraint.Multiplier += change;
        if (firstWeight > 0) bodies.WritePose(endpoints.Source).Position -= firstWeight * gradient * change;
        if (secondWeight > 0) bodies.WritePose(endpoints.Target).Position += secondWeight * gradient * change;
    }
}
