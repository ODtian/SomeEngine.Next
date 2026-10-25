using System.Numerics;
using SomeEngine.ECS;

namespace SomeEngine.Physics;

public readonly record struct SphereGeometry(float Radius) : IComponent, ICollisionConvex<SphereGeometry>
{
    public static float GetConvexRadius(in SphereGeometry geometry) => geometry.Radius;
    public static CollisionFeaturePoint SupportCore(in SphereGeometry geometry, in BodyPose pose, Vector3 direction) =>
        new(pose.Position, 0);
    public static CollisionBounds Bounds(in SphereGeometry geometry, in BodyPose pose) =>
        new(pose.Position - new Vector3(geometry.Radius), pose.Position + new Vector3(geometry.Radius));
    public static GeometryPointDistance DistanceToPoint(in SphereGeometry geometry, in BodyPose pose, Vector3 point)
    {
        Vector3 offset = point - pose.Position;
        float length = offset.Length();
        Vector3 normal = length > 0 ? offset / length : Vector3.UnitX;
        return new(pose.Position + normal * geometry.Radius, normal, length - geometry.Radius, 0);
    }
    public static int FaceCapacity(in SphereGeometry geometry) => 1;
    public static int GetFace(in SphereGeometry geometry, in BodyPose pose, Vector3 direction,
        Span<CollisionFeaturePoint> points)
    {
        points[0] = CollisionConvex.Support(in geometry, in pose, direction);
        return 1;
    }
}

public readonly struct SphereCollision<TGeometry> : ICollisionNarrowPhase<SphereGeometry, TGeometry>
    where TGeometry : struct, ICollisionGeometry<TGeometry>
{
    public int ContactCapacity => 1;
    public CollisionDistance Distance(in SphereGeometry first, in BodyPose firstPose,
        in TGeometry second, in BodyPose secondPose)
    {
        GeometryPointDistance point = TGeometry.DistanceToPoint(in second, in secondPose, firstPose.Position);
        return new(firstPose.Position - point.Normal * first.Radius, point.Point, -point.Normal,
            point.Distance - first.Radius, 0, point.Feature);
    }

    public int GetManifold(in SphereGeometry first, in BodyPose firstPose, in TGeometry second, in BodyPose secondPose,
        float contactDistance, Span<CollisionDistance> points)
    {
        CollisionDistance distance = Distance(in first, in firstPose, in second, in secondPose);
        if (distance.Separation > contactDistance) return 0;
        points[0] = distance;
        return 1;
    }
}
