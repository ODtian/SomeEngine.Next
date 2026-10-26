using System.Numerics;

namespace SomeEngine.Physics;

internal readonly record struct ConvexSupportPoint(Vector3 First, Vector3 Second, int FirstFeature, int SecondFeature)
{
    internal Vector3 Difference => First - Second;
}

internal static class CollisionSimplex
{
    internal static Vector3 Reduce(Span<ConvexSupportPoint> points, ref int count, out Vector4 weights)
    {
        Vector3 closest = Closest(points[..count], Vector3.Zero, out Vector4 selected);
        int written = 0;
        weights = Vector4.Zero;
        for (int index = 0; index < count; index++)
        {
            if (selected[index] <= 0) continue;
            points[written] = points[index];
            weights[written++] = selected[index];
        }
        count = written;
        return closest;
    }

    internal static Vector3 Closest(ReadOnlySpan<ConvexSupportPoint> points, Vector3 target, out Vector4 weights)
    {
        Vector3 a = points[0].Difference - target;
        if (points.Length == 1) { weights = new(1, 0, 0, 0); return a; }
        Vector3 b = points[1].Difference - target;
        if (points.Length == 2)
        {
            Vector3 point = ClosestSegment(a, b, out Vector2 edgeWeights);
            weights = new(edgeWeights.X, edgeWeights.Y, 0, 0);
            return point;
        }
        Vector3 c = points[2].Difference - target;
        if (points.Length == 3)
        {
            Vector3 point = ClosestTriangle(a, b, c, out Vector3 faceWeights);
            weights = new(faceWeights, 0);
            return point;
        }
        Vector3 d = points[3].Difference - target;
        Vector3 first = b - a, second = c - a, third = d - a;
        float determinant = Vector3.Dot(first, Vector3.Cross(second, third));
        if (determinant != 0)
        {
            float u = Vector3.Dot(-a, Vector3.Cross(second, third)) / determinant;
            float v = Vector3.Dot(first, Vector3.Cross(-a, third)) / determinant;
            float w = Vector3.Dot(first, Vector3.Cross(second, -a)) / determinant;
            Vector4 candidate = new(1 - u - v - w, u, v, w);
            if (candidate.X >= 0 && candidate.Y >= 0 && candidate.Z >= 0 && candidate.W >= 0)
            { weights = candidate; return Vector3.Zero; }
        }
        Vector3 closest = ClosestTriangle(a, b, c, out Vector3 selected);
        weights = new(selected, 0);
        Vector3 other = ClosestTriangle(a, b, d, out selected);
        if (other.LengthSquared() < closest.LengthSquared())
        { closest = other; weights = new(selected.X, selected.Y, 0, selected.Z); }
        other = ClosestTriangle(a, c, d, out selected);
        if (other.LengthSquared() < closest.LengthSquared())
        { closest = other; weights = new(selected.X, 0, selected.Y, selected.Z); }
        other = ClosestTriangle(b, c, d, out selected);
        if (other.LengthSquared() < closest.LengthSquared())
        { closest = other; weights = new(0, selected.X, selected.Y, selected.Z); }
        return closest;
    }

    private static Vector3 ClosestSegment(Vector3 a, Vector3 b, out Vector2 weights)
    {
        Vector3 edge = b - a;
        float squared = edge.LengthSquared();
        float parameter = squared > 0 ? Math.Clamp(-Vector3.Dot(a, edge) / squared, 0, 1) : 0;
        weights = new(1 - parameter, parameter);
        return a + edge * parameter;
    }

    private static Vector3 ClosestTriangle(Vector3 a, Vector3 b, Vector3 c, out Vector3 weights)
    {
        Vector3 ab = b - a, ac = c - a, normal = Vector3.Cross(ab, ac);
        float squared = normal.LengthSquared();
        if (squared > 0)
        {
            float second = Vector3.Dot(Vector3.Cross(-a, ac), normal) / squared;
            float third = Vector3.Dot(Vector3.Cross(ab, -a), normal) / squared;
            weights = new(1 - second - third, second, third);
            if (weights.X >= 0 && weights.Y >= 0 && weights.Z >= 0)
                return normal * (Vector3.Dot(a, normal) / squared);
        }
        Vector3 closest = ClosestSegment(a, b, out Vector2 edgeWeights);
        weights = new(edgeWeights.X, edgeWeights.Y, 0);
        Vector3 other = ClosestSegment(a, c, out edgeWeights);
        if (other.LengthSquared() < closest.LengthSquared())
        { closest = other; weights = new(edgeWeights.X, 0, edgeWeights.Y); }
        other = ClosestSegment(b, c, out edgeWeights);
        if (other.LengthSquared() < closest.LengthSquared())
        { closest = other; weights = new(0, edgeWeights.X, edgeWeights.Y); }
        return closest;
    }

    internal static CollisionDistance Witness(ReadOnlySpan<ConvexSupportPoint> points, Vector4 weights,
        Vector3 normal, float separation, float firstRadius, float secondRadius)
    {
        Vector3 first = default, second = default;
        int feature = 0;
        for (int index = 0; index < points.Length; index++)
        {
            first += points[index].First * weights[index];
            second += points[index].Second * weights[index];
            if (weights[index] > weights[feature]) feature = index;
        }
        return new(first + normal * firstRadius, second - normal * secondRadius, normal,
            separation - firstRadius - secondRadius, points[feature].FirstFeature, points[feature].SecondFeature);
    }
}
