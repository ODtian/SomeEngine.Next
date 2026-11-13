using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;

namespace SomeEngine.Physics;

/// <summary>XPBD position multipliers accumulated within one substep.</summary>
public struct XpbdContactState
{
    public float NormalMultiplier;
    public Vector3 TangentMultiplier;
}

/// <summary>Projects the shared contact gap through its combined body Jacobians.</summary>
public readonly struct XpbdContactProjection
{
    public static void GetBodyForce(in ContactPoint point, ReadOnlySpan<ContactBodyLink> links, in ContactMaterial material, ref XpbdContactState state, Entity body,
        scoped PhysicsBodies bodies, in PhysicsStepContext context, out Vector3 force, out Vector3 torque)
    {
        float duration = (float)context.Duration;
        Vector3 onSecond = (point.Normal * state.NormalMultiplier + state.TangentMultiplier) / (duration * duration);
        ContactBodyMath.GetJacobian(in point, links, body, bodies, in context, onSecond, out force, out torque);
        for (int index = 0; index < links.Length; index++)
        {
            ref readonly ContactBodyLink link = ref links[index];
            if (link.NormalReference == ContactNormalReference.MaterialPoint
                || ContactBodyMath.GetBody(in link, bodies, in context) != body) continue;
            Vector3 offset = Vector3.Transform(link.NormalLocalPoint - link.LocalPoint, bodies.ReadPose(body).Rotation);
            float weight = link.Weight * (index < point.FirstBodyCount ? -1 : 1);
            torque += Vector3.Cross(offset, point.Normal * (state.NormalMultiplier / (duration * duration))) * weight;
        }
    }

    public static void Initialize(in ContactPoint point, ReadOnlySpan<ContactBodyLink> links, in ContactMaterial material, ref XpbdContactState state, in PhysicsStepContext context) =>
        state = default;

    public static void Project(in ContactPoint point, ReadOnlySpan<ContactBodyLink> links, in ContactMaterial material, ref XpbdContactState state,
        scoped PhysicsBodies bodies, in PhysicsStepContext context, ref ContactResponse response)
    {
        Vector3 start = ContactBodyMath.GetDelta(in point, links, bodies, in context, true);
        float gap = point.Separation + Vector3.Dot(point.Normal,
            ContactBodyMath.GetDelta(in point, links, bodies, in context, false, normal: true) - point.BodyAnchorDelta);
        float startGap = point.Separation + Vector3.Dot(point.Normal,
            ContactBodyMath.GetDelta(in point, links, bodies, in context, true, normal: true) - point.BodyAnchorDelta);
        float duration = (float)context.Duration;
        float damping = gap < MathF.Min(startGap, 0) ? material.NormalDamping : 0;
        var terms = new XpbdMaterialTerms(material.NormalStiffness, damping, duration);
        float weight = GetWeight(in point, links, bodies, in context, point.Normal, normal: true);
        float multiplier = MathF.Max(0, terms.Project(gap, startGap, weight, state.NormalMultiplier));
        ApplyCorrection(in point, links, bodies, in context, point.Normal, multiplier - state.NormalMultiplier, normal: true);
        state.NormalMultiplier = multiplier;
        ProjectFriction(in point, links, bodies, in context, start, in material, ref state);
        response.ForceOnSecond = (point.Normal * multiplier + state.TangentMultiplier) / (duration * duration);
    }

    private static void ProjectFriction(in ContactPoint point, ReadOnlySpan<ContactBodyLink> links,
        scoped PhysicsBodies bodies, in PhysicsStepContext context, Vector3 start, in ContactMaterial material, ref XpbdContactState state)
    {
        Vector3 slip = ContactBodyMath.GetDelta(in point, links, bodies, in context, false) - start;
        slip -= point.Normal * Vector3.Dot(point.Normal, slip);
        Vector3 trial = state.TangentMultiplier;
        float travel = slip.Length();
        if (travel > 0)
        {
            Vector3 direction = slip / travel;
            float weight = GetWeight(in point, links, bodies, in context, direction);
            if (weight > 0) trial -= direction * (travel / weight);
        }
        trial -= point.Normal * Vector3.Dot(point.Normal, trial);
        float magnitude = trial.Length();
        if (magnitude > material.StaticFriction * state.NormalMultiplier)
            trial *= material.DynamicFriction * state.NormalMultiplier / magnitude;
        Vector3 change = trial - state.TangentMultiplier;
        float length = change.Length();
        if (length > 0) ApplyCorrection(in point, links, bodies, in context, change / length, length);
        state.TangentMultiplier = trial;
    }

    private static float GetWeight(in ContactPoint point, ReadOnlySpan<ContactBodyLink> links,
        scoped PhysicsBodies bodies, in PhysicsStepContext context, Vector3 direction, bool normal = false)
    {
        float weight = 0;
        for (int index = 0; index < links.Length; index++)
        {
            if (!ContactBodyMath.IsFirstOccurrence(links, index, bodies, in context)) continue;
            Entity body = ContactBodyMath.GetBody(in links[index], bodies, in context);
            ContactBodyMath.GetJacobian(in point, links, body, bodies, in context, direction, out Vector3 linear, out Vector3 angular, normal);
            weight += BodyConstraintMath.GetWeight(in bodies.ReadMass(body), in bodies.ReadPose(body), linear, angular);
        }
        return weight;
    }

    private static void ApplyCorrection(in ContactPoint point, ReadOnlySpan<ContactBodyLink> links,
        scoped PhysicsBodies bodies, in PhysicsStepContext context, Vector3 direction, float change, bool normal = false)
    {
        for (int index = 0; index < links.Length; index++)
        {
            if (!ContactBodyMath.IsFirstOccurrence(links, index, bodies, in context)) continue;
            Entity body = ContactBodyMath.GetBody(in links[index], bodies, in context);
            ContactBodyMath.GetJacobian(in point, links, body, bodies, in context, direction, out Vector3 linear, out Vector3 angular, normal);
            BodyConstraintMath.ApplyCorrection(bodies, body, in bodies.ReadMass(body), linear, angular, change);
        }
    }
}
