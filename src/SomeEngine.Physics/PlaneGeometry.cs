using System.Numerics;
using SomeEngine.ECS;

namespace SomeEngine.Physics;

/// <summary>The solid half-space Dot(Normal, localPoint) &lt;= Offset, with a unit local normal.</summary>
public readonly record struct PlaneGeometry(Vector3 Normal, float Offset) : IComponent, ICollisionGeometry<PlaneGeometry>
{
    public static CollisionBounds Bounds(in PlaneGeometry geometry, in BodyPose pose) =>
        new(new Vector3(float.NegativeInfinity), new Vector3(float.PositiveInfinity));

    public static GeometryPointDistance DistanceToPoint(in PlaneGeometry geometry, in BodyPose pose, Vector3 point)
    {
        Vector3 normal = GeometrySpace.DirectionToWorld(in pose, geometry.Normal);
        float distance = Vector3.Dot(normal, point - pose.Position) - geometry.Offset;
        return new(point - normal * distance, normal, distance, 0);
    }

    public static int FaceCapacity(in PlaneGeometry geometry) => 0;
    public static int GetFace(in PlaneGeometry geometry, in BodyPose pose, Vector3 direction,
        Span<CollisionFeaturePoint> points) => 0;
}

public readonly struct ConvexPlaneCollision<TGeometry>(float tolerance) : ICollisionNarrowPhase<TGeometry, PlaneGeometry>
    where TGeometry : struct, ICollisionConvex<TGeometry>
{
    public int ContactCapacity => 4;

    public CollisionDistance Distance(in TGeometry first, in BodyPose firstPose,
        in PlaneGeometry second, in BodyPose secondPose)
    {
        Vector3 normal = GeometrySpace.DirectionToWorld(in secondPose, second.Normal);
        CollisionFeaturePoint support = CollisionConvex.Support(in first, in firstPose, -normal);
        GeometryPointDistance plane = PlaneGeometry.DistanceToPoint(in second, in secondPose, support.Point);
        return new(support.Point, plane.Point, -normal, plane.Distance, support.Feature, 0);
    }

    public int GetManifold(in TGeometry first, in BodyPose firstPose, in PlaneGeometry second, in BodyPose secondPose,
        float contactDistance, Span<CollisionDistance> points)
    {
        CollisionDistance distance = Distance(in first, in firstPose, in second, in secondPose);
        if (distance.Separation > contactDistance) return 0;
        Span<CollisionFeaturePoint> face = stackalloc CollisionFeaturePoint[TGeometry.FaceCapacity(in first)];
        int faceCount = TGeometry.GetFace(in first, in firstPose, distance.Normal, face);
        Span<CollisionDistance> candidates = stackalloc CollisionDistance[faceCount];
        int count = 0;
        for (int index = 0; index < faceCount; index++)
        {
            GeometryPointDistance plane = PlaneGeometry.DistanceToPoint(in second, in secondPose, face[index].Point);
            if (plane.Distance > contactDistance + tolerance) continue;
            candidates[count++] = new(face[index].Point, plane.Point, distance.Normal, plane.Distance, face[index].Feature, 0);
        }
        return CollisionManifolds.Reduce(candidates[..count], points);
    }
}
