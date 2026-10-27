// SPDX-License-Identifier: Apache-2.0
// Copyright (c) 2025 The Newton Developers
// C# adaptation of compliant ALM joint assembly and duals from Newton v1.7.0 through NewtonRigid.cpp.
// Source and license: ThirdParty/Newton/NOTICE and the repository LICENSE.
using System.Numerics;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Relations;

namespace SomeEngine.Physics;

public readonly struct AvbdJointMath : IAvbdConstraint<JointConstraint>, IPhysicsConstraintForces<JointConstraint>
{
    public static void GetBodyForce(in JointConstraint constraint, scoped QueryRow row, Entity body,
        scoped PhysicsBodies bodies, in PhysicsStepContext context, out Vector3 force, out Vector3 torque)
    {
        ref readonly var endpoints = ref row.ReadRef<DirectedRelationEndpoints<JointConstraint>>();
        ref readonly AvbdJointState state = ref row.ReadRef<AvbdJointState>();
        bool worldPoint = UsesWorldPointCoordinates(in constraint);
        force = torque = Vector3.Zero;
        for (int dof = 0; dof < 6; dof++)
        {
            if (JointMath.GetLimit(in constraint, dof).Motion == JointAxisMotion.Free ||
                (dof < 3 ? constraint.LinearStiffness[dof] : constraint.AngularStiffness[dof - 3]) == 0) continue;
            float multiplier = dof < 3 ? state.LinearMultiplier[dof] : state.AngularMultiplier[dof - 3];
            JointEquationRow derivative = GetRow(in constraint, in bodies.ReadPose(endpoints.Source), in bodies.ReadMass(endpoints.Source),
                in bodies.ReadPose(endpoints.Target), in bodies.ReadMass(endpoints.Target), dof, worldPoint);
            if (body == endpoints.Source) { force -= derivative.SourceLinear * multiplier; torque -= derivative.SourceAngular * multiplier; }
            if (body == endpoints.Target) { force -= derivative.TargetLinear * multiplier; torque -= derivative.TargetAngular * multiplier; }
        }
    }

    public static PhysicsConstraintError GetActivityError(in JointConstraint constraint, scoped QueryRow row,
        scoped PhysicsBodies bodies, in PhysicsStepContext context)
    {
        ref readonly var endpoints = ref row.ReadRef<DirectedRelationEndpoints<JointConstraint>>();
        return JointMath.GetActivityError(in constraint, in bodies.ReadPose(endpoints.Source), in bodies.ReadPose(endpoints.Target));
    }

    public static int GetBodyCount(in JointConstraint constraint, scoped QueryRow row, in PhysicsStepContext context) => 2;
    public static Entity GetBody(in JointConstraint constraint, scoped QueryRow row, int index, in PhysicsStepContext context)
    {
        ref readonly var endpoints = ref row.ReadRef<DirectedRelationEndpoints<JointConstraint>>();
        return index == 0 ? endpoints.Source : endpoints.Target;
    }

    public static void Initialize(in JointConstraint constraint, scoped QueryRow row, scoped PhysicsBodies bodies,
        in PhysicsStepContext context, float retention)
    {
        ref readonly var endpoints = ref row.ReadRef<DirectedRelationEndpoints<JointConstraint>>();
        ref readonly BodyMass sourceMass = ref bodies.ReadMass(endpoints.Source);
        ref readonly BodyMass targetMass = ref bodies.ReadMass(endpoints.Target);
        ref readonly BodyStepState sourceStart = ref bodies.ReadStepState(endpoints.Source);
        ref readonly BodyStepState targetStart = ref bodies.ReadStepState(endpoints.Target);
        var source = new BodyPose { Rotation = sourceStart.StartRotation };
        var target = new BodyPose { Rotation = targetStart.StartRotation };
        source.SetCenterOfMass(sourceStart.StartCenterOfMass, in sourceMass);
        target.SetCenterOfMass(targetStart.StartCenterOfMass, in targetMass);
        GetCoordinates(in constraint, in source, in target, out Vector3 linear, out Vector3 angular);
        ref AvbdJointState state = ref row.ReadWrite<AvbdJointState>();
        state.LinearMultiplier *= retention;
        state.AngularMultiplier *= retention;
        for (int axis = 0; axis < 3; axis++)
        {
            state.InitialLinearError[axis] = JointMath.GetInitialError(JointMath.GetLimit(in constraint, axis), linear[axis]);
            state.InitialAngularError[axis] = JointMath.GetInitialError(JointMath.GetLimit(in constraint, axis + 3), angular[axis]);
        }
    }

    public static void AccumulateBodyEquation(in JointConstraint constraint, scoped QueryRow row, Entity body,
        scoped PhysicsBodies bodies, in PhysicsStepContext context, float initialErrorFraction, ref AvbdBodyEquation equation)
    {
        ref readonly var endpoints = ref row.ReadRef<DirectedRelationEndpoints<JointConstraint>>();
        QueryRow sourceRow = bodies.GetRow(endpoints.Source), targetRow = bodies.GetRow(endpoints.Target);
        ref readonly BodyPose source = ref sourceRow.ReadRef<BodyPose>();
        ref readonly BodyPose target = ref targetRow.ReadRef<BodyPose>();
        ref readonly BodyMass sourceMass = ref sourceRow.ReadRef<BodyMass>();
        ref readonly BodyMass targetMass = ref targetRow.ReadRef<BodyMass>();
        ref readonly AvbdJointState state = ref row.ReadRef<AvbdJointState>();
        bool isSource = body == endpoints.Source;
        bool worldPoint = UsesWorldPointCoordinates(in constraint);
        Vector3 linearForce = default;
        for (int dof = 0; dof < 6; dof++)
        {
            int axis = dof % 3;
            JointAxisLimit limit = JointMath.GetLimit(in constraint, dof);
            float stiffness = dof < 3 ? constraint.LinearStiffness[axis] : constraint.AngularStiffness[axis];
            if (limit.Motion == JointAxisMotion.Free || stiffness == 0) continue;
            JointEquationRow derivative = GetRow(in constraint, in source, in sourceMass, in target, in targetMass, dof, worldPoint);
            float multiplier = dof < 3 ? state.LinearMultiplier[axis] : state.AngularMultiplier[axis];
            if (!JointMath.TryGetError(limit, derivative.Coordinate, multiplier, out float error, out JointLimitSide side)) continue;
            float penalty = dof < 3 ? state.LinearPenalty[axis] : state.AngularPenalty[axis];
            float initial = dof < 3 ? state.InitialLinearError[axis] : state.InitialAngularError[axis];
            var material = new AvbdMaterialTerms(stiffness, penalty);
            float force = JointMath.ProjectForce(side, material.EvaluateForce(error - initialErrorFraction * initial, multiplier));
            if (limit.Motion == JointAxisMotion.Limited && force == 0) continue;
            if (dof < 3) linearForce += derivative.TargetLinear * force;
            equation.AddRow(isSource ? derivative.SourceLinear : derivative.TargetLinear,
                isSource ? derivative.SourceAngular : derivative.TargetAngular, force, material.EffectiveStiffness);
        }
        if (linearForce == Vector3.Zero) return;
        Vector3 forceOffset = isSource
            ? worldPoint
                ? Vector3.Transform(constraint.SourceFrame.Position - sourceMass.CenterOfMass, source.Rotation)
                : (target.Position - source.Position) + Vector3.Transform(constraint.TargetFrame.Position, target.Rotation)
                    - Vector3.Transform(sourceMass.CenterOfMass, source.Rotation)
            : Vector3.Transform(constraint.TargetFrame.Position - targetMass.CenterOfMass, target.Rotation);
        // Bounds the force-weighted angular Hessian of the current linear joint coordinates.
        float curvature = forceOffset.Length() * linearForce.Length();
        equation.AddRow(Vector3.Zero, Vector3.UnitX, 0, curvature);
        equation.AddRow(Vector3.Zero, Vector3.UnitY, 0, curvature);
        equation.AddRow(Vector3.Zero, Vector3.UnitZ, 0, curvature);
    }

    public static void UpdateDuals(in JointConstraint constraint, scoped QueryRow row, scoped PhysicsBodies bodies,
        in PhysicsStepContext context, float initialErrorFraction)
    {
        ref readonly var endpoints = ref row.ReadRef<DirectedRelationEndpoints<JointConstraint>>();
        ref readonly BodyPose source = ref bodies.ReadPose(endpoints.Source);
        ref readonly BodyPose target = ref bodies.ReadPose(endpoints.Target);
        GetCoordinates(in constraint, in source, in target, out Vector3 linear, out Vector3 angular);
        ref AvbdJointState state = ref row.ReadWrite<AvbdJointState>();
        for (int dof = 0; dof < 6; dof++)
        {
            int axis = dof % 3;
            float multiplier = dof < 3 ? state.LinearMultiplier[axis] : state.AngularMultiplier[axis];
            float coordinate = dof < 3 ? linear[axis] : angular[axis];
            JointAxisLimit limit = JointMath.GetLimit(in constraint, dof);
            float force = 0;
            if (JointMath.TryGetError(limit, coordinate, multiplier, out float error, out JointLimitSide side))
            {
                float stiffness = dof < 3 ? constraint.LinearStiffness[axis] : constraint.AngularStiffness[axis];
                float penalty = dof < 3 ? state.LinearPenalty[axis] : state.AngularPenalty[axis];
                float initial = dof < 3 ? state.InitialLinearError[axis] : state.InitialAngularError[axis];
                var material = new AvbdMaterialTerms(stiffness, penalty);
                force = JointMath.ProjectForce(side, material.EvaluateForce(error - initialErrorFraction * initial, multiplier));
            }
            if (dof < 3) state.LinearMultiplier[axis] = force;
            else state.AngularMultiplier[axis] = force;
        }
    }

    private static bool UsesWorldPointCoordinates(in JointConstraint constraint) =>
        constraint.LinearX.Motion == JointAxisMotion.Locked && constraint.LinearY.Motion == JointAxisMotion.Locked
        && constraint.LinearZ.Motion == JointAxisMotion.Locked && constraint.LinearStiffness.X == constraint.LinearStiffness.Y
        && constraint.LinearStiffness.X == constraint.LinearStiffness.Z;

    private static void GetCoordinates(in JointConstraint constraint, in BodyPose source, in BodyPose target,
        out Vector3 linear, out Vector3 angular)
    {
        JointMath.GetCoordinates(in constraint, in source, in target, out linear, out angular);
        if (UsesWorldPointCoordinates(in constraint))
        {
            linear = (target.Position - source.Position) + Vector3.Transform(constraint.TargetFrame.Position, target.Rotation)
                - Vector3.Transform(constraint.SourceFrame.Position, source.Rotation);
        }
    }

    private static JointEquationRow GetRow(in JointConstraint constraint, in BodyPose source, in BodyMass sourceMass,
        in BodyPose target, in BodyMass targetMass, int dof, bool worldPoint)
    {
        if (dof >= 3 || !worldPoint) return JointMath.GetRow(in constraint, in source, in sourceMass, in target, in targetMass, dof);
        Vector3 direction = dof == 0 ? Vector3.UnitX : dof == 1 ? Vector3.UnitY : Vector3.UnitZ;
        Vector3 delta = (target.Position - source.Position) + Vector3.Transform(constraint.TargetFrame.Position, target.Rotation)
            - Vector3.Transform(constraint.SourceFrame.Position, source.Rotation);
        Vector3 sourceOffset = Vector3.Transform(constraint.SourceFrame.Position - sourceMass.CenterOfMass, source.Rotation);
        Vector3 targetOffset = Vector3.Transform(constraint.TargetFrame.Position - targetMass.CenterOfMass, target.Rotation);
        return new(Vector3.Dot(delta, direction), -direction, -Vector3.Cross(sourceOffset, direction), direction,
            Vector3.Cross(targetOffset, direction));
    }
}
