using SomeEngine.Serialization;
using SomeEngine.Serialization.Containers;

namespace SomeEngine.Examples.Curves;

[BinarySerializable, TextSerializable]
[BinaryFields(nameof(X), nameof(Y))]
public readonly partial record struct Point(float X, float Y);

/// <summary>A piecewise linear curve using already available points. It requires no asset facilities.</summary>
[SomeEngine.Serialization.AssetUse]
public sealed class Curve
{
    private ReadOnlyMemory<Point> _points;
    public Curve(ReadOnlyMemory<Point> points) => _points = CheckPoints(points);
    public float Evaluate(float x)
    {
        ReadOnlySpan<Point> points = _points.Span;
        Point previous = points[0];
        if (x <= previous.X) return previous.Y;
        foreach (Point point in points)
        {
            if (x <= point.X)
                return previous.Y + (point.Y - previous.Y) * ((x - previous.X) / (point.X - previous.X));
            previous = point;
        }
        return previous.Y;
    }
    public void Update(ReadOnlyMemory<Point> points) => _points = CheckPoints(points);
    private static ReadOnlyMemory<Point> CheckPoints(ReadOnlyMemory<Point> points)
    {
        if (points.IsEmpty) throw new ArgumentException("A curve needs at least one point.", nameof(points));
        float previous = float.NegativeInfinity;
        foreach (Point point in points.Span)
        {
            if (!float.IsFinite(point.X) || !float.IsFinite(point.Y) || point.X <= previous)
                throw new ArgumentException("Curve coordinates must be finite and X must increase.", nameof(points));
            previous = point.X;
        }
        return points;
    }
}

/// <summary>An independent use of the same curve definition as evenly spaced samples.</summary>
public sealed class CurveSamples
{
    private readonly float[] _values;
    public CurveSamples(Curve curve, float start, float end, int count)
    {
        if (count < 2) throw new ArgumentOutOfRangeException(nameof(count));
        _values = new float[count];
        for (int i = 0; i < count; i++) _values[i] = curve.Evaluate(start + (end - start) * i / (count - 1));
    }
    public ReadOnlySpan<float> Values => _values;
}
