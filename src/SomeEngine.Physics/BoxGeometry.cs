using System.Numerics;
using SomeEngine.ECS;

namespace SomeEngine.Physics;

public readonly record struct BoxGeometry(Vector3 HalfExtents) : IComponent, ICollisionConvex<BoxGeometry>
{
    public static float GetConvexRadius(in BoxGeometry geometry) => 0;
    public static CollisionBounds Bounds(in BoxGeometry geometry, in BodyPose pose) =>
        CollisionConvex.Bounds(in geometry, in pose);

    public static CollisionFeaturePoint SupportCore(in BoxGeometry geometry, in BodyPose pose, Vector3 direction)
    {
        Vector3 local = GeometrySpace.DirectionToLocal(in pose, direction);
        Vector3 point = new(local.X >= 0 ? geometry.HalfExtents.X : -geometry.HalfExtents.X,
            local.Y >= 0 ? geometry.HalfExtents.Y : -geometry.HalfExtents.Y,
            local.Z >= 0 ? geometry.HalfExtents.Z : -geometry.HalfExtents.Z);
        return new(GeometrySpace.ToWorld(in pose, point), VertexFeature(point));
    }

    public static GeometryPointDistance DistanceToPoint(in BoxGeometry geometry, in BodyPose pose, Vector3 point)
    {
        Vector3 local = GeometrySpace.ToLocal(in pose, point);
        Vector3 outside = Vector3.Abs(local) - geometry.HalfExtents;
        Vector3 closest = Vector3.Clamp(local, -geometry.HalfExtents, geometry.HalfExtents);
        Vector3 difference = local - closest;
        float length = difference.Length();
        Vector3 normal;
        int axis;
        if (length > 0)
        {
            normal = difference / length;
            Vector3 absolute = Vector3.Abs(normal);
            axis = absolute.X >= absolute.Y && absolute.X >= absolute.Z ? 0 : absolute.Y >= absolute.Z ? 1 : 2;
        }
        else
        {
            axis = outside.X >= outside.Y && outside.X >= outside.Z ? 0 : outside.Y >= outside.Z ? 1 : 2;
            normal = Vector3.Zero;
            normal[axis] = local[axis] >= 0 ? 1 : -1;
            closest[axis] = normal[axis] * geometry.HalfExtents[axis];
            length = outside[axis];
        }
        return new(GeometrySpace.ToWorld(in pose, closest), GeometrySpace.DirectionToWorld(in pose, normal),
            length, axis * 2 + (local[axis] >= 0 ? 1 : 0));
    }

    public static int FaceCapacity(in BoxGeometry geometry) => 4;
    public static int GetFace(in BoxGeometry geometry, in BodyPose pose, Vector3 direction,
        Span<CollisionFeaturePoint> points)
    {
        Vector3 local = GeometrySpace.DirectionToLocal(in pose, direction);
        Vector3 absolute = Vector3.Abs(local);
        int axis = absolute.X >= absolute.Y && absolute.X >= absolute.Z ? 0 : absolute.Y >= absolute.Z ? 1 : 2;
        float sign = local[axis] >= 0 ? 1 : -1;
        Vector3 center = Vector3.Zero, first = Vector3.Zero, second = Vector3.Zero;
        center[axis] = sign * geometry.HalfExtents[axis];
        first[(axis + 1) % 3] = sign * geometry.HalfExtents[(axis + 1) % 3];
        second[(axis + 2) % 3] = geometry.HalfExtents[(axis + 2) % 3];
        int feature = axis * 2 + (sign > 0 ? 1 : 0);
        WriteFacePoint(in pose, center - first - second, feature, points, 0);
        WriteFacePoint(in pose, center + first - second, feature, points, 1);
        WriteFacePoint(in pose, center + first + second, feature, points, 2);
        WriteFacePoint(in pose, center - first + second, feature, points, 3);
        return 4;
    }

    private static int VertexFeature(Vector3 point) =>
        8 + (point.X >= 0 ? 1 : 0) + (point.Y >= 0 ? 2 : 0) + (point.Z >= 0 ? 4 : 0);

    private static void WriteFacePoint(in BodyPose pose, Vector3 point, int feature,
        Span<CollisionFeaturePoint> points, int index) =>
        points[index] = new(GeometrySpace.ToWorld(in pose, point), feature);
}
