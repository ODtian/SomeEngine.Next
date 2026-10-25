using System.Numerics;
using SomeEngine.ECS;

namespace SomeEngine.Physics;

/// <summary>A two-sided triangular core with a spherical surface radius.</summary>
public readonly record struct TriangleGeometry(Vector3 First, Vector3 Second, Vector3 Third, float Radius)
    : IComponent, ICollisionConvex<TriangleGeometry>
{
    public static float GetConvexRadius(in TriangleGeometry geometry) => geometry.Radius;
    public static CollisionBounds Bounds(in TriangleGeometry geometry, in BodyPose pose) =>
        CollisionConvex.Bounds(in geometry, in pose);

    public static CollisionFeaturePoint SupportCore(in TriangleGeometry geometry, in BodyPose pose, Vector3 direction)
    {
        Vector3 local = GeometrySpace.DirectionToLocal(in pose, direction);
        Vector3 point = geometry.First;
        int feature = 2;
        if (Vector3.Dot(geometry.Second, local) > Vector3.Dot(point, local))
        {
            point = geometry.Second;
            feature = 3;
        }
        if (Vector3.Dot(geometry.Third, local) > Vector3.Dot(point, local))
        {
            point = geometry.Third;
            feature = 4;
        }
        return new(GeometrySpace.ToWorld(in pose, point), feature);
    }

    public static int FaceCapacity(in TriangleGeometry geometry) => 3;

    public static int GetFace(in TriangleGeometry geometry, in BodyPose pose, Vector3 direction,
        Span<CollisionFeaturePoint> points)
    {
        Vector3 normal = Vector3.Normalize(Vector3.Cross(geometry.Second - geometry.First, geometry.Third - geometry.First));
        bool front = Vector3.Dot(GeometrySpace.DirectionToWorld(in pose, normal), direction) >= 0;
        Vector3 offset = normal * (front ? geometry.Radius : -geometry.Radius);
        int feature = front ? 0 : 1;
        points[0] = new(GeometrySpace.ToWorld(in pose, geometry.First + offset), feature);
        points[1] = new(GeometrySpace.ToWorld(in pose, (front ? geometry.Second : geometry.Third) + offset), feature);
        points[2] = new(GeometrySpace.ToWorld(in pose, (front ? geometry.Third : geometry.Second) + offset), feature);
        return 3;
    }

    public static GeometryPointDistance DistanceToPoint(in TriangleGeometry geometry, in BodyPose pose, Vector3 point)
    {
        Vector3 local = GeometrySpace.ToLocal(in pose, point);
        Vector3 faceNormal = Vector3.Normalize(Vector3.Cross(geometry.Second - geometry.First, geometry.Third - geometry.First));
        float height = Vector3.Dot(faceNormal, local - geometry.First);
        Vector3 projected = local - faceNormal * height;
        Vector3 closest, normal;
        float length;
        if (CollisionTriangles.Inside(projected, geometry.First, geometry.Second, geometry.Third, faceNormal))
        {
            closest = projected;
            normal = height >= 0 ? faceNormal : -faceNormal;
            length = MathF.Abs(height);
        }
        else
        {
            closest = CollisionTriangles.ClosestPoint(local, geometry.First, geometry.Second, geometry.Third);
            Vector3 offset = local - closest;
            length = offset.Length();
            normal = offset / length;
        }
        Vector3 weights = CollisionTriangles.GetWeights(closest, geometry.First, geometry.Second, geometry.Third);
        return new(GeometrySpace.ToWorld(in pose, closest + normal * geometry.Radius),
            GeometrySpace.DirectionToWorld(in pose, normal), length - geometry.Radius, GetFeature(weights, height >= 0));
    }

    public static bool CastSphere(in TriangleGeometry geometry, in BodyPose pose, in CollisionSphereSweep sweep,
        in CollisionQuerySettings settings, out GeometryCastHit hit)
    {
        Vector3 first = GeometrySpace.ToWorld(in pose, geometry.First);
        Vector3 second = GeometrySpace.ToWorld(in pose, geometry.Second);
        Vector3 third = GeometrySpace.ToWorld(in pose, geometry.Third);
        var expanded = sweep with { Radius = sweep.Radius + geometry.Radius };
        if (!CollisionTriangles.SweepSphere(in expanded, first, second, third, sweep.Length,
                out float distance, out Vector3 point, out Vector3 normal))
        {
            hit = default;
            return false;
        }
        Vector3 weights = CollisionTriangles.GetWeights(point, first, second, third);
        bool front = Vector3.Dot(Vector3.Cross(second - first, third - first), normal) >= 0;
        hit = new(distance, point + normal * geometry.Radius, normal, GetFeature(weights, front));
        return true;
    }

    private static int GetFeature(Vector3 weights, bool front) =>
        weights.X >= 1 ? 2 : weights.Y >= 1 ? 3 : weights.Z >= 1 ? 4
            : weights.X <= 0 ? 5 : weights.Y <= 0 ? 6 : weights.Z <= 0 ? 7 : front ? 0 : 1;
}

internal static partial class CollisionTriangles
{
    internal static Vector3 GetWeights(Vector3 point, Vector3 first, Vector3 second, Vector3 third)
    {
        Vector3 firstEdge = second - first, secondEdge = third - first, offset = point - first;
        float firstSquared = firstEdge.LengthSquared(), secondSquared = secondEdge.LengthSquared();
        float edgeDot = Vector3.Dot(firstEdge, secondEdge);
        float firstProjection = Vector3.Dot(firstEdge, offset), secondProjection = Vector3.Dot(secondEdge, offset);
        float denominator = firstSquared * secondSquared - edgeDot * edgeDot;
        float secondWeight = (secondSquared * firstProjection - edgeDot * secondProjection) / denominator;
        float thirdWeight = (firstSquared * secondProjection - edgeDot * firstProjection) / denominator;
        return new(1 - secondWeight - thirdWeight, secondWeight, thirdWeight);
    }
}
