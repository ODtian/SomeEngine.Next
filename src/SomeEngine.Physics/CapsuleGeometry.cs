using System.Numerics;
using SomeEngine.ECS;

namespace SomeEngine.Physics;

/// <summary>A Y-axis segment of twice HalfLength, rounded by Radius.</summary>
public readonly record struct CapsuleGeometry(float Radius, float HalfLength) : IComponent, ICollisionConvex<CapsuleGeometry>
{
    public static float GetConvexRadius(in CapsuleGeometry geometry) => geometry.Radius;
    public static CollisionBounds Bounds(in CapsuleGeometry geometry, in BodyPose pose) =>
        CollisionConvex.Bounds(in geometry, in pose);
    public static CollisionFeaturePoint SupportCore(in CapsuleGeometry geometry, in BodyPose pose, Vector3 direction)
    {
        Vector3 axis = GeometrySpace.DirectionToWorld(in pose, Vector3.UnitY);
        bool upper = Vector3.Dot(axis, direction) >= 0;
        return new(pose.Position + axis * (upper ? geometry.HalfLength : -geometry.HalfLength), upper ? 1 : 0);
    }
    public static GeometryPointDistance DistanceToPoint(in CapsuleGeometry geometry, in BodyPose pose, Vector3 point)
    {
        Vector3 local = GeometrySpace.ToLocal(in pose, point);
        float height = Math.Clamp(local.Y, -geometry.HalfLength, geometry.HalfLength);
        Vector3 center = new(0, height, 0);
        Vector3 offset = local - center;
        float length = offset.Length();
        Vector3 normal = length > 0 ? offset / length : Vector3.UnitX;
        int feature = height <= -geometry.HalfLength ? 0 : height >= geometry.HalfLength ? 1 : 2;
        return new(GeometrySpace.ToWorld(in pose, center + normal * geometry.Radius),
            GeometrySpace.DirectionToWorld(in pose, normal), length - geometry.Radius, feature);
    }
    public static int FaceCapacity(in CapsuleGeometry geometry) => 2;
    public static int GetFace(in CapsuleGeometry geometry, in BodyPose pose, Vector3 direction,
        Span<CollisionFeaturePoint> points)
    {
        Vector3 axis = GeometrySpace.DirectionToWorld(in pose, Vector3.UnitY);
        Vector3 normal = Vector3.Normalize(direction);
        if (MathF.Abs(Vector3.Dot(axis, normal)) <= 1e-5f)
        {
            points[0] = new(pose.Position - axis * geometry.HalfLength + normal * geometry.Radius, 2);
            points[1] = new(pose.Position + axis * geometry.HalfLength + normal * geometry.Radius, 2);
            return 2;
        }
        points[0] = CollisionConvex.Support(in geometry, in pose, direction);
        return 1;
    }
}

public readonly struct CapsuleCollision(float tolerance) : ICollisionNarrowPhase<CapsuleGeometry, CapsuleGeometry>
{
    public int ContactCapacity => 2;

    public CollisionDistance Distance(in CapsuleGeometry first, in BodyPose firstPose,
        in CapsuleGeometry second, in BodyPose secondPose)
    {
        Vector3 firstAxis = GeometrySpace.DirectionToWorld(in firstPose, Vector3.UnitY);
        Vector3 secondAxis = GeometrySpace.DirectionToWorld(in secondPose, Vector3.UnitY);
        GeometrySegments.Closest(firstPose.Position - firstAxis * first.HalfLength,
            firstPose.Position + firstAxis * first.HalfLength,
            secondPose.Position - secondAxis * second.HalfLength,
            secondPose.Position + secondAxis * second.HalfLength, out Vector3 firstPoint, out Vector3 secondPoint);
        Vector3 difference = secondPoint - firstPoint;
        float length = difference.Length();
        Vector3 normal;
        if (length > 0) normal = difference / length;
        else
        {
            normal = Vector3.Cross(firstAxis, secondAxis);
            if (normal.LengthSquared() <= tolerance * tolerance)
                normal = Vector3.Cross(firstAxis, MathF.Abs(firstAxis.X) < .9f ? Vector3.UnitX : Vector3.UnitY);
            normal = Vector3.Normalize(normal);
        }
        return new(firstPoint + normal * first.Radius, secondPoint - normal * second.Radius, normal,
            length - first.Radius - second.Radius, 2, 2);
    }

    public int GetManifold(in CapsuleGeometry first, in BodyPose firstPose,
        in CapsuleGeometry second, in BodyPose secondPose, float contactDistance, Span<CollisionDistance> points)
    {
        CollisionDistance distance = Distance(in first, in firstPose, in second, in secondPose);
        if (distance.Separation > contactDistance) return 0;
        Span<CollisionFeaturePoint> firstFace = stackalloc CollisionFeaturePoint[2];
        Span<CollisionFeaturePoint> secondFace = stackalloc CollisionFeaturePoint[2];
        int firstCount = CapsuleGeometry.GetFace(in first, in firstPose, distance.Normal, firstFace);
        int secondCount = CapsuleGeometry.GetFace(in second, in secondPose, -distance.Normal, secondFace);
        int count = firstCount == 2 && secondCount == 2 ?
            CollisionManifolds.Lines(firstFace, secondFace, distance.Normal, tolerance, points) : 0;
        if (count > 0) return count;
        points[0] = distance;
        return 1;
    }
}
