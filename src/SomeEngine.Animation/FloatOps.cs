namespace SomeEngine.Animation;

public readonly struct FloatOps : IValueOps<float>
{
    public static bool Equals(in float a, in float b) => a.Equals(b);
    public static float Lerp(in float a, in float b, float t) => a + (b - a) * t;
    public static float Add(in float a, in float b) => a + b;
    public static float Subtract(in float a, in float b) => a - b;
    public static float Scale(in float a, float scale) => a * scale;
    public static float Distance(in float a, in float b) => MathF.Abs(a - b);
}
