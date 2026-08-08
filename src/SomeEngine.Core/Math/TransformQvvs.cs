using System.Numerics;

namespace SomeEngine.Core.Math;

/// <summary>
/// QVVS Transform: Quaternion, Vector (Position), Vector (Stretch), Scale
/// Based on Latios Framework concepts.
/// </summary>
public struct TransformQvvs(Vector3 position, Quaternion rotation, float scale = 1.0f)
{
    public Quaternion Rotation = rotation;
    public Vector3 Position = position;
    public Vector3 Stretch = Vector3.One;
    public float Scale = scale;

    public static readonly TransformQvvs Identity = new()
    {
        Rotation = Quaternion.Identity,
        Position = Vector3.Zero,
        Stretch = Vector3.One,
        Scale = 1.0f,
    };

    public readonly Matrix4x4 ToMatrix()
    {
        if (Rotation.X == 0.0f
            && Rotation.Y == 0.0f
            && Rotation.Z == 0.0f
            && Rotation.W == 1.0f
            && Stretch.X == 1.0f
            && Stretch.Y == 1.0f
            && Stretch.Z == 1.0f)
        {
            return new Matrix4x4(
                Scale,
                0.0f,
                0.0f,
                0.0f,
                0.0f,
                Scale,
                0.0f,
                0.0f,
                0.0f,
                0.0f,
                Scale,
                0.0f,
                Position.X,
                Position.Y,
                Position.Z,
                1.0f);
        }

        Vector3 scale = Stretch * Scale;
        Quaternion q = Rotation;
        float xx = q.X * q.X;
        float yy = q.Y * q.Y;
        float zz = q.Z * q.Z;
        float xy = q.X * q.Y;
        float zw = q.Z * q.W;
        float xz = q.X * q.Z;
        float yw = q.Y * q.W;
        float yz = q.Y * q.Z;
        float xw = q.X * q.W;

        return new Matrix4x4(
            scale.X * (1.0f - (2.0f * (yy + zz))),
            scale.X * (2.0f * (xy + zw)),
            scale.X * (2.0f * (xz - yw)),
            0.0f,
            scale.Y * (2.0f * (xy - zw)),
            scale.Y * (1.0f - (2.0f * (xx + zz))),
            scale.Y * (2.0f * (yz + xw)),
            0.0f,
            scale.Z * (2.0f * (xz + yw)),
            scale.Z * (2.0f * (yz - xw)),
            scale.Z * (1.0f - (2.0f * (xx + yy))),
            0.0f,
            Position.X,
            Position.Y,
            Position.Z,
            1.0f);
    }

    public static TransformQvvs Combine(in TransformQvvs parent, in TransformQvvs local)
    {
        var scaledLocalPos = local.Position * (parent.Stretch * parent.Scale);
        var rotatedLocalPos = Vector3.Transform(scaledLocalPos, parent.Rotation);

        return new TransformQvvs
        {
            Rotation = parent.Rotation * local.Rotation,
            Scale = parent.Scale * local.Scale,
            Stretch = parent.Stretch * local.Stretch,
            Position = parent.Position + rotatedLocalPos,
        };
    }

    /// <summary>
    /// Transforms a point from local space to world space.
    /// </summary>
    public readonly Vector3 TransformPoint(Vector3 point)
    {
        // p' = p + q * (s * v * x)
        var scaled = point * (Stretch * Scale);
        var rotated = Vector3.Transform(scaled, Rotation);
        return Position + rotated;
    }

    /// <summary>
    /// Transforms a direction vector (ignores translation).
    /// </summary>
    public readonly Vector3 TransformDirection(Vector3 direction)
    {
        var scaled = direction * (Stretch * Scale);
        return Vector3.Transform(scaled, Rotation);
    }

    /// <summary>
    /// Returns the inverse of this transform.
    /// </summary>
    public readonly bool TryInverse(out TransformQvvs inverse)
    {
        if (!IsFinite() ||
            !Matrix4x4.Invert(ToMatrix(), out Matrix4x4 inverseMatrix) ||
            !TryCreateFromMatrix(inverseMatrix, out inverse))
        {
            inverse = default;
            return false;
        }

        return true;
    }

    /// <summary>
    /// Returns the inverse transform, or throws when scale, stretch, rotation, or
    /// input values make the transform non-invertible.
    /// </summary>
    public readonly TransformQvvs Inverse()
    {
        if (TryInverse(out TransformQvvs inverse))
            return inverse;

        throw new InvalidOperationException(
            "Cannot invert a degenerate or non-finite QVVS transform, or its inverse " +
            "cannot be represented without shear.");
    }

