using System.Numerics;
using SomeEngine.Core.Math;

namespace SomeEngine.Animation.Tests;

public class ValueOpsTests
{
    [Fact]
    public void UserValueOperationsReceiveDefaultBatchesAndSpringSampling()
    {
        Check<Temperature, TemperatureOps>(new(2), new(6), new(3), new(8), new(-4), new(1), 4, new(5));
        Temperature current = new(2);
        Temperature velocity = default;
        Curve spring = Spec.Spring(180, 20).Curve;

        spring.Step<Temperature, TemperatureOps>(1f / 60, new Temperature(6), ref current, ref velocity);

        Assert.Equal(2.1410417f, current.Celsius, 5);
        Assert.Equal(10.925f, velocity.Celsius, 4);
    }

    public readonly record struct Temperature(float Celsius);

    public readonly struct TemperatureOps : IValueOps<Temperature>
    {
        public static Temperature Lerp(in Temperature a, in Temperature b, float t) => new(FloatOps.Lerp(a.Celsius, b.Celsius, t));
        public static Temperature Add(in Temperature a, in Temperature b) => new(a.Celsius + b.Celsius);
        public static Temperature Subtract(in Temperature a, in Temperature b) => new(a.Celsius - b.Celsius);
        public static Temperature Scale(in Temperature a, float scale) => new(a.Celsius * scale);
        public static float Distance(in Temperature a, in Temperature b) => MathF.Abs(a.Celsius - b.Celsius);
    }

    [Fact]
    public void ColorThicknessAndRectArithmeticAndDefaultBatchOperationsAgree()
    {
        Check<Color, ColorOps>(new(2, 4, 6, 8), new(6, 8, 10, 12), new(3, 5, 7, 9), new(8, 12, 16, 20), new(-4, -4, -4, -4), new(1, 2, 3, 4), 8, new(5, 8, 11, 14));
        Check<Thickness, ThicknessOps>(new(2, 4, 6, 8), new(6, 8, 10, 12), new(3, 5, 7, 9), new(8, 12, 16, 20), new(-4, -4, -4, -4), new(1, 2, 3, 4), 8, new(5, 8, 11, 14));
        Check<Rect, RectOps>(new(2, 4, 6, 8), new(6, 8, 10, 12), new(3, 5, 7, 9), new(8, 12, 16, 20), new(-4, -4, -4, -4), new(1, 2, 3, 4), 8, new(5, 8, 11, 14));
    }

    [Fact]
    public void QuaternionAndMatrixArithmeticAndDefaultBatchOperationsAgree()
    {
        Check<Quaternion, QuaternionOps>(new(0, 0, 0, 1), new(0, 0, 1, 0), new(0, 0, 0.31622777f, 0.9486833f),
            new(0, 0, 1, 1), new(0, 0, -1, 1), new(0, 0, 0, 0.5f), 1.4142135f, new(0, 0, 0.5f, 1));
        Check<Matrix3x2, Matrix3x2Ops>(new(2, 4, 6, 8, 10, 12), new(6, 8, 10, 12, 14, 16), new(3, 5, 7, 9, 11, 13),
            new(8, 12, 16, 20, 24, 28), new(-4, -4, -4, -4, -4, -4), new(1, 2, 3, 4, 5, 6), 9.797959f, new(5, 8, 11, 14, 17, 20));
    }

    [Fact]
    public void VectorArithmeticAndDefaultBatchOperationsAgree()
    {
        Check<Vector2, Vector2Ops>(new(2, 4), new(6, 8), new(3, 5), new(8, 12), new(-4, -4), new(1, 2), 5.656854f, new(5, 8));
        Check<Vector3, Vector3Ops>(new(2, 4, 6), new(6, 8, 10), new(3, 5, 7), new(8, 12, 16), new(-4), new(1, 2, 3), 6.928203f, new(5, 8, 11));
        Check<Vector4, Vector4Ops>(new(2, 4, 6, 8), new(6, 8, 10, 12), new(3, 5, 7, 9), new(8, 12, 16, 20), new(-4), new(1, 2, 3, 4), 8, new(5, 8, 11, 14));
    }

    [Fact]
    public void FloatArithmeticAndDefaultBatchOperationsAgree()
    {
        Check<float, FloatOps>(2, 6, 3, 8, -4, 1, 4, 5);
    }

    private static void Check<T, TOps>(T a, T b, T lerp, T sum, T difference, T scaled, float distance, T weightedSum)
        where T : unmanaged
        where TOps : struct, IValueOps<T>
    {
        Assert.InRange(TOps.Distance(lerp, TOps.Lerp(a, b, 0.25f)), 0, 0.00001f);
        Assert.Equal(sum, TOps.Add(a, b));
        Assert.Equal(difference, TOps.Subtract(a, b));
        Assert.Equal(scaled, TOps.Scale(a, 0.5f));
        Assert.Equal(distance, TOps.Distance(a, b), 5);
        T[] result = new T[2];
        T[] first = [a, a];
        T[] second = [b, b];
        ValueOps<T, TOps>.Lerp(first, second, 0.25f, result);
        foreach (T value in result)
            Assert.InRange(TOps.Distance(lerp, value), 0, 0.00001f);
        ValueOps<T, TOps>.Add(first, second, 0.5f, result);
        Assert.Equal(new[] { weightedSum, weightedSum }, result);
        ValueOps<T, TOps>.Subtract(first, second, result);
        Assert.Equal(new[] { difference, difference }, result);
        ValueOps<T, TOps>.Scale(first, 0.5f, result);
        Assert.Equal(new[] { scaled, scaled }, result);
        float[] distances = new float[2];
        ValueOps<T, TOps>.Distance(first, second, distances);
        foreach (float value in distances)
            Assert.Equal(distance, value, 5);
    }
}
