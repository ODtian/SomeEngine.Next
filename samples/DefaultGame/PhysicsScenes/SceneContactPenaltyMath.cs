// Newton v1.7.0 contact conditioning and AVBD dual penalty ascent.
// Original project source: codex/job331-380, Native/AvbdContact.cpp and Native/LongRange.cpp.
// Copyright (c) 2025 The Newton Developers. Apache-2.0; see ThirdParty/Newton/NOTICE.
using System.Numerics;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Relations;
using SomeEngine.Physics;

namespace DefaultGame;

/// <summary>The project's adaptive penalty metric around its selected ordinary or supported contact equations.</summary>
internal readonly struct SceneContactPenaltyMath : IAvbdContactMath<AvbdContactState>
{
    public static void Initialize(in ContactPoint point, ReadOnlySpan<ContactBodyLink> links, in ContactMaterial material,
        ref AvbdContactState state, scoped PhysicsBodies bodies, in PhysicsStepContext context, float retention)
    {
        AvbdContactMath.Initialize(in point, links, in material, ref state, bodies, in context, retention);
        SceneContactPenalty.Initialize(in point, links, ref state, bodies, in context, retention);
    }
    public static void AccumulateBodyEquation(in ContactPoint point, ReadOnlySpan<ContactBodyLink> links, in ContactMaterial material,
        in AvbdContactState state, Entity body, scoped PhysicsBodies bodies, in PhysicsStepContext context,
        float initialErrorFraction, ref AvbdBodyEquation equation) =>
        AvbdContactMath.AccumulateBodyEquation(in point, links, in material, in state, body, bodies, in context, initialErrorFraction, ref equation);
    public static void UpdateDuals(in ContactPoint point, ReadOnlySpan<ContactBodyLink> links, in ContactMaterial material,
        ref AvbdContactState state, scoped PhysicsBodies bodies, in PhysicsStepContext context,
        float initialErrorFraction, ref ContactResponse response)
    {
        AvbdContactMath.UpdateDuals(in point, links, in material, ref state, bodies, in context, initialErrorFraction, ref response);
        SceneContactPenalty.Update(in point, links, ref state, bodies, in context);
    }
}

internal readonly struct SceneSupportedContactPenaltyMath : IAvbdContactMath<AvbdSupportedContactState>
{
    public static void Initialize(in ContactPoint point, ReadOnlySpan<ContactBodyLink> links, in ContactMaterial material,
        ref AvbdSupportedContactState state, scoped PhysicsBodies bodies, in PhysicsStepContext context, float retention)
    {
        AvbdSupportedContactMath.Initialize(in point, links, in material, ref state, bodies, in context, retention);
        SceneContactPenalty.Initialize(in point, links, ref state.Contact, bodies, in context, retention);
    }
    public static void AccumulateBodyEquation(in ContactPoint point, ReadOnlySpan<ContactBodyLink> links, in ContactMaterial material,
        in AvbdSupportedContactState state, Entity body, scoped PhysicsBodies bodies, in PhysicsStepContext context,
        float initialErrorFraction, ref AvbdBodyEquation equation) =>
        AvbdSupportedContactMath.AccumulateBodyEquation(in point, links, in material, in state, body, bodies, in context, initialErrorFraction, ref equation);
    public static void UpdateDuals(in ContactPoint point, ReadOnlySpan<ContactBodyLink> links, in ContactMaterial material,
        ref AvbdSupportedContactState state, scoped PhysicsBodies bodies, in PhysicsStepContext context,
        float initialErrorFraction, ref ContactResponse response)
    {
        AvbdSupportedContactMath.UpdateDuals(in point, links, in material, ref state, bodies, in context, initialErrorFraction, ref response);
        SceneContactPenalty.Update(in point, links, ref state.Contact, bodies, in context);
    }
}

internal static class SceneContactPenalty
{
    internal const float Minimum = 1, Maximum = 1e9f, IncreasePerMeter = 100000, PressureLengthScale = .0005f;

    internal static void Initialize(in ContactPoint point, ReadOnlySpan<ContactBodyLink> links, ref AvbdContactState state, scoped PhysicsBodies bodies,
        in PhysicsStepContext context, float retention)
    {
        float mobility = 0;
        for (int index = 0; index < links.Length; index++)
        {
            Entity body = ReadBody(in links[index], bodies, in context);
            bool first = true;
            for (int previous = 0; previous < index; previous++)
                first &= ReadBody(in links[previous], bodies, in context) != body;
            if (!first) continue;
            ref readonly BodyMass mass = ref bodies.ReadMass(body);
            if (mass.Motion != BodyMotion.Dynamic) continue;
            ref readonly BodyPose pose = ref bodies.ReadPose(body);
            Vector3 center = pose.GetCenterOfMass(in mass), linear = default, angular = default;
            for (int linkIndex = 0; linkIndex < links.Length; linkIndex++)
            {
                ref readonly ContactBodyLink link = ref links[linkIndex];
                if (ReadBody(in link, bodies, in context) != body) continue;
                float weight = link.Weight * (linkIndex < point.FirstBodyCount ? -1 : 1);
                Vector3 arm = pose.Position + Vector3.Transform(link.NormalLocalPoint, pose.Rotation) - center;
                linear += point.Normal * weight;
                angular += Vector3.Cross(arm, point.Normal) * weight;
            }
            Vector3 local = Vector3.Transform(angular, Quaternion.Conjugate(pose.Rotation));
            Vector3 response = Vector3.Transform(Vector3.TransformNormal(local, mass.InverseInertia), pose.Rotation);
            mobility += mass.InverseMass * linear.LengthSquared() + Vector3.Dot(angular, response);
        }
        float conditioning = (float)(1 / (context.Duration * context.Duration * mobility));
        state.NormalPenalty = Math.Clamp(MathF.Max(conditioning, state.NormalPenalty * retention), Minimum, Maximum);
        state.TangentPenalty = Math.Clamp(MathF.Max(conditioning, state.TangentPenalty * retention), Minimum, Maximum);
    }

    internal static void Update(in ContactPoint point, ReadOnlySpan<ContactBodyLink> links, ref AvbdContactState state, scoped PhysicsBodies bodies, in PhysicsStepContext context)
    {
        Vector3 delta = default;
        for (int index = 0; index < links.Length; index++)
        {
            ref readonly ContactBodyLink link = ref links[index];
            ref readonly BodyPose pose = ref bodies.ReadPose(ReadBody(in link, bodies, in context));
            delta += (pose.Position + Vector3.Transform(link.NormalLocalPoint, pose.Rotation))
                * (link.Weight * (index < point.FirstBodyCount ? -1 : 1));
        }
        float gap = point.Separation + Vector3.Dot(point.Normal, delta - point.BodyAnchorDelta);
        float pressureMetric = -state.NormalMultiplier / PressureLengthScale;
        float penalty = MathF.Max(state.NormalPenalty + IncreasePerMeter * MathF.Max(0, -gap), pressureMetric);
        state.NormalPenalty = Math.Clamp(penalty, Minimum, Maximum);
        state.TangentPenalty = state.NormalPenalty;
    }

    private static Entity ReadBody(in ContactBodyLink link, scoped PhysicsBodies bodies, in PhysicsStepContext context) =>
        link.Body;
}
