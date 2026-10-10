using System.Numerics;

namespace SomeEngine.Assets.Importers;

internal static class ClusterSphere
{
    // UE Sphere.cpp, Ritter point and sphere constructors.
    internal static Vector4 FromPoints(ReadOnlySpan<Vector3> points)
    {
        int[] minimum = [0, 0, 0], maximum = [0, 0, 0];
        for (int point = 0; point < points.Length; point++)
            for (int axis = 0; axis < 3; axis++)
            {
                if (points[point][axis] < points[minimum[axis]][axis]) minimum[axis] = point;
                if (points[point][axis] > points[maximum[axis]][axis]) maximum[axis] = point;
            }
        float largest = 0;
        int largestAxis = 0;
        for (int axis = 0; axis < 3; axis++)
        {
            float distance = Vector3.DistanceSquared(points[minimum[axis]], points[maximum[axis]]);
            if (distance > largest) { largest = distance; largestAxis = axis; }
        }
        Vector3 center = (points[minimum[largestAxis]] + points[maximum[largestAxis]]) * .5f;
        float radius = MathF.Sqrt(largest) * .5f;
        foreach (Vector3 point in points)
        {
            float squared = Vector3.DistanceSquared(point, center);
            if (squared <= radius * radius) continue;
            float distance = MathF.Sqrt(squared);
            float t = .5f + .5f * radius / distance;
            center = point * (1 - t) + center * t;
            radius = (radius + distance) * .5f;
        }
        return new(center, radius);
    }

    internal static Vector4 FromSpheres(ReadOnlySpan<Vector4> spheres)
    {
        int[] minimum = [0, 0, 0], maximum = [0, 0, 0];
        for (int sphere = 0; sphere < spheres.Length; sphere++)
            for (int axis = 0; axis < 3; axis++)
            {
                if (spheres[sphere][axis] - spheres[sphere].W < spheres[minimum[axis]][axis] - spheres[minimum[axis]].W) minimum[axis] = sphere;
                if (spheres[sphere][axis] + spheres[sphere].W > spheres[maximum[axis]][axis] + spheres[maximum[axis]].W) maximum[axis] = sphere;
            }
        float largest = 0;
        int largestAxis = 0;
        for (int axis = 0; axis < 3; axis++)
        {
            Vector4 first = spheres[minimum[axis]], second = spheres[maximum[axis]];
            float distance = Vector3.Distance(Center(first), Center(second)) + first.W + second.W;
            if (distance > largest) { largest = distance; largestAxis = axis; }
        }
        Vector4 result = Merge(spheres[minimum[largestAxis]], spheres[maximum[largestAxis]]);
        foreach (Vector4 sphere in spheres) result = Merge(result, sphere);
        return result;
    }

    private static Vector4 Merge(Vector4 first, Vector4 second)
    {
        if (first.W == 0) return second;
        Vector3 offset = Center(second) - Center(first);
        float squared = offset.LengthSquared();
        float difference = first.W - second.W;
        if (difference * difference + 1e-4f >= squared) return first.W < second.W ? second : first;
        float distance = MathF.Sqrt(squared), radius = (distance + first.W + second.W) * .5f;
        Vector3 center = Center(first);
        if (distance > 1e-8f) center += offset * ((radius - first.W) / distance);
        return new(center, radius);
    }
    private static Vector3 Center(Vector4 sphere) => new(sphere.X, sphere.Y, sphere.Z);
}
