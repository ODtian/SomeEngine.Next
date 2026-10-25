using System.Numerics;
using SomeEngine.ECS.Entities;

namespace SomeEngine.Physics;

internal static class ContactBodyMath
{
    internal static Entity GetBody(in ContactBodyLink link, in PhysicsStepContext context) =>
        context.World.GetDirectedRelationEndpoints(link.Participant).Target;

    internal static Entity GetBody(in ContactBodyLink link, scoped PhysicsBodies bodies, in PhysicsStepContext context) =>
        bodies.TryReadReference<SomeEngine.ECS.Relations.DirectedRelationEndpoints<ContactParticipant>>(
            link.Participant.Entity, out var endpoints) ? endpoints.Value.Target : GetBody(in link, in context);

    internal static bool IsFirstOccurrence(ReadOnlySpan<ContactBodyLink> links, int index, in PhysicsStepContext context)
    {
        Entity body = GetBody(in links[index], in context);
        for (int earlier = 0; earlier < index; earlier++)
            if (GetBody(in links[earlier], in context) == body) return false;
        return true;
    }

    internal static bool IsFirstOccurrence(ReadOnlySpan<ContactBodyLink> links, int index, scoped PhysicsBodies bodies,
        in PhysicsStepContext context)
    {
        Entity body = GetBody(in links[index], bodies, in context);
        for (int earlier = 0; earlier < index; earlier++)
            if (GetBody(in links[earlier], bodies, in context) == body) return false;
        return true;
    }

    internal static float GetSpeed(in ContactPoint point, ReadOnlySpan<ContactBodyLink> links,
        scoped PhysicsBodies bodies, in PhysicsStepContext context, bool stepInput)
    {
        float speed = 0;
        for (int index = 0; index < links.Length; index++)
        {
            if (!IsFirstOccurrence(links, index, bodies, in context)) continue;
            Entity body = GetBody(in links[index], bodies, in context);
            BodyVelocity velocity = bodies.GetRow(body).Read<BodyVelocity>();
            if (stepInput && bodies.ReadMass(body).Motion == BodyMotion.Kinematic)
            {
                ref readonly BodyStepState state = ref bodies.ReadStepState(body);
                velocity.Linear = (state.PredictedCenterOfMass - state.StartCenterOfMass) / (float)context.Duration;
                velocity.Angular = BodyRotation.Velocity(state.StartRotation, state.PredictedRotation, (float)context.Duration);
            }
            GetJacobian(in point, links, body, bodies, in context, point.Normal, out Vector3 linear, out Vector3 angular);
            speed += Vector3.Dot(linear, velocity.Linear) + Vector3.Dot(angular, velocity.Angular);
        }
        return speed;
    }

    internal static Vector3 GetDelta(in ContactPoint point, ReadOnlySpan<ContactBodyLink> links,
        scoped PhysicsBodies bodies, in PhysicsStepContext context, bool start)
    {
        Vector3 delta = default;
        for (int index = 0; index < links.Length; index++)
        {
            ref readonly ContactBodyLink link = ref links[index];
            Entity body = GetBody(in link, bodies, in context);
            BodyPose pose;
            if (start)
            {
                ref readonly BodyStepState state = ref bodies.ReadStepState(body);
                pose = new BodyPose { Rotation = state.StartRotation };
                pose.SetCenterOfMass(state.StartCenterOfMass, in bodies.ReadMass(body));
            }
            else pose = bodies.ReadPose(body);
            Vector3 position = pose.Position + Vector3.Transform(link.LocalPoint, pose.Rotation);
            delta += position * (link.Weight * (index < point.FirstBodyCount ? -1 : 1));
        }
        return delta;
    }

    internal static void GetJacobian(in ContactPoint point, ReadOnlySpan<ContactBodyLink> links, Entity body,
        scoped PhysicsBodies bodies, in PhysicsStepContext context, Vector3 direction, out Vector3 linear, out Vector3 angular)
    {
        linear = angular = Vector3.Zero;
        ref readonly BodyPose pose = ref bodies.ReadPose(body);
        Vector3 center = pose.GetCenterOfMass(in bodies.ReadMass(body));
        for (int index = 0; index < links.Length; index++)
        {
            ref readonly ContactBodyLink link = ref links[index];
            if (GetBody(in link, bodies, in context) != body) continue;
            float weight = link.Weight * (index < point.FirstBodyCount ? -1 : 1);
            Vector3 offset = pose.Position + Vector3.Transform(link.LocalPoint, pose.Rotation) - center;
            linear += direction * weight;
            angular += Vector3.Cross(offset, direction) * weight;
        }
    }
}
