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
        float nearest = float.PositiveInfinity;
        Vector3 closest = default;
        weights = default;
        Span<int> indices = stackalloc int[4];
        for (int mask = 1; mask < 1 << points.Length; mask++)
        {
            int count = 0;
            for (int index = 0; index < points.Length; index++)
                if ((mask & (1 << index)) != 0) indices[count++] = index;
            Vector3 a = points[indices[0]].Difference - target;
            Vector4 candidate = default;
            candidate[indices[0]] = 1;
            if (count > 1)
            {
                Vector3 first = points[indices[1]].Difference - target - a;
                if (count == 2)
                {
                    float denominator = first.LengthSquared();
                    if (denominator == 0) continue;
                    float parameter = -Vector3.Dot(a, first) / denominator;
                    candidate[indices[0]] = 1 - parameter;
                    candidate[indices[1]] = parameter;
                }
                else
                {
                    Vector3 second = points[indices[2]].Difference - target - a;
                    if (count == 3)
                    {
                        float firstSquared = first.LengthSquared(), secondSquared = second.LengthSquared();
                        float edgeDot = Vector3.Dot(first, second);
                        float denominator = firstSquared * secondSquared - edgeDot * edgeDot;
                        if (denominator <= 0) continue;
                        float firstProjection = Vector3.Dot(a, first), secondProjection = Vector3.Dot(a, second);
                        float u = (edgeDot * secondProjection - secondSquared * firstProjection) / denominator;
                        float v = (edgeDot * firstProjection - firstSquared * secondProjection) / denominator;
                        candidate[indices[0]] = 1 - u - v;
                        candidate[indices[1]] = u;
                        candidate[indices[2]] = v;
                    }
                    else
                    {
                        Vector3 third = points[indices[3]].Difference - target - a;
                        float determinant = Vector3.Dot(first, Vector3.Cross(second, third));
                        if (determinant == 0) continue;
                        Vector3 right = -a;
                        float u = Vector3.Dot(right, Vector3.Cross(second, third)) / determinant;
                        float v = Vector3.Dot(first, Vector3.Cross(right, third)) / determinant;
                        float w = Vector3.Dot(first, Vector3.Cross(second, right)) / determinant;
                        candidate[indices[0]] = 1 - u - v - w;
                        candidate[indices[1]] = u;
                        candidate[indices[2]] = v;
                        candidate[indices[3]] = w;
                    }
                }
            }
            bool inside = true;
            Vector3 point = default;
            for (int index = 0; index < points.Length; index++)
            {
                if (candidate[index] < 0) inside = false;
                point += (points[index].Difference - target) * candidate[index];
            }
            if (!inside || point.LengthSquared() >= nearest) continue;
            closest = point;
            nearest = point.LengthSquared();
            weights = candidate;
        }
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
