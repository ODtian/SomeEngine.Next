// SPDX-License-Identifier: Apache-2.0
// Copyright (c) 2025 The Newton Developers
// C# adaptation of Newton v1.7.0 compliant ALM joint rows, through NewtonRigid.cpp.
// Source and license: ThirdParty/Newton/NOTICE and the repository LICENSE.
using System.Numerics;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;

namespace SomeEngine.Physics;

public readonly struct AvbdElasticConnectionMath : IAvbdConstraint<ElasticConnection>
{
    public static int GetBodyCount(in ElasticConnection constraint, scoped QueryRow row) => 2;
    public static Entity GetBody(in ElasticConnection constraint, scoped QueryRow row, int index)
    {
        ref readonly var endpoints = ref row.ReadRef<UndirectedRelationEndpoints<ElasticConnection>>();
        return index == 0 ? endpoints.EndpointA : endpoints.EndpointB;
    }

    public static void Initialize(in ElasticConnection constraint, scoped QueryRow row,
        scoped PhysicsBodies bodies, in PhysicsStepContext context, float retention)
    {
        ref AvbdElasticState state = ref row.ReadWrite<AvbdElasticState>();
        ref readonly var endpoints = ref row.ReadRef<UndirectedRelationEndpoints<ElasticConnection>>();
        ref readonly BodyMass firstMass = ref bodies.ReadMass(endpoints.EndpointA);
        ref readonly BodyMass secondMass = ref bodies.ReadMass(endpoints.EndpointB);
        ref readonly BodyStepState first = ref bodies.ReadStepState(endpoints.EndpointA);
        ref readonly BodyStepState second = ref bodies.ReadStepState(endpoints.EndpointB);
        state.InitialLinearError = first.StartCenterOfMass + Vector3.Transform(constraint.FirstAnchor - firstMass.CenterOfMass, first.StartRotation)
            - second.StartCenterOfMass - Vector3.Transform(constraint.SecondAnchor - secondMass.CenterOfMass, second.StartRotation);
        state.InitialAngularError = RotationError(first.StartRotation, second.StartRotation, constraint.RestRotation);
        state.LinearMultiplier *= retention;
        state.AngularMultiplier *= retention;
    }

    public static void AccumulateBodyEquation(in ElasticConnection constraint, scoped QueryRow row, Entity body,
        scoped PhysicsBodies bodies, in PhysicsStepContext context, float initialErrorFraction, ref AvbdBodyEquation equation)
    {
        ref readonly AvbdElasticState state = ref row.ReadRef<AvbdElasticState>();
        ref readonly var endpoints = ref row.ReadRef<UndirectedRelationEndpoints<ElasticConnection>>();
        ref readonly BodyPose first = ref bodies.ReadPose(endpoints.EndpointA);
        ref readonly BodyPose second = ref bodies.ReadPose(endpoints.EndpointB);
        ref readonly BodyMass mass = ref bodies.ReadMass(body);
        bool isFirst = body.Equals(endpoints.EndpointA);
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
            Vector3 right = direction - Vector3.Cross(angularError, direction) * 0.5f
                + Vector3.Cross(angularError, Vector3.Cross(angularError, direction)) * (1f / 12);
            Vector3 jacobian = Vector3.Transform(Vector3.Transform(right, alignment), first.Rotation) * -sign;
            equation.AddRow(Vector3.Zero, jacobian, angularForce[axis], angular.EffectiveStiffness);
        }
    }

    public static void UpdateDuals(in ElasticConnection constraint, scoped QueryRow row,
        scoped PhysicsBodies bodies, in PhysicsStepContext context, float initialErrorFraction)
    {
        ref AvbdElasticState state = ref row.ReadWrite<AvbdElasticState>();
        ref readonly var endpoints = ref row.ReadRef<UndirectedRelationEndpoints<ElasticConnection>>();
        ref readonly BodyPose first = ref bodies.ReadPose(endpoints.EndpointA);
        ref readonly BodyPose second = ref bodies.ReadPose(endpoints.EndpointB);
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
        AvbdRotation.GetRotationVector(Quaternion.Conjugate(first) * second * Quaternion.Conjugate(rest));
}
