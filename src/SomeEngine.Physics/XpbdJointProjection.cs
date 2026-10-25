using System.Numerics;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Relations;

namespace SomeEngine.Physics;

/// <summary>Projects the common joint coordinates using per-axis XPBD multipliers.</summary>
public readonly struct XpbdJointProjection : IXpbdConstraint<JointConstraint>
{
    public static void Initialize(in JointConstraint constraint, scoped QueryRow row, in PhysicsStepContext context) =>
        row.ReadWrite<XpbdJointState>() = default;

    public static void Project(in JointConstraint constraint, scoped QueryRow row,
        scoped PhysicsBodies bodies, in PhysicsStepContext context)
    {
        ref readonly var endpoints = ref row.ReadRef<DirectedRelationEndpoints<JointConstraint>>();
        ref readonly BodyMass sourceMass = ref bodies.ReadMass(endpoints.Source);
        ref readonly BodyMass targetMass = ref bodies.ReadMass(endpoints.Target);
        if (sourceMass.Motion != BodyMotion.Dynamic && targetMass.Motion != BodyMotion.Dynamic) return;
        ref XpbdJointState state = ref row.ReadWrite<XpbdJointState>();
        float duration = (float)context.Duration;
        for (int dof = 0; dof < 6; dof++)
        {
            int axis = dof % 3;
            JointAxisLimit limit = JointMath.GetLimit(in constraint, dof);
            float stiffness = dof < 3 ? constraint.LinearStiffness[axis] : constraint.AngularStiffness[axis];
            if (limit.Motion == JointAxisMotion.Free || stiffness == 0) continue;
            ref readonly BodyPose source = ref bodies.ReadPose(endpoints.Source);
            ref readonly BodyPose target = ref bodies.ReadPose(endpoints.Target);
            JointEquationRow derivative = JointMath.GetRow(in constraint, in source, in sourceMass, in target, in targetMass, dof);
            float multiplier = dof < 3 ? state.LinearMultiplier[axis] : state.AngularMultiplier[axis];
            // The XPBD displacement multiplier has the opposite sign to the ALM constraint force.
            if (!JointMath.TryGetError(limit, derivative.Coordinate, -multiplier, out float error, out JointLimitSide side)) continue;
            float alpha = 1 / (stiffness * duration * duration);
            float weight = GetWeight(in sourceMass, in source, derivative.SourceLinear, derivative.SourceAngular)
                + GetWeight(in targetMass, in target, derivative.TargetLinear, derivative.TargetAngular);
            float change = (-error - alpha * multiplier) / (weight + alpha);
            float next = -JointMath.ProjectForce(side, -(multiplier + change));
            change = next - multiplier;
            if (dof < 3) state.LinearMultiplier[axis] = next;
            else state.AngularMultiplier[axis] = next;
            ApplyCorrection(bodies, endpoints.Source, in sourceMass, derivative.SourceLinear, derivative.SourceAngular, change);
            ApplyCorrection(bodies, endpoints.Target, in targetMass, derivative.TargetLinear, derivative.TargetAngular, change);
        }
    }

    private static float GetWeight(in BodyMass mass, in BodyPose pose, Vector3 linear, Vector3 angular) =>
        mass.Motion == BodyMotion.Dynamic ? mass.InverseMass * linear.LengthSquared()
            + Vector3.Dot(angular, mass.ApplyInverseInertia(pose.Rotation, angular)) : 0;

    private static void ApplyCorrection(scoped PhysicsBodies bodies, Entity body, in BodyMass mass,
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