    /// <summary>
    /// Reconstructs a QVVS only when the affine matrix contains no shear or other
    /// information that the QVVS representation would discard.
    /// </summary>
    internal static bool TryCreateFromMatrix(
        in Matrix4x4 matrix,
        out TransformQvvs transform)
    {
        if (!IsFinite(matrix) ||
            !ApproximatelyEqual(matrix.M14, 0.0f) ||
            !ApproximatelyEqual(matrix.M24, 0.0f) ||
            !ApproximatelyEqual(matrix.M34, 0.0f) ||
            !ApproximatelyEqual(matrix.M44, 1.0f) ||
            !Matrix4x4.Decompose(matrix, out Vector3 stretch, out Quaternion rotation, out Vector3 position))
        {
            transform = default;
            return false;
        }

        float rotationLengthSquared = rotation.LengthSquared();
        if (!float.IsFinite(rotationLengthSquared) || rotationLengthSquared == 0.0f)
        {
            transform = default;
            return false;
        }

        transform = new TransformQvvs
        {
            Position = position,
            Rotation = Quaternion.Normalize(rotation),
            Stretch = stretch,
            Scale = 1.0f,
        };

        if (!transform.IsFinite() || !MatrixApproximatelyEquals(transform.ToMatrix(), matrix))
        {
            transform = default;
            return false;
        }

        return true;
    }

    internal static bool MatrixApproximatelyEquals(in Matrix4x4 left, in Matrix4x4 right)
    {
        return ApproximatelyEqual(left.M11, right.M11) &&
               ApproximatelyEqual(left.M12, right.M12) &&
               ApproximatelyEqual(left.M13, right.M13) &&
               ApproximatelyEqual(left.M14, right.M14) &&
               ApproximatelyEqual(left.M21, right.M21) &&
               ApproximatelyEqual(left.M22, right.M22) &&
               ApproximatelyEqual(left.M23, right.M23) &&
               ApproximatelyEqual(left.M24, right.M24) &&
               ApproximatelyEqual(left.M31, right.M31) &&
               ApproximatelyEqual(left.M32, right.M32) &&
               ApproximatelyEqual(left.M33, right.M33) &&
               ApproximatelyEqual(left.M34, right.M34) &&
               ApproximatelyEqual(left.M41, right.M41) &&
               ApproximatelyEqual(left.M42, right.M42) &&
               ApproximatelyEqual(left.M43, right.M43) &&
               ApproximatelyEqual(left.M44, right.M44);
    }

    private static bool ApproximatelyEqual(float left, float right)
    {
        const float absoluteTolerance = 1.0e-5f;
        const float relativeTolerance = 1.0e-5f;
        float difference = MathF.Abs(left - right);
        float scale = MathF.Max(MathF.Abs(left), MathF.Abs(right));
        return difference <= absoluteTolerance + (relativeTolerance * scale);
    }

    private static bool IsFinite(in Matrix4x4 matrix)
    {
        return float.IsFinite(matrix.M11) &&
               float.IsFinite(matrix.M12) &&
               float.IsFinite(matrix.M13) &&
               float.IsFinite(matrix.M14) &&
               float.IsFinite(matrix.M21) &&
               float.IsFinite(matrix.M22) &&
               float.IsFinite(matrix.M23) &&
               float.IsFinite(matrix.M24) &&
               float.IsFinite(matrix.M31) &&
               float.IsFinite(matrix.M32) &&
               float.IsFinite(matrix.M33) &&
               float.IsFinite(matrix.M34) &&
               float.IsFinite(matrix.M41) &&
               float.IsFinite(matrix.M42) &&
               float.IsFinite(matrix.M43) &&
               float.IsFinite(matrix.M44);
    }

    public readonly bool IsFinite()
    {
        return float.IsFinite(Position.X) &&
               float.IsFinite(Position.Y) &&
               float.IsFinite(Position.Z) &&
               float.IsFinite(Rotation.X) &&
               float.IsFinite(Rotation.Y) &&
               float.IsFinite(Rotation.Z) &&
               float.IsFinite(Rotation.W) &&
               float.IsFinite(Stretch.X) &&
               float.IsFinite(Stretch.Y) &&
               float.IsFinite(Stretch.Z) &&
               float.IsFinite(Scale);
    }
}

