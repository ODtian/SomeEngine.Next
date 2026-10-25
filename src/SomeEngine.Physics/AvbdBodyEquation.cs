// SPDX-License-Identifier: Apache-2.0
// Copyright (c) 2025 The Newton Developers
// Inertia terms follow Newton v1.7.0 solve_rigid_body and the project's NewtonRigid.cpp.
// Source and license: ThirdParty/Newton/NOTICE and the repository LICENSE.
using System.Numerics;
using System.Runtime.CompilerServices;
using SomeEngine.ECS;

namespace SomeEngine.Physics;

/// <summary>A body's coupled translation/rotation equation and reusable six-dimensional LDL factors.</summary>
public struct AvbdBodyEquation : IComponent
{
    private SymmetricMatrix6 _matrix;
    private SymmetricMatrix6 _factor;
    private Matrix4x4 _bodyInertia;
    public Vector3 LinearGradient;
    public Vector3 AngularGradient;

    internal void Initialize(in BodyMass mass) => _bodyInertia = BodyInertia.InvertTensor(mass.InverseInertia);

    internal void Reset(in BodyPose pose, in BodyMass mass, in BodyStepState state, float duration)
    {
        _matrix = default;
        float inverseStepSquared = 1 / (duration * duration);
        float linearWeight = inverseStepSquared / mass.InverseMass;
        LinearGradient = (pose.GetCenterOfMass(in mass) - state.PredictedCenterOfMass) * linearWeight;
        Vector3 rotationError = BodyRotation.GetRotationVector(Quaternion.Conjugate(pose.Rotation) * state.PredictedRotation);
        AngularGradient = -Vector3.Transform(Vector3.TransformNormal(rotationError, _bodyInertia), pose.Rotation)
            * inverseStepSquared;
        for (int axis = 0; axis < 3; axis++)
        {
            _matrix[GetIndex(axis, axis)] = linearWeight;
            Vector3 direction = axis == 0 ? Vector3.UnitX : axis == 1 ? Vector3.UnitY : Vector3.UnitZ;
            Vector3 column = BodyInertia.Apply(pose.Rotation, _bodyInertia, direction) * inverseStepSquared;
            for (int row = axis; row < 3; row++)
                _matrix[GetIndex(row + 3, axis + 3)] = column[row];
        }
    }

    /// <summary>Adds one force row and its coupled translational/angular curvature.</summary>
    public void AddRow(Vector3 linearJacobian, Vector3 angularJacobian, float force, float stiffness)
    {
        LinearGradient += linearJacobian * force;
        AngularGradient += angularJacobian * force;
        Span<double> jacobian = stackalloc double[6]
        {
            linearJacobian.X, linearJacobian.Y, linearJacobian.Z,
            angularJacobian.X, angularJacobian.Y, angularJacobian.Z,
        };
        for (int row = 0; row < 6; row++)
            for (int column = 0; column <= row; column++)
                _matrix[GetIndex(row, column)] += jacobian[row] * jacobian[column] * stiffness;
    }

    internal void Factorize()
    {
        for (int row = 0; row < 6; row++)
        {
            for (int column = 0; column < row; column++)
            {
                double value = _matrix[GetIndex(row, column)];
                for (int previous = 0; previous < column; previous++)
                    value -= _factor[GetIndex(row, previous)] * _factor[GetIndex(column, previous)] * _factor[GetIndex(previous, previous)];
                _factor[GetIndex(row, column)] = value / _factor[GetIndex(column, column)];
            }
            double diagonal = _matrix[GetIndex(row, row)];
            for (int previous = 0; previous < row; previous++)
            {
                double coefficient = _factor[GetIndex(row, previous)];
                diagonal -= coefficient * coefficient * _factor[GetIndex(previous, previous)];
            }
            _factor[GetIndex(row, row)] = diagonal;
        }
    }

    /// <summary>Uses the current body factorization to find the response to a force and torque.</summary>
    public readonly void SolveResponse(Vector3 force, Vector3 torque, out Vector3 translation, out Vector3 rotation)
    {
        Span<double> response = stackalloc double[6] { force.X, force.Y, force.Z, torque.X, torque.Y, torque.Z };
        for (int row = 0; row < 6; row++)
            for (int column = 0; column < row; column++)
                response[row] -= _factor[GetIndex(row, column)] * response[column];
        for (int row = 0; row < 6; row++) response[row] /= _factor[GetIndex(row, row)];
        for (int row = 5; row >= 0; row--)
            for (int column = row + 1; column < 6; column++)
                response[row] -= _factor[GetIndex(column, row)] * response[column];
        translation = new((float)response[0], (float)response[1], (float)response[2]);
        rotation = new((float)response[3], (float)response[4], (float)response[5]);
    }

    private static int GetIndex(int row, int column) => row * (row + 1) / 2 + column;

    [InlineArray(21)]
    private struct SymmetricMatrix6
    {
        private double _first;
    }
}

internal static class BodyInertia
{
    internal static Matrix4x4 InvertTensor(Matrix4x4 inverse)
    {
        inverse.M14 = inverse.M24 = inverse.M34 = inverse.M41 = inverse.M42 = inverse.M43 = 0;
        inverse.M44 = 1;
        Matrix4x4.Invert(inverse, out Matrix4x4 inertia);
        return inertia;
    }

    internal static Vector3 Apply(Quaternion rotation, Matrix4x4 inertia, Vector3 value) =>
        Vector3.Transform(Vector3.TransformNormal(Vector3.Transform(value, Quaternion.Conjugate(rotation)), inertia), rotation);
}
