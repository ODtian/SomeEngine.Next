using System.Numerics;

namespace SomeEngine.Animation;

public readonly struct QuaternionOps : IValueOps<Quaternion>
{
    public static Quaternion Lerp(in Quaternion a, in Quaternion b, float t) => Quaternion.Lerp(a, b, t);
    public static Quaternion Add(in Quaternion a, in Quaternion b) => a + b;
    public static Quaternion Subtract(in Quaternion a, in Quaternion b) => a - b;
    public static Quaternion Scale(in Quaternion a, float scale) => a * scale;
    public static float Distance(in Quaternion a, in Quaternion b) => (a - b).Length();
}

public readonly struct Matrix3x2Ops : IValueOps<Matrix3x2>
{
    public static Matrix3x2 Lerp(in Matrix3x2 a, in Matrix3x2 b, float t) => Matrix3x2.Lerp(a, b, t);
    public static Matrix3x2 Add(in Matrix3x2 a, in Matrix3x2 b) => a + b;
    public static Matrix3x2 Subtract(in Matrix3x2 a, in Matrix3x2 b) => a - b;
    public static Matrix3x2 Scale(in Matrix3x2 a, float scale) => a * scale;
    public static float Distance(in Matrix3x2 a, in Matrix3x2 b)
    {
        Matrix3x2 difference = a - b;
        return MathF.Sqrt(difference.M11 * difference.M11 + difference.M12 * difference.M12 +
            difference.M21 * difference.M21 + difference.M22 * difference.M22 +
            difference.M31 * difference.M31 + difference.M32 * difference.M32);
    }
}
