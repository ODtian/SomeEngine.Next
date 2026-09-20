using SomeEngine.Serialization;
using SomeEngine.Serialization.Containers;

namespace SomeEngine.Examples.Curves;

[BinarySerializable, TextSerializable]
[BinaryFields(nameof(X), nameof(Y))]
public readonly partial record struct Point(float X, float Y);

/// <summary>A piecewise linear curve using already available points. It requires no asset facilities.</summary>
[SomeEngine.Serialization.AssetUse]
public sealed class Curve : IDisposable
{
    private PageRange<Point>? _points;

    /// <summary>Takes responsibility for the supplied read, including disposal if the points are invalid.</summary>
    public Curve(PageRange<Point> points) => _points = CheckPoints(points);

    public float Evaluate(float x)
    {
        var points = (_points ?? throw new ObjectDisposedException(nameof(Curve))).Sequence;
        Point previous = points.FirstSpan[0];
        if (x <= previous.X) return previous.Y;
        foreach (var segment in points)
            foreach (Point point in segment.Span)
            {
                if (x <= point.X)
                    return previous.Y + (point.Y - previous.Y) * ((x - previous.X) / (point.X - previous.X));
                previous = point;
            }
        return previous.Y;
    }

    /// <summary>Replaces points on this curve. Like Evaluate, callers serialize access to this operation.</summary>
    public void Update(PageRange<Point> points)
    {
        PageRange<Point> next = CheckPoints(points);
        if (_points is null) { next.Dispose(); throw new ObjectDisposedException(nameof(Curve)); }
        PageRange<Point> previous = _points;
        _points = next;
        previous.Dispose();
    }

    private static PageRange<Point> CheckPoints(PageRange<Point> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        try
        {
            if (points.Sequence.IsEmpty) throw new ArgumentException("A curve needs at least one point.", nameof(points));
            float previous = float.NegativeInfinity;
            foreach (var segment in points.Sequence)
                foreach (Point point in segment.Span)
                {
                    if (!float.IsFinite(point.X) || !float.IsFinite(point.Y) || point.X <= previous)
                        throw new ArgumentException("Curve coordinates must be finite and X must increase.", nameof(points));
                    previous = point.X;
                }
            return points;
        }
        catch { points.Dispose(); throw; }
    }

    public void Dispose() { _points?.Dispose(); _points = null; }
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
