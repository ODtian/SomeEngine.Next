// Weighted superposition objective adapted from Everything-Will-Be-IK.
// Copyright (c) 2016 Eron Gjoni. MIT license: external/ewbik/LICENSE.
using System.Numerics;

namespace SomeEngine.Animation.Pose;

/// <summary>Weighted proper-rotation fitting through the symmetric quaternion eigenproblem.</summary>
internal sealed class EwbikRotationFitting
{
    private readonly double[] _covariance = new double[9], _matrix = new double[16], _vectors = new double[16];

    internal void Reset() => Array.Clear(_covariance);

    internal void Add(Vector3 source, Vector3 target, float weight)
    {
        double x = (double)weight * source.X, y = (double)weight * source.Y, z = (double)weight * source.Z;
        _covariance[0] += x * target.X; _covariance[1] += x * target.Y; _covariance[2] += x * target.Z;
        _covariance[3] += y * target.X; _covariance[4] += y * target.Y; _covariance[5] += y * target.Z;
        _covariance[6] += z * target.X; _covariance[7] += z * target.Y; _covariance[8] += z * target.Z;
    }

    internal Quaternion Fit()
    {
        double xx = _covariance[0], xy = _covariance[1], xz = _covariance[2], yx = _covariance[3], yy = _covariance[4];
        double yz = _covariance[5], zx = _covariance[6], zy = _covariance[7], zz = _covariance[8];
        Set(0, 0, xx - yy - zz); Set(1, 1, yy - xx - zz); Set(2, 2, zz - xx - yy); Set(3, 3, xx + yy + zz);
        Set(0, 1, xy + yx); Set(0, 2, xz + zx); Set(1, 2, yz + zy);
        Set(0, 3, yz - zy); Set(1, 3, zx - xz); Set(2, 3, xy - yx);
        Array.Clear(_vectors);
        for (int axis = 0; axis < 4; axis++) _vectors[axis * 4 + axis] = 1;
        for (int sweep = 0; sweep < 12; sweep++)
        {
            bool changed = false;
            for (int p = 0; p < 3; p++)
                for (int q = p + 1; q < 4; q++)
                {
                    double cross = _matrix[p * 4 + q], a = _matrix[p * 4 + p], b = _matrix[q * 4 + q];
                    if (Math.Abs(cross) <= 1e-12 * (Math.Abs(a) + Math.Abs(b))) continue;
                    changed = true;
                    double ratio = (b - a) / (2 * cross);
                    double tangent = Math.CopySign(1, ratio) / (Math.Abs(ratio) + Math.Sqrt(1 + ratio * ratio));
                    double cosine = 1 / Math.Sqrt(1 + tangent * tangent), sine = tangent * cosine;
                    _matrix[p * 4 + p] = a - tangent * cross;
                    _matrix[q * 4 + q] = b + tangent * cross;
                    Set(p, q, 0);
                    for (int row = 0; row < 4; row++)
                    {
                        if (row != p && row != q)
                        {
                            double u = _matrix[row * 4 + p], v = _matrix[row * 4 + q];
                            Set(row, p, cosine * u - sine * v);
                            Set(row, q, sine * u + cosine * v);
                        }
                        double first = _vectors[row * 4 + p], second = _vectors[row * 4 + q];
                        _vectors[row * 4 + p] = cosine * first - sine * second;
                        _vectors[row * 4 + q] = sine * first + cosine * second;
                    }
                }
            if (!changed) break;
        }
        int largest = 0;
        for (int column = 1; column < 4; column++)
            if (_matrix[column * 4 + column] > _matrix[largest * 4 + largest]) largest = column;
        double maximum = _matrix[largest * 4 + largest], tolerance = Math.Abs(maximum) * 1e-10;
        double x = 0, y = 0, z = 0, w = 0;
        // Project identity into a tied optimum: position-only headings retain the shortest unconstrained turn.
        for (int column = 0; column < 4; column++)
            if (maximum - _matrix[column * 4 + column] <= tolerance)
            {
                double component = _vectors[12 + column];
                x += _vectors[column] * component; y += _vectors[4 + column] * component;
                z += _vectors[8 + column] * component; w += component * component;
            }
        if (x * x + y * y + z * z + w * w < 1e-24)
        {
            x = _vectors[largest]; y = _vectors[4 + largest]; z = _vectors[8 + largest]; w = _vectors[12 + largest];
        }
        Quaternion rotation = Quaternion.Normalize(new((float)x, (float)y, (float)z, (float)w));
        return rotation.W < 0 ? -rotation : rotation;
    }

    private void Set(int row, int column, double value) => _matrix[row * 4 + column] = _matrix[column * 4 + row] = value;
}
