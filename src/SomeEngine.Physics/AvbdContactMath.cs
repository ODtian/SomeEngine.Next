// SPDX-License-Identifier: Apache-2.0
// Copyright (c) 2025 The Newton Developers
// Compliant unilateral contact follows Newton v1.7.0 through NewtonRigid.cpp.
// Source and license: ThirdParty/Newton/NOTICE and the repository LICENSE.
using System.Numerics;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;

namespace SomeEngine.Physics;

public readonly struct AvbdContactMath : IAvbdConstraint<ContactPoint>
{
    public static PhysicsConstraintError GetActivityError(in ContactPoint point, scoped QueryRow row,
        scoped PhysicsBodies bodies, in PhysicsStepContext context) => ContactBodyMath.GetActivityError(in point, row, bodies, in context);

    public static int GetReadEntityCount(in ContactPoint point, scoped QueryRow row, in PhysicsStepContext context) =>
        row.ReadBuffer<ContactBodyLink>().Count;
    public static Entity GetReadEntity(in ContactPoint point, scoped QueryRow row, int index, in PhysicsStepContext context) =>
        row.ReadBuffer<ContactBodyLink>().AsSpan()[index].Participant.Entity;
    public static int GetBodyCount(in ContactPoint point, scoped QueryRow row, in PhysicsStepContext context) =>
        row.ReadBuffer<ContactBodyLink>().Count;

    public static Entity GetBody(in ContactPoint point, scoped QueryRow row, int index, in PhysicsStepContext context) =>
        ContactBodyMath.GetBody(in row.ReadBuffer<ContactBodyLink>().AsSpan()[index], in context);

    public static bool TryGetBody(in ContactPoint point, scoped QueryRow row, int index, scoped PhysicsBodies bodies,
        in PhysicsStepContext context, out Entity body)
    {
        body = ContactBodyMath.GetBody(in row.ReadBuffer<ContactBodyLink>().AsSpan()[index], bodies, in context);
        return true;
    }

    public static void Initialize(in ContactPoint point, scoped QueryRow row, scoped PhysicsBodies bodies,
        in PhysicsStepContext context, float retention)
    {
        ref AvbdContactState state = ref row.ReadWrite<AvbdContactState>();
        ref readonly ContactMaterial material = ref row.ReadRef<ContactMaterial>();
        if (point.WarmStart)
        {
            Vector3 force = state.MultiplierNormal * state.NormalMultiplier + state.TangentMultiplier;
            float normal = Vector3.Dot(force, point.Normal);
            state.NormalMultiplier = MathF.Min(normal, 0) * retention;
            state.TangentMultiplier = (force - point.Normal * normal) * retention;
            float magnitude = state.TangentMultiplier.Length();
            float bound = -state.NormalMultiplier * material.StaticFriction;
            if (magnitude > bound) state.TangentMultiplier *= bound / magnitude;
        }
        else
        {
            state.NormalMultiplier = 0;
            state.TangentMultiplier = Vector3.Zero;
        }
        state.MultiplierNormal = point.Normal;
        Vector3 start = ContactBodyMath.GetDelta(in point, row.ReadBuffer<ContactBodyLink>().AsSpan(), bodies, in context, true);
        state.InitialDelta = start;
        Vector3 normalStart = ContactBodyMath.GetDelta(in point, row.ReadBuffer<ContactBodyLink>().AsSpan(), bodies, in context, true, normal: true);
        state.InitialSeparation = point.Separation + Vector3.Dot(point.Normal, normalStart - point.BodyAnchorDelta);
    }

    public static void AccumulateBodyEquation(in ContactPoint point, scoped QueryRow row, Entity body,
        scoped PhysicsBodies bodies, in PhysicsStepContext context, float initialErrorFraction, ref AvbdBodyEquation equation)
        => AccumulateBodyEquation(in point, row, body, bodies, in context, initialErrorFraction, Vector3.Zero, ref equation);

    internal static void AccumulateBodyEquation(in ContactPoint point, scoped QueryRow row, Entity body,
        scoped PhysicsBodies bodies, in PhysicsStepContext context, float initialErrorFraction,
        Vector3 extraForceOnSecond, ref AvbdBodyEquation equation) =>
        AccumulateBodyEquation(in point, row, body, bodies, in context, initialErrorFraction, extraForceOnSecond,
            normalUpperBound: false, angularCurvature: false, ref equation);

    internal static void AccumulatePotentialBodyEquation(in ContactPoint point, scoped QueryRow row, Entity body,
        scoped PhysicsBodies bodies, in PhysicsStepContext context, float initialErrorFraction,
        Vector3 extraForceOnSecond, bool supported, ref AvbdBodyEquation equation) =>
        AccumulateBodyEquation(in point, row, body, bodies, in context, initialErrorFraction, extraForceOnSecond, !supported, true, ref equation);

    private static void AccumulateBodyEquation(in ContactPoint point, scoped QueryRow row, Entity body,
        scoped PhysicsBodies bodies, in PhysicsStepContext context, float initialErrorFraction,
        Vector3 extraForceOnSecond, bool normalUpperBound, bool angularCurvature, ref AvbdBodyEquation equation)
    {
        ReadOnlySpan<ContactBodyLink> links = row.ReadBuffer<ContactBodyLink>().AsSpan();
        ref readonly BodyPose pose = ref bodies.ReadPose(body);
        Vector3 center = pose.GetCenterOfMass(in bodies.ReadMass(body));
        Vector3 delta = default, normalDelta = default, weightedOffset = default, normalOffset = default;
        float bodyWeight = 0, curvatureLength = 0, normalCurvatureLength = 0, frictionCurvatureLength = 0;
        for (int index = 0; index < links.Length; index++)
        {
            ref readonly ContactBodyLink link = ref links[index];
            Entity participant = ContactBodyMath.GetBody(in link, bodies, in context);
            ref readonly BodyPose participantPose = ref (participant == body ? ref pose : ref bodies.ReadPose(participant));
            Vector3 position = participantPose.Position + Vector3.Transform(link.LocalPoint, participantPose.Rotation);
            Vector3 normalPosition = link.NormalReference == ContactNormalReference.MaterialPoint ? position
                : participantPose.Position + Vector3.Transform(link.NormalLocalPoint, participantPose.Rotation);
            float weight = link.Weight * (index < point.FirstBodyCount ? -1 : 1);
            delta += position * weight;
            normalDelta += normalPosition * weight;
            if (participant != body) continue;
            Vector3 offset = position - center;
            bodyWeight += weight;
            weightedOffset += offset * weight;
            normalOffset += (normalPosition - center) * weight;
            if (angularCurvature)
            {
                if (link.NormalReference == ContactNormalReference.MaterialPoint)
                    curvatureLength += MathF.Abs(link.Weight) * offset.Length();
                else
                {
                    normalCurvatureLength += MathF.Abs(link.Weight) * (normalPosition - center).Length();
                    frictionCurvatureLength += MathF.Abs(link.Weight) * offset.Length();
                }
            }
        }
        AvbdContactEquation contact = Evaluate(in point, row, in context, initialErrorFraction, extraForceOnSecond, delta, normalDelta);
        Vector3 linear = point.Normal * bodyWeight, angular = Vector3.Cross(normalOffset, point.Normal);
        float normalStiffness = contact.NormalStiffness;
        if (normalUpperBound && normalStiffness == 0)
            normalStiffness = new AvbdMaterialTerms(row.ReadRef<ContactMaterial>().NormalStiffness,
                row.ReadRef<AvbdContactState>().NormalPenalty).EffectiveStiffness;
        equation.AddRow(linear, angular, contact.NormalForce, normalStiffness);
        Vector3 across = Vector3.Cross(point.Normal, contact.TangentDirection);
        linear = contact.TangentDirection * bodyWeight;
        angular = Vector3.Cross(weightedOffset, contact.TangentDirection);
        equation.AddRow(linear, angular, Vector3.Dot(contact.TangentForce, contact.TangentDirection), contact.AlongStiffness);
        linear = across * bodyWeight;
        angular = Vector3.Cross(weightedOffset, across);
        equation.AddRow(linear, angular, Vector3.Dot(contact.TangentForce, across), contact.AcrossStiffness);
        if (angularCurvature)
        {
            Vector3 force = point.Normal * contact.NormalForce + contact.TangentForce - extraForceOnSecond;
            float normalForce = Vector3.Dot(force, point.Normal);
            float curvature = curvatureLength * force.Length();
            if (normalCurvatureLength > 0 || frictionCurvatureLength > 0)
                curvature += normalCurvatureLength * MathF.Abs(normalForce)
                    + frictionCurvatureLength * (force - point.Normal * normalForce).Length();
            equation.AddRow(Vector3.Zero, Vector3.UnitX, 0, curvature);
            equation.AddRow(Vector3.Zero, Vector3.UnitY, 0, curvature);
            equation.AddRow(Vector3.Zero, Vector3.UnitZ, 0, curvature);
        }
    }

    public static void UpdateDuals(in ContactPoint point, scoped QueryRow row, scoped PhysicsBodies bodies,
        in PhysicsStepContext context, float initialErrorFraction)
        => UpdateDuals(in point, row, bodies, in context, initialErrorFraction, Vector3.Zero);

    internal static void UpdateDuals(in ContactPoint point, scoped QueryRow row, scoped PhysicsBodies bodies,
        in PhysicsStepContext context, float initialErrorFraction, Vector3 extraForceOnSecond)
    {
        AvbdContactEquation contact = Evaluate(in point, row, bodies, in context, initialErrorFraction, extraForceOnSecond);
        ref AvbdContactState state = ref row.ReadWrite<AvbdContactState>();
        state.NormalMultiplier = contact.NormalDual;
        state.TangentMultiplier = contact.TangentForce;
        row.ReadWrite<ContactResponse>().ForceOnSecond = -(point.Normal * contact.NormalForce + contact.TangentForce) + extraForceOnSecond;
    }

    private static AvbdContactEquation Evaluate(in ContactPoint point, scoped QueryRow row, scoped PhysicsBodies bodies,
        in PhysicsStepContext context, float initialErrorFraction, Vector3 extraForceOnSecond) =>
        Evaluate(in point, row, in context, initialErrorFraction, extraForceOnSecond,
            ContactBodyMath.GetDelta(in point, row.ReadBuffer<ContactBodyLink>().AsSpan(), bodies, in context, false),
            ContactBodyMath.GetDelta(in point, row.ReadBuffer<ContactBodyLink>().AsSpan(), bodies, in context, false, normal: true));

    private static AvbdContactEquation Evaluate(in ContactPoint point, scoped QueryRow row, in PhysicsStepContext context,
        float initialErrorFraction, Vector3 extraForceOnSecond, Vector3 delta, Vector3 normalDelta)
    {
        ref readonly AvbdContactState state = ref row.ReadRef<AvbdContactState>();
        ref readonly ContactMaterial material = ref row.ReadRef<ContactMaterial>();
        float gap = point.Separation + Vector3.Dot(point.Normal, normalDelta - point.BodyAnchorDelta);
        float residual = gap < 0 ? gap - initialErrorFraction * MathF.Min(state.InitialSeparation, 0) : gap;
        var terms = new AvbdMaterialTerms(material.NormalStiffness, state.NormalPenalty);
        float force = MathF.Min(0, terms.EvaluateForce(residual, state.NormalMultiplier));
        float stiffness = force < 0 ? terms.EffectiveStiffness : 0;
        Vector3 extra = -extraForceOnSecond;
        float extraNormal = Vector3.Dot(extra, point.Normal);
        Vector3 extraTangent = extra - point.Normal * extraNormal;
        float pressure = MathF.Min(0, force + extraNormal);
        Vector3 tangent = -extraTangent, direction = default;
        float along = 0, across = 0;
        if (pressure < 0 && (material.StaticFriction > 0 || material.DynamicFriction > 0))
        {
            Vector3 slip = delta - state.InitialDelta;
            slip -= point.Normal * Vector3.Dot(point.Normal, slip);
            Vector3 trial = state.TangentMultiplier + slip * state.TangentPenalty + extraTangent;
            trial -= point.Normal * Vector3.Dot(point.Normal, trial);
            float magnitude = trial.Length();
            direction = magnitude > 0 ? trial / magnitude : Vector3.Normalize(Vector3.Cross(point.Normal,
                MathF.Abs(point.Normal.X) < 0.707f ? Vector3.UnitX : Vector3.UnitY));
            if (magnitude <= -pressure * material.StaticFriction)
            {
                tangent = trial - extraTangent;
                along = across = state.TangentPenalty;
            }
            else
            {
                float bound = -pressure * material.DynamicFriction;
                tangent = direction * bound - extraTangent;
                float travel = slip.Length();
                float epsilon = state.FrictionRegularizationSpeed * (float)context.Duration;
                float reciprocal = travel > epsilon ? 1 / travel : (2 - travel / epsilon) / epsilon;
                along = bound * reciprocal;
                across = state.TangentPenalty * bound / magnitude;
            }
        }
        float normalForce = force;
        float approach = (gap - state.InitialSeparation) / (float)context.Duration;
        if (material.NormalDamping > 0 && approach < 0 && force < 0)
        {
            normalForce += material.NormalDamping * approach;
            stiffness += material.NormalDamping / (float)context.Duration;
        }
        return new(normalForce, force, stiffness, tangent, direction, along, across);
    }
}

internal readonly record struct AvbdContactEquation(float NormalForce, float NormalDual, float NormalStiffness,
    Vector3 TangentForce, Vector3 TangentDirection, float AlongStiffness, float AcrossStiffness);
