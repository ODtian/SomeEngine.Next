using System.Numerics;

namespace SomeEngine.Physics;

public readonly record struct CollisionFeaturePoint(Vector3 Point, int Feature);
public readonly record struct GeometryPointDistance(Vector3 Point, Vector3 Normal, float Distance, int Feature);

/// <summary>Witnesses on two geometries, with normal from the first toward the second and a signed gap.</summary>
public readonly record struct CollisionDistance(Vector3 FirstPoint, Vector3 SecondPoint, Vector3 Normal,
    float Separation, int FirstFeature, int SecondFeature)
{
    public CollisionDistance Reverse() =>
        new(SecondPoint, FirstPoint, -Normal, Separation, SecondFeature, FirstFeature);
}

public interface ICollisionGeometry<TGeometry> where TGeometry : struct, ICollisionGeometry<TGeometry>
{
    static abstract CollisionBounds Bounds(in TGeometry geometry, in BodyPose pose);
    static abstract GeometryPointDistance DistanceToPoint(in TGeometry geometry, in BodyPose pose, Vector3 point);
    static abstract int FaceCapacity(in TGeometry geometry);
    static abstract int GetFace(in TGeometry geometry, in BodyPose pose, Vector3 direction,
        Span<CollisionFeaturePoint> points);
    static virtual bool CastSphere(in TGeometry geometry, in BodyPose pose, in CollisionSphereSweep sweep,
        in CollisionQuerySettings settings, out GeometryCastHit hit) =>
        GeometryCasts.Convex(in geometry, in pose, in sweep, in settings, out hit);
}

/// <summary>A concrete support-mapped core with an optional spherical radius around it.</summary>
public interface ICollisionConvex<TGeometry> : ICollisionGeometry<TGeometry>
    where TGeometry : struct, ICollisionConvex<TGeometry>
{
    static abstract float GetConvexRadius(in TGeometry geometry);
    static abstract CollisionFeaturePoint SupportCore(in TGeometry geometry, in BodyPose pose, Vector3 direction);
}

public interface ICollisionNarrowPhase<TFirst, TSecond> where TFirst : struct where TSecond : struct
{
    int ContactCapacity { get; }
    CollisionDistance Distance(in TFirst first, in BodyPose firstPose, in TSecond second, in BodyPose secondPose);
    int GetManifold(in TFirst first, in BodyPose firstPose, in TSecond second, in BodyPose secondPose,
        float contactDistance, Span<CollisionDistance> points);
}

internal static class GeometrySpace
{
    internal static Vector3 ToWorld(in BodyPose pose, Vector3 point) =>
        pose.Position + Vector3.Transform(point, pose.Rotation);
    internal static Vector3 ToLocal(in BodyPose pose, Vector3 point) =>
        Vector3.Transform(point - pose.Position, Quaternion.Conjugate(pose.Rotation));
    internal static Vector3 DirectionToLocal(in BodyPose pose, Vector3 direction) =>
        Vector3.Transform(direction, Quaternion.Conjugate(pose.Rotation));
    internal static Vector3 DirectionToWorld(in BodyPose pose, Vector3 direction) =>
        Vector3.Transform(direction, pose.Rotation);
}

public static class CollisionConvex
{
    public static CollisionFeaturePoint Support<TGeometry>(in TGeometry geometry, in BodyPose pose, Vector3 direction)
        where TGeometry : struct, ICollisionConvex<TGeometry>
    {
        CollisionFeaturePoint core = TGeometry.SupportCore(in geometry, in pose, direction);
        float radius = TGeometry.GetConvexRadius(in geometry);
        return new(core.Point + direction * (radius / direction.Length()), core.Feature);
    }

    public static CollisionBounds Bounds<TGeometry>(in TGeometry geometry, in BodyPose pose)
        where TGeometry : struct, ICollisionConvex<TGeometry> => new(
        new(Support(in geometry, in pose, -Vector3.UnitX).Point.X,
            Support(in geometry, in pose, -Vector3.UnitY).Point.Y,
            Support(in geometry, in pose, -Vector3.UnitZ).Point.Z),
        new(Support(in geometry, in pose, Vector3.UnitX).Point.X,
            Support(in geometry, in pose, Vector3.UnitY).Point.Y,
            Support(in geometry, in pose, Vector3.UnitZ).Point.Z));
}
