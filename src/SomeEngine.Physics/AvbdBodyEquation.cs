// SPDX-License-Identifier: Apache-2.0
// Copyright (c) 2025 The Newton Developers
// Inertia terms follow Newton v1.7.0 solve_rigid_body and the project's NewtonRigid.cpp.
// Source and license: ThirdParty/Newton/NOTICE and the repository LICENSE.
using System.Numerics;
using System.Runtime.CompilerServices;
using SomeEngine.ECS;

namespace SomeEngine.Physics;

/// <summary>A body's active translation/rotation equation and reusable LDL factors.</summary>
public struct AvbdBodyEquation : IComponent
{
    private SymmetricMatrix6 _matrix;
    private SymmetricMatrix6 _factor;
    private Matrix4x4 _bodyInertia;
    private int _dimensions;
    private int _rotationMask;
    private RotationAxes _rotationAxes;
    private bool _curvature;
    public Vector3 LinearGradient;
    public Vector3 AngularGradient;

    internal void Initialize(in BodyMass mass)
    {
        _rotationMask = BodyInertia.GetRotationMask(in mass.InverseInertia);
        _dimensions = 3 + BitOperations.PopCount((uint)_rotationMask);
        _bodyInertia = _rotationMask != 0 ? BodyInertia.InvertTensor(mass.InverseInertia) : default;
    }

    internal void Reset(in BodyPose pose, in BodyMass mass, in BodyStepState state, float duration, bool curvature = true)
    {
        _curvature = curvature;
        float inverseStepSquared = 1 / (duration * duration);
        float linearWeight = inverseStepSquared / mass.InverseMass;
        LinearGradient = (pose.GetCenterOfMass(in mass) - state.PredictedCenterOfMass) * linearWeight;
        AngularGradient = Vector3.Zero;
        if (_dimensions > 3)
        {
            Vector3 rotationError = BodyRotation.GetRotationVector(Quaternion.Conjugate(pose.Rotation) * state.PredictedRotation);
            AngularGradient = -Vector3.Transform(Vector3.TransformNormal(rotationError, _bodyInertia), pose.Rotation)
                * inverseStepSquared;
        }
        if (!curvature) return;
        _matrix = default;
        for (int axis = 0; axis < 3; axis++)
        {
            _matrix[GetIndex(axis, axis)] = linearWeight;
        }
        int active = 0;
        for (int axis = 0; axis < 3; axis++)
        {
            if ((_rotationMask & (1 << axis)) == 0) continue;
            Vector3 direction = axis == 0 ? Vector3.UnitX : axis == 1 ? Vector3.UnitY : Vector3.UnitZ;
            _rotationAxes[active++] = _rotationMask == 7 ? direction : Vector3.Transform(direction, pose.Rotation);
        }
        for (int column = 0; column < active; column++)
        {
            Vector3 response = BodyInertia.Apply(pose.Rotation, _bodyInertia, _rotationAxes[column]) * inverseStepSquared;
            for (int row = column; row < active; row++)
                _matrix[GetIndex(row + 3, column + 3)] = Vector3.Dot(_rotationAxes[row], response);
        }
    }

    /// <summary>Adds one force row and its coupled translational/angular curvature.</summary>
    public void AddRow(Vector3 linearJacobian, Vector3 angularJacobian, float force, float stiffness)
    {
        if (_dimensions == 3) angularJacobian = Vector3.Zero;
        LinearGradient += linearJacobian * force;
        AngularGradient += angularJacobian * force;
        if (!_curvature) return;
        Span<double> jacobian = stackalloc double[6] { linearJacobian.X, linearJacobian.Y, linearJacobian.Z, 0, 0, 0 };
        for (int axis = 0; axis < _dimensions - 3; axis++) jacobian[axis + 3] = Vector3.Dot(angularJacobian, _rotationAxes[axis]);
        for (int row = 0; row < _dimensions; row++)
            for (int column = 0; column <= row; column++)
                _matrix[GetIndex(row, column)] += jacobian[row] * jacobian[column] * stiffness;
    }

    internal readonly double GetCurvature(Vector3 translation, Vector3 rotation)
    {
        Span<double> change = stackalloc double[6] { translation.X, translation.Y, translation.Z, 0, 0, 0 };
        for (int axis = 0; axis < _dimensions - 3; axis++) change[axis + 3] = Vector3.Dot(rotation, _rotationAxes[axis]);
        double curvature = 0;
        for (int row = 0; row < _dimensions; row++)
            for (int column = 0; column <= row; column++)
                curvature += change[row] * change[column] * _matrix[GetIndex(row, column)] * (row == column ? 1 : 2);
        return curvature;
    }

    internal void Factorize()
    {
        for (int row = 0; row < _dimensions; row++)
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
        Span<double> response = stackalloc double[6] { force.X, force.Y, force.Z, 0, 0, 0 };
        for (int axis = 0; axis < _dimensions - 3; axis++) response[axis + 3] = Vector3.Dot(torque, _rotationAxes[axis]);
        for (int row = 0; row < _dimensions; row++)
            for (int column = 0; column < row; column++)
                response[row] -= _factor[GetIndex(row, column)] * response[column];
        for (int row = 0; row < _dimensions; row++) response[row] /= _factor[GetIndex(row, row)];
        for (int row = _dimensions - 1; row >= 0; row--)
            for (int column = row + 1; column < _dimensions; column++)
                response[row] -= _factor[GetIndex(column, row)] * response[column];
        translation = new((float)response[0], (float)response[1], (float)response[2]);
        rotation = Vector3.Zero;
        for (int axis = 0; axis < _dimensions - 3; axis++) rotation += _rotationAxes[axis] * (float)response[axis + 3];
    }

    private static int GetIndex(int row, int column) => row * (row + 1) / 2 + column;

    [InlineArray(21)]
    private struct SymmetricMatrix6
    {
        private double _first;
    }

    [InlineArray(3)]
    private struct RotationAxes
    {
        private Vector3 _first;
    }
}

internal static class BodyInertia
{
    internal static int GetRotationMask(in Matrix4x4 inverse) =>
        (inverse.M11 != 0 ? 1 : 0) | (inverse.M22 != 0 ? 2 : 0) | (inverse.M33 != 0 ? 4 : 0);

    internal static bool HasRotation(in Matrix4x4 inverse) => GetRotationMask(in inverse) != 0;

    internal static Matrix4x4 InvertTensor(Matrix4x4 inverse)
    {
        inverse.M14 = inverse.M24 = inverse.M34 = inverse.M41 = inverse.M42 = inverse.M43 = 0;
        inverse.M44 = 1;
        int mask = GetRotationMask(in inverse);
        if ((mask & 1) == 0) inverse.M11 = 1;
        if ((mask & 2) == 0) inverse.M22 = 1;
        if ((mask & 4) == 0) inverse.M33 = 1;
        Matrix4x4.Invert(inverse, out Matrix4x4 inertia);
        if ((mask & 1) == 0) inertia.M11 = 0;
        if ((mask & 2) == 0) inertia.M22 = 0;
        if ((mask & 4) == 0) inertia.M33 = 0;
        return inertia;
    }

    internal static Vector3 Apply(Quaternion rotation, Matrix4x4 inertia, Vector3 value) =>
        Vector3.Transform(Vector3.TransformNormal(Vector3.Transform(value, Quaternion.Conjugate(rotation)), inertia), rotation);
}
