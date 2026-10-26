// SPDX-License-Identifier: Apache-2.0
// Radial bounds, positive curvature and local anchors follow the project's LongRange.cpp.
// Source and attribution: ThirdParty/LongRange/NOTICE and the repository LICENSE.
using System.Numerics;
using SomeEngine.ECS.Entities;

namespace SomeEngine.Physics;

internal static class DistanceRangeMath
{
    internal static void Evaluate(float minimum, float maximum, float stiffness, float penalty,
        Vector2 previous, Vector2 initialError, float initialFraction, float distance,
        out float force, out float curvature, out Vector2 multiplier)
    {
        var material = new AvbdMaterialTerms(stiffness, penalty);
        if (minimum == maximum)
        {
            force = material.EvaluateForce(distance - maximum - initialFraction * (initialError.X - initialError.Y), previous.X);
            curvature = material.EffectiveStiffness;
            multiplier = new(force, 0);
            return;
        }
        float upper = MathF.Max(0, material.EvaluateForce(distance - maximum - initialFraction * initialError.X, previous.X));
        float lower = MathF.Max(0, material.EvaluateForce(minimum - distance - initialFraction * initialError.Y, previous.Y));
        force = upper - lower;
        curvature = material.EffectiveStiffness * ((upper > 0 ? 1 : 0) + (lower > 0 ? 1 : 0));
        multiplier = new(upper, lower);
    }

    internal static void Accumulate(Vector3 delta, float distance, Vector3 offset, float side,
        float force, float stiffness, ref AvbdBodyEquation equation)
    {
        if (stiffness == 0) return;
        Vector3 direction = distance > 0 ? delta / distance : Vector3.Zero;
        Vector3 linear = direction * side, angular = Vector3.Cross(offset, linear);
        equation.AddRow(linear, angular, force, stiffness);
        if (force == 0 || distance == 0) return;
        float magnitude = MathF.Abs(force), radiusSquared = offset.LengthSquared(), radial = Vector3.Dot(offset, direction);
        for (int axis = 0; axis < 3; axis++)
        {
            Vector3 unit = axis == 0 ? Vector3.UnitX : axis == 1 ? Vector3.UnitY : Vector3.UnitZ;
            float linearCurvature = magnitude / distance * MathF.Max(0, 1 - direction[axis] * direction[axis]);
            float angularCurvature = MathF.Max(0,
                (radiusSquared - offset[axis] * offset[axis] - angular[axis] * angular[axis]) / distance);
            Vector3 column = offset * direction[axis] - unit * radial;
            equation.AddRow(unit, Vector3.Zero, 0, linearCurvature);
            equation.AddRow(Vector3.Zero, unit, 0, magnitude * (angularCurvature + column.Length()));
        }
    }

    internal static void ReadAnchor(Entity body, Vector3 local, scoped PhysicsBodies bodies, bool start,
        out Vector3 point, out Vector3 offset)
    {
        ref readonly BodyMass mass = ref bodies.ReadMass(body);
        if (start)
        {
            ref readonly BodyStepState step = ref bodies.ReadStepState(body);
            offset = Vector3.Transform(local - mass.CenterOfMass, step.StartRotation);
            point = step.StartCenterOfMass + offset;
        }
        else
        {
            ref readonly BodyPose pose = ref bodies.ReadPose(body);
            offset = Vector3.Transform(local - mass.CenterOfMass, pose.Rotation);
            point = pose.GetCenterOfMass(in mass) + offset;
        }
    }
}
