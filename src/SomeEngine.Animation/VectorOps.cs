using System.Numerics;

namespace SomeEngine.Animation;

public readonly struct Vector2Ops : IValueOps<Vector2>
{
    public static bool Equals(in Vector2 a, in Vector2 b) => a.Equals(b);
    public static Vector2 Lerp(in Vector2 a, in Vector2 b, float t) => Vector2.Lerp(a, b, t);
    public static Vector2 Add(in Vector2 a, in Vector2 b) => a + b;
    public static Vector2 Subtract(in Vector2 a, in Vector2 b) => a - b;
    public static Vector2 Scale(in Vector2 a, float scale) => a * scale;
    public static float Distance(in Vector2 a, in Vector2 b) => Vector2.Distance(a, b);
}

public readonly struct Vector3Ops : IValueOps<Vector3>
{
    public static bool Equals(in Vector3 a, in Vector3 b) => a.Equals(b);
    public static Vector3 Lerp(in Vector3 a, in Vector3 b, float t) => Vector3.Lerp(a, b, t);
    public static Vector3 Add(in Vector3 a, in Vector3 b) => a + b;
    public static Vector3 Subtract(in Vector3 a, in Vector3 b) => a - b;
    public static Vector3 Scale(in Vector3 a, float scale) => a * scale;
    public static float Distance(in Vector3 a, in Vector3 b) => Vector3.Distance(a, b);
}

public readonly struct Vector4Ops : IValueOps<Vector4>
{
    public static bool Equals(in Vector4 a, in Vector4 b) => a.Equals(b);
    public static Vector4 Lerp(in Vector4 a, in Vector4 b, float t) => Vector4.Lerp(a, b, t);
    public static Vector4 Add(in Vector4 a, in Vector4 b) => a + b;
    public static Vector4 Subtract(in Vector4 a, in Vector4 b) => a - b;
    public static Vector4 Scale(in Vector4 a, float scale) => a * scale;
    public static float Distance(in Vector4 a, in Vector4 b) => Vector4.Distance(a, b);
}
