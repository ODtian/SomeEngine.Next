using System.Numerics;

namespace SomeEngine.Animation.Pose;

internal static class InertialPoseMath
{
    internal static Vector3 Log(Vector3 value) => new(MathF.Log(value.X), MathF.Log(value.Y), MathF.Log(value.Z));
    internal static Vector3 Exp(Vector3 value) => new(MathF.Exp(value.X), MathF.Exp(value.Y), MathF.Exp(value.Z));

    internal static Vector3 ToRotationVector(Quaternion rotation)
    {
        if (rotation.W < 0) rotation = -rotation;
        Vector3 vector = new(rotation.X, rotation.Y, rotation.Z);
        float length = vector.Length();
        return length == 0 ? Vector3.Zero : vector * (2 * MathF.Atan2(length, rotation.W) / length);
    }

    internal static Quaternion ToQuaternion(Vector3 rotation)
    {
        float angle = rotation.Length();
        return angle == 0 ? Quaternion.Identity : new Quaternion(rotation * (MathF.Sin(angle * .5f) / angle), MathF.Cos(angle * .5f));
    }

    // SO(3) left Jacobian maps the derivative of log rotation to parent-frame angular velocity.
    internal static Vector3 ToAngularVelocity(Vector3 rotation, Vector3 rate)
    {
        float squared = rotation.LengthSquared();
        float first, second;
        if (squared < .0001f)
        {
            first = .5f - squared / 24 + squared * squared / 720;
            second = 1f / 6 - squared / 120 + squared * squared / 5040;
        }
        else
        {
            float angle = MathF.Sqrt(squared);
            first = (1 - MathF.Cos(angle)) / squared;
            second = (angle - MathF.Sin(angle)) / (squared * angle);
        }
        Vector3 cross = Vector3.Cross(rotation, rate);
        return rate + first * cross + second * Vector3.Cross(rotation, cross);
    }

    internal static Vector3 ToRotationVectorRate(Vector3 rotation, Vector3 angularVelocity)
    {
        float squared = rotation.LengthSquared();
        float second;
        if (squared < .0001f) second = 1f / 12 + squared / 720 + squared * squared / 30240;
        else
        {
            float halfAngle = MathF.Sqrt(squared) * .5f;
            second = (1 - halfAngle / MathF.Tan(halfAngle)) / squared;
        }
        Vector3 cross = Vector3.Cross(rotation, angularVelocity);
        return angularVelocity - .5f * cross + second * Vector3.Cross(rotation, cross);
    }
}
