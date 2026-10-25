using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Relations;

namespace SomeEngine.Physics;

/// <summary>A point distance and its accumulated XPBD multiplier for the current substep.</summary>
[RelationSchema(RelationDirection.Undirected)]
public struct XpbdLength : IComponent
{
    public float RestLength;
    public float Compliance;
    public float Multiplier;
}

public readonly struct XpbdLengthProjection : IXpbdConstraint<XpbdLength>, IXpbdConstraintForces<XpbdLength>
{
    public static int GetBodyCount(in XpbdLength constraint, scoped QueryRow row, in PhysicsStepContext context) => 2;
    public static Entity GetBody(in XpbdLength constraint, scoped QueryRow row, int index, in PhysicsStepContext context)
    {
        ref readonly var endpoints = ref row.ReadRef<UndirectedRelationEndpoints<XpbdLength>>();
        return index == 0 ? endpoints.EndpointA : endpoints.EndpointB;
    }

    public static void GetBodyForce(in XpbdLength constraint, scoped QueryRow row, Entity body,
        scoped PhysicsBodies bodies, in PhysicsStepContext context, out Vector3 force, out Vector3 torque)
    {
        ref readonly var endpoints = ref row.ReadRef<UndirectedRelationEndpoints<XpbdLength>>();
        Vector3 difference = bodies.ReadPose(endpoints.EndpointB).Position - bodies.ReadPose(endpoints.EndpointA).Position;
        float duration = (float)context.Duration;
        Vector3 onSecond = Vector3.Normalize(difference) * (constraint.Multiplier / (duration * duration));
        force = torque = Vector3.Zero;
        if (body == endpoints.EndpointA) force -= onSecond;
        if (body == endpoints.EndpointB) force += onSecond;
    }

    public static void Initialize(in XpbdLength constraint, scoped QueryRow row, in PhysicsStepContext context) =>
        row.ReadWrite<XpbdLength>().Multiplier = 0;

    public static void Project(in XpbdLength constraint, scoped QueryRow row,
        scoped PhysicsBodies bodies, in PhysicsStepContext context)
    {
        float duration = (float)context.Duration;
        ref readonly var endpoints = ref row.ReadRef<UndirectedRelationEndpoints<XpbdLength>>();
        Vector3 difference = bodies.ReadPose(endpoints.EndpointB).Position - bodies.ReadPose(endpoints.EndpointA).Position;
        float distance = difference.Length();
        Vector3 gradient = difference / distance;
        ref readonly BodyMass firstMass = ref bodies.ReadMass(endpoints.EndpointA);
        ref readonly BodyMass secondMass = ref bodies.ReadMass(endpoints.EndpointB);
        float firstWeight = firstMass.Motion == BodyMotion.Dynamic ? firstMass.InverseMass : 0;
        float secondWeight = secondMass.Motion == BodyMotion.Dynamic ? secondMass.InverseMass : 0;
        float alpha = constraint.Compliance / (duration * duration);
        float change = (-(distance - constraint.RestLength) - alpha * constraint.Multiplier) /
            (firstWeight + secondWeight + alpha);
        row.ReadWrite<XpbdLength>().Multiplier += change;
        if (firstWeight > 0) bodies.WritePose(endpoints.EndpointA).Position -= firstWeight * gradient * change;
        if (secondWeight > 0) bodies.WritePose(endpoints.EndpointB).Position += secondWeight * gradient * change;
    }
}
