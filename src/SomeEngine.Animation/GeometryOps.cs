using System.Numerics;
using SomeEngine.Core.Math;

namespace SomeEngine.Animation;

public readonly struct ColorOps : IValueOps<Color>
{
    public static Color Lerp(in Color a, in Color b, float t) => FromVector(Vector4.Lerp(ToVector(a), ToVector(b), t));
    public static Color Add(in Color a, in Color b) => FromVector(ToVector(a) + ToVector(b));
    public static Color Subtract(in Color a, in Color b) => FromVector(ToVector(a) - ToVector(b));
    public static Color Scale(in Color a, float scale) => FromVector(ToVector(a) * scale);
    public static float Distance(in Color a, in Color b) => Vector4.Distance(ToVector(a), ToVector(b));

    private static Vector4 ToVector(in Color value) => new(value.R, value.G, value.B, value.A);
    private static Color FromVector(Vector4 value) => new(value.X, value.Y, value.Z, value.W);
}

public readonly struct ThicknessOps : IValueOps<Thickness>
{
    public static Thickness Lerp(in Thickness a, in Thickness b, float t) => FromVector(Vector4.Lerp(ToVector(a), ToVector(b), t));
    public static Thickness Add(in Thickness a, in Thickness b) => FromVector(ToVector(a) + ToVector(b));
    public static Thickness Subtract(in Thickness a, in Thickness b) => FromVector(ToVector(a) - ToVector(b));
    public static Thickness Scale(in Thickness a, float scale) => FromVector(ToVector(a) * scale);
    public static float Distance(in Thickness a, in Thickness b) => Vector4.Distance(ToVector(a), ToVector(b));

    private static Vector4 ToVector(in Thickness value) => new(value.Left, value.Top, value.Right, value.Bottom);
    private static Thickness FromVector(Vector4 value) => new(value.X, value.Y, value.Z, value.W);
}

public readonly struct RectOps : IValueOps<Rect>
{
    public static Rect Lerp(in Rect a, in Rect b, float t) => FromVector(Vector4.Lerp(ToVector(a), ToVector(b), t));
    public static Rect Add(in Rect a, in Rect b) => FromVector(ToVector(a) + ToVector(b));
    public static Rect Subtract(in Rect a, in Rect b) => FromVector(ToVector(a) - ToVector(b));
    public static Rect Scale(in Rect a, float scale) => FromVector(ToVector(a) * scale);
    public static float Distance(in Rect a, in Rect b) => Vector4.Distance(ToVector(a), ToVector(b));

    private static Vector4 ToVector(in Rect value) => new(value.X, value.Y, value.Width, value.Height);
    private static Rect FromVector(Vector4 value) => new(value.X, value.Y, value.Z, value.W);
}
