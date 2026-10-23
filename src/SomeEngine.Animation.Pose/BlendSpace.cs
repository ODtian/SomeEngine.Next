using System.Numerics;

namespace SomeEngine.Animation.Pose;

public readonly record struct BlendPoint1D(float Coordinate, AnimationClip Clip, float PhaseOffset = 0);
public readonly record struct BlendPoint2D(Vector2 Coordinate, AnimationClip Clip, float PhaseOffset = 0);
public readonly record struct BlendTriangle(int A, int B, int C);
public readonly record struct BlendSpaceSample(Vector2 Coordinate, AnimationClip Clip, float PhaseOffset);
public readonly record struct BlendSpaceInterval(int A, int B, float From, float InverseWidth);

/// <summary>Shared looping clips, phase offsets and interpolation intervals or triangles.</summary>
public sealed class BlendSpace
{
    private readonly BlendSpaceSample[] _samples;
    private readonly BlendSpaceInterval[] _intervals;
    private readonly BlendTriangle[] _triangles;
    private readonly TriangleCoordinates[] _coordinates;

    public ReadOnlySpan<BlendSpaceSample> Samples => _samples;
    public ReadOnlySpan<BlendSpaceInterval> Intervals => _intervals;
    public ReadOnlySpan<BlendTriangle> Triangles => _triangles;

    private BlendSpace(BlendSpaceSample[] samples, BlendSpaceInterval[] intervals, BlendTriangle[] triangles)
    {
        (_samples, _intervals, _triangles) = (samples, intervals, triangles);
        _coordinates = new TriangleCoordinates[triangles.Length];
        for (int index = 0; index < triangles.Length; index++)
        {
            BlendTriangle triangle = triangles[index];
            Vector2 origin = samples[triangle.A].Coordinate;
            Vector2 b = samples[triangle.B].Coordinate - origin;
            Vector2 c = samples[triangle.C].Coordinate - origin;
            float inverse = 1 / (b.X * c.Y - c.X * b.Y);
            _coordinates[index] = new(origin, new(c.Y * inverse, -c.X * inverse), new(-b.Y * inverse, b.X * inverse));
        }
    }

    /// <summary>Creates a space from at least two distinct coordinates and positive-duration clips.</summary>
    public static BlendSpace From1D(ReadOnlySpan<BlendPoint1D> points)
    {
        BlendPoint1D[] sorted = points.ToArray();
        Array.Sort(sorted, static (a, b) => a.Coordinate.CompareTo(b.Coordinate));
        var samples = new BlendSpaceSample[sorted.Length];
        var intervals = new BlendSpaceInterval[sorted.Length - 1];
        for (int point = 0; point < sorted.Length; point++)
        {
            BlendPoint1D value = sorted[point];
            samples[point] = new(new(value.Coordinate, 0), value.Clip, value.PhaseOffset);
            if (point > 0)
                intervals[point - 1] = new(point - 1, point, sorted[point - 1].Coordinate,
                    1 / (value.Coordinate - sorted[point - 1].Coordinate));
        }
        return new(samples, intervals, []);
    }

    /// <summary>Creates a space from positive-duration clips and an explicit triangulation of their coordinates.</summary>
    public static BlendSpace From2D(ReadOnlySpan<BlendPoint2D> points, ReadOnlySpan<BlendTriangle> triangles)
    {
        var samples = new BlendSpaceSample[points.Length];
        for (int point = 0; point < points.Length; point++)
            samples[point] = new(points[point].Coordinate, points[point].Clip, points[point].PhaseOffset);
        return new(samples, [], triangles.ToArray());
    }

    internal void GetWeights(Vector2 coordinate, Span<float> weights)
    {
        weights.Clear();
        if (_intervals.Length > 0)
        {
            int index = 0;
            while (index + 1 < _intervals.Length && coordinate.X > _samples[_intervals[index].B].Coordinate.X) index++;
            BlendSpaceInterval interval = _intervals[index];
            float t = Math.Clamp((coordinate.X - interval.From) * interval.InverseWidth, 0, 1);
            weights[interval.A] = 1 - t;
            weights[interval.B] = t;
            return;
        }
        for (int index = 0; index < _triangles.Length; index++)
        {
            TriangleCoordinates map = _coordinates[index];
            Vector2 relative = coordinate - map.Origin;
            float b = Vector2.Dot(relative, map.B), c = Vector2.Dot(relative, map.C), a = 1 - b - c;
            if (a < 0 || b < 0 || c < 0) continue;
            BlendTriangle triangle = _triangles[index];
            weights[triangle.A] = a;
            weights[triangle.B] = b;
            weights[triangle.C] = c;
            return;
        }
        var nearest = new EdgePoint { DistanceSquared = float.PositiveInfinity };
        foreach (BlendTriangle triangle in _triangles)
        {
            ProjectEdge(coordinate, triangle.A, triangle.B, ref nearest);
            ProjectEdge(coordinate, triangle.B, triangle.C, ref nearest);
            ProjectEdge(coordinate, triangle.C, triangle.A, ref nearest);
        }
        weights[nearest.A] = 1 - nearest.T;
        weights[nearest.B] = nearest.T;
    }

    private void ProjectEdge(Vector2 coordinate, int a, int b, ref EdgePoint nearest)
    {
        Vector2 origin = _samples[a].Coordinate;
        Vector2 edge = _samples[b].Coordinate - origin;
        float t = Math.Clamp(Vector2.Dot(coordinate - origin, edge) / edge.LengthSquared(), 0, 1);
        float distance = Vector2.DistanceSquared(coordinate, origin + edge * t);
        if (distance < nearest.DistanceSquared)
            nearest = new() { A = a, B = b, T = t, DistanceSquared = distance };
    }

    private readonly record struct TriangleCoordinates(Vector2 Origin, Vector2 B, Vector2 C);
    private struct EdgePoint { internal int A, B; internal float T, DistanceSquared; }
}
