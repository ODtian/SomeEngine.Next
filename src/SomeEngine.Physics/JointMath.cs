// SPDX-License-Identifier: Apache-2.0
// Copyright (c) 2025 The Newton Developers
// Relative rotation vectors and joint Jacobians follow Newton v1.7.0 through NewtonRigid.cpp.
// Source and license: ThirdParty/Newton/NOTICE and the repository LICENSE.
using System.Numerics;

namespace SomeEngine.Physics;

internal enum JointLimitSide : byte
{
    Locked,
    Lower,
    Upper,
}

internal readonly record struct JointEquationRow(float Coordinate, Vector3 SourceLinear,
    Vector3 SourceAngular, Vector3 TargetLinear, Vector3 TargetAngular);

internal static class JointMath
{
    internal static void GetCoordinates(in JointConstraint constraint, in BodyPose source, in BodyPose target,
        out Vector3 linear, out Vector3 angular)
    {
        Vector3 sourcePoint = source.Position + Vector3.Transform(constraint.SourceFrame.Position, source.Rotation);
        Vector3 targetPoint = target.Position + Vector3.Transform(constraint.TargetFrame.Position, target.Rotation);
        Quaternion sourceFrame = source.Rotation * constraint.SourceFrame.Rotation;
        Quaternion targetFrame = target.Rotation * constraint.TargetFrame.Rotation;
        linear = Vector3.Transform(targetPoint - sourcePoint, Quaternion.Conjugate(sourceFrame));
        angular = BodyRotation.GetRotationVector(Quaternion.Conjugate(sourceFrame) * targetFrame);
    }

    internal static JointEquationRow GetRow(in JointConstraint constraint, in BodyPose source, in BodyMass sourceMass,
        in BodyPose target, in BodyMass targetMass, int dof)
    {
        int axis = dof % 3;
        Vector3 localAxis = axis == 0 ? Vector3.UnitX : axis == 1 ? Vector3.UnitY : Vector3.UnitZ;
        Quaternion sourceFrame = source.Rotation * constraint.SourceFrame.Rotation;
        if (dof < 3)
        {
            Vector3 sourcePoint = source.Position + Vector3.Transform(constraint.SourceFrame.Position, source.Rotation);
            Vector3 targetPoint = target.Position + Vector3.Transform(constraint.TargetFrame.Position, target.Rotation);
            Vector3 direction = Vector3.Transform(localAxis, sourceFrame);
            return new(Vector3.Dot(targetPoint - sourcePoint, direction), -direction,
                -Vector3.Cross(targetPoint - source.GetCenterOfMass(in sourceMass), direction), direction,
                Vector3.Cross(targetPoint - target.GetCenterOfMass(in targetMass), direction));
        }
        Quaternion targetFrame = target.Rotation * constraint.TargetFrame.Rotation;
        Quaternion relative = Quaternion.Conjugate(sourceFrame) * targetFrame;
        Vector3 error = BodyRotation.GetRotationVector(relative);
        Vector3 derivative = BodyRotation.GetConstraintJacobian(sourceFrame, relative, error, localAxis);
        return new(error[axis], Vector3.Zero, -derivative, Vector3.Zero, derivative);
    }

    internal static JointAxisLimit GetLimit(in JointConstraint constraint, int dof) => dof switch
    {
        0 => constraint.LinearX,
        1 => constraint.LinearY,
        2 => constraint.LinearZ,
        3 => constraint.AngularX,
        4 => constraint.AngularY,
        _ => constraint.AngularZ,
    };

    internal static float GetInitialError(JointAxisLimit limit, float coordinate) => limit.Motion switch
    {
        JointAxisMotion.Free => 0,
        JointAxisMotion.Locked => coordinate,
        _ => coordinate - Math.Clamp(coordinate, limit.Minimum, limit.Maximum),
    };

    internal static bool TryGetError(JointAxisLimit limit, float coordinate, float force,
        out float error, out JointLimitSide side)
    {
        error = coordinate;
        side = JointLimitSide.Locked;
        if (limit.Motion == JointAxisMotion.Free) return false;
        if (limit.Motion == JointAxisMotion.Locked) return true;
        if (coordinate < limit.Minimum || (coordinate <= limit.Maximum && force < 0))
        {
            error = coordinate - limit.Minimum;
            side = JointLimitSide.Lower;
            return true;
        }
        if (coordinate > limit.Maximum || force > 0)
        {
            error = coordinate - limit.Maximum;
            side = JointLimitSide.Upper;
            return true;
        }
        return false;
    }

    internal static float ProjectForce(JointLimitSide side, float force) => side switch
    {
        JointLimitSide.Lower => MathF.Min(0, force),
        JointLimitSide.Upper => MathF.Max(0, force),
        _ => force,
    };
}
