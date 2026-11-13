// SPDX-License-Identifier: Apache-2.0
// Copyright (c) 2025 The Newton Developers
// C# adaptation of Newton v1.7.0 compliant ALM joint rows, through NewtonRigid.cpp.
// Source and license: ThirdParty/Newton/NOTICE and the repository LICENSE.
using System.Numerics;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS;

namespace SomeEngine.Physics;

public readonly partial struct AvbdElasticConnectionMath
{
    public static void GetBodyForce(in ElasticConnection constraint, in RelationEndpoints<ElasticConnection> endpoints, ref AvbdElasticState state, Entity body,
        scoped PhysicsBodies bodies, in PhysicsStepContext context, out Vector3 force, out Vector3 torque)
    {
        ref readonly BodyPose first = ref bodies.ReadPose(endpoints.Source);
        ref readonly BodyPose second = ref bodies.ReadPose(endpoints.Target);
        float sign = body == endpoints.Source ? 1 : -1;
        Vector3 local = body == endpoints.Source ? constraint.FirstAnchor : constraint.SecondAnchor;
        ref readonly BodyPose pose = ref bodies.ReadPose(body);
        force = -sign * state.LinearMultiplier;
        torque = Vector3.Cross(Vector3.Transform(local - bodies.ReadMass(body).CenterOfMass, pose.Rotation), force);
        Vector3 error = RotationError(first.Rotation, second.Rotation, constraint.RestRotation);
        Quaternion alignment = Quaternion.Conjugate(first.Rotation) * second.Rotation * Quaternion.Conjugate(constraint.RestRotation);
        for (int axis = 0; axis < 3; axis++)
        {
            Vector3 direction = axis == 0 ? Vector3.UnitX : axis == 1 ? Vector3.UnitY : Vector3.UnitZ;
            torque += BodyRotation.GetConstraintJacobian(first.Rotation, alignment, error, direction) * (sign * state.AngularMultiplier[axis]);
        }
    }

    public static PhysicsConstraintError GetActivityError(in ElasticConnection constraint, in RelationEndpoints<ElasticConnection> endpoints,
        scoped PhysicsBodies bodies, in PhysicsStepContext context)
    {
        ref readonly BodyPose first = ref bodies.ReadPose(endpoints.Source);
        ref readonly BodyPose second = ref bodies.ReadPose(endpoints.Target);
        return new(constraint.LinearStiffness > 0 ? LinearError(in constraint, in first, in second).Length() : 0,
            constraint.AngularStiffness > 0 ? RotationError(first.Rotation, second.Rotation, constraint.RestRotation).Length() : 0);
    }




    public static void Initialize(in ElasticConnection constraint, in RelationEndpoints<ElasticConnection> endpoints, ref AvbdElasticState state,
        scoped PhysicsBodies bodies, in PhysicsStepContext context, float retention)
    {
        ref readonly BodyMass firstMass = ref bodies.ReadMass(endpoints.Source);
        ref readonly BodyMass secondMass = ref bodies.ReadMass(endpoints.Target);
        ref readonly BodyStepState first = ref bodies.ReadStepState(endpoints.Source);
        ref readonly BodyStepState second = ref bodies.ReadStepState(endpoints.Target);
        state.InitialLinearError = first.StartCenterOfMass + Vector3.Transform(constraint.FirstAnchor - firstMass.CenterOfMass, first.StartRotation)
            - second.StartCenterOfMass - Vector3.Transform(constraint.SecondAnchor - secondMass.CenterOfMass, second.StartRotation);
        state.InitialAngularError = RotationError(first.StartRotation, second.StartRotation, constraint.RestRotation);
        state.LinearMultiplier *= retention;
        state.AngularMultiplier *= retention;
    }

    public static void AccumulateBodyEquation(in ElasticConnection constraint, in RelationEndpoints<ElasticConnection> endpoints, ref AvbdElasticState state, Entity body,
        scoped PhysicsBodies bodies, in PhysicsStepContext context, float initialErrorFraction, ref AvbdBodyEquation equation)
    {
        ref readonly BodyPose first = ref bodies.ReadPose(endpoints.Source);
        ref readonly BodyPose second = ref bodies.ReadPose(endpoints.Target);
        ref readonly BodyMass mass = ref bodies.ReadMass(body);
        bool isFirst = body.Equals(endpoints.Source);
        float sign = isFirst ? 1 : -1;
        Vector3 localAnchor = isFirst ? constraint.FirstAnchor : constraint.SecondAnchor;
        Quaternion rotation = isFirst ? first.Rotation : second.Rotation;
        Vector3 offset = Vector3.Transform(localAnchor - mass.CenterOfMass, rotation);
        Vector3 error = LinearError(in constraint, in first, in second) - state.InitialLinearError * initialErrorFraction;
        var linear = new AvbdMaterialTerms(constraint.LinearStiffness, state.LinearPenalty);
        Vector3 force = linear.EvaluateForce(error, state.LinearMultiplier);
        for (int axis = 0; axis < 3; axis++)
        {
            Vector3 direction = axis == 0 ? Vector3.UnitX : axis == 1 ? Vector3.UnitY : Vector3.UnitZ;
            equation.AddRow(direction * sign, Vector3.Cross(offset, direction) * sign, force[axis], linear.EffectiveStiffness);
        }
        Vector3 angularError = RotationError(first.Rotation, second.Rotation, constraint.RestRotation);
        var angular = new AvbdMaterialTerms(constraint.AngularStiffness, state.AngularPenalty);
        Vector3 angularForce = angular.EvaluateForce(angularError - state.InitialAngularError * initialErrorFraction, state.AngularMultiplier);
        Quaternion alignment = Quaternion.Conjugate(first.Rotation) * second.Rotation * Quaternion.Conjugate(constraint.RestRotation);
        for (int axis = 0; axis < 3; axis++)
        {
            Vector3 direction = axis == 0 ? Vector3.UnitX : axis == 1 ? Vector3.UnitY : Vector3.UnitZ;
            Vector3 jacobian = BodyRotation.GetConstraintJacobian(first.Rotation, alignment, angularError, direction) * -sign;
            equation.AddRow(Vector3.Zero, jacobian, angularForce[axis], angular.EffectiveStiffness);
        }
    }

    public static void UpdateDuals(in ElasticConnection constraint, in RelationEndpoints<ElasticConnection> endpoints, ref AvbdElasticState state,
        scoped PhysicsBodies bodies, in PhysicsStepContext context, float initialErrorFraction)
    {
        ref readonly BodyPose first = ref bodies.ReadPose(endpoints.Source);
        ref readonly BodyPose second = ref bodies.ReadPose(endpoints.Target);
        var linear = new AvbdMaterialTerms(constraint.LinearStiffness, state.LinearPenalty);
        var angular = new AvbdMaterialTerms(constraint.AngularStiffness, state.AngularPenalty);
        state.LinearMultiplier = linear.EvaluateForce(LinearError(in constraint, in first, in second)
            - state.InitialLinearError * initialErrorFraction, state.LinearMultiplier);
        state.AngularMultiplier = angular.EvaluateForce(RotationError(first.Rotation, second.Rotation, constraint.RestRotation)
            - state.InitialAngularError * initialErrorFraction, state.AngularMultiplier);
    }

    private static Vector3 LinearError(in ElasticConnection constraint, in BodyPose first, in BodyPose second) =>
        first.Position + Vector3.Transform(constraint.FirstAnchor, first.Rotation)
        - second.Position - Vector3.Transform(constraint.SecondAnchor, second.Rotation);

    private static Vector3 RotationError(Quaternion first, Quaternion second, Quaternion rest) =>
        BodyRotation.GetRotationVector(Quaternion.Conjugate(first) * second * Quaternion.Conjugate(rest));
}
