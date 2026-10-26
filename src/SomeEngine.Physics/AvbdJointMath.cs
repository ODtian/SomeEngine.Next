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
        force = torque = Vector3.Zero;
        for (int dof = 0; dof < 6; dof++)
        {
            if (JointMath.GetLimit(in constraint, dof).Motion == JointAxisMotion.Free ||
                (dof < 3 ? constraint.LinearStiffness[dof] : constraint.AngularStiffness[dof - 3]) == 0) continue;
            float multiplier = dof < 3 ? state.LinearMultiplier[dof] : state.AngularMultiplier[dof - 3];
            JointEquationRow derivative = JointMath.GetRow(in constraint, in bodies.ReadPose(endpoints.Source), in bodies.ReadMass(endpoints.Source),
                in bodies.ReadPose(endpoints.Target), in bodies.ReadMass(endpoints.Target), dof);
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
        JointMath.GetCoordinates(in constraint, in source, in target, out Vector3 linear, out Vector3 angular);
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
        ref readonly BodyPose source = ref bodies.ReadPose(endpoints.Source);
        ref readonly BodyPose target = ref bodies.ReadPose(endpoints.Target);
        ref readonly BodyMass sourceMass = ref bodies.ReadMass(endpoints.Source);
        ref readonly BodyMass targetMass = ref bodies.ReadMass(endpoints.Target);
        ref readonly AvbdJointState state = ref row.ReadRef<AvbdJointState>();
        bool isSource = body == endpoints.Source;
        for (int dof = 0; dof < 6; dof++)
        {
            int axis = dof % 3;
            JointAxisLimit limit = JointMath.GetLimit(in constraint, dof);
            JointEquationRow derivative = JointMath.GetRow(in constraint, in source, in sourceMass, in target, in targetMass, dof);
            float multiplier = dof < 3 ? state.LinearMultiplier[axis] : state.AngularMultiplier[axis];
            if (!JointMath.TryGetError(limit, derivative.Coordinate, multiplier, out float error, out JointLimitSide side)) continue;
            float stiffness = dof < 3 ? constraint.LinearStiffness[axis] : constraint.AngularStiffness[axis];
            float penalty = dof < 3 ? state.LinearPenalty[axis] : state.AngularPenalty[axis];
            float initial = dof < 3 ? state.InitialLinearError[axis] : state.InitialAngularError[axis];
            var material = new AvbdMaterialTerms(stiffness, penalty);
            float force = JointMath.ProjectForce(side, material.EvaluateForce(error - initialErrorFraction * initial, multiplier));
            if (limit.Motion == JointAxisMotion.Limited && force == 0) continue;
            equation.AddRow(isSource ? derivative.SourceLinear : derivative.TargetLinear,
                isSource ? derivative.SourceAngular : derivative.TargetAngular, force, material.EffectiveStiffness);
        }
    }

    public static void UpdateDuals(in JointConstraint constraint, scoped QueryRow row, scoped PhysicsBodies bodies,
        in PhysicsStepContext context, float initialErrorFraction)
    {
        ref readonly var endpoints = ref row.ReadRef<DirectedRelationEndpoints<JointConstraint>>();
        ref readonly BodyPose source = ref bodies.ReadPose(endpoints.Source);
        ref readonly BodyPose target = ref bodies.ReadPose(endpoints.Target);
        JointMath.GetCoordinates(in constraint, in source, in target, out Vector3 linear, out Vector3 angular);
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
}
