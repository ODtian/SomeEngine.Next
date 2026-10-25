using System.Numerics;
using SomeEngine.ECS;

namespace SomeEngine.Physics;

/// <summary>A filled Y-axis cylinder with circular caps.</summary>
public readonly record struct CylinderGeometry(float Radius, float HalfHeight) : IComponent, ICollisionConvex<CylinderGeometry>
{
    public static float GetConvexRadius(in CylinderGeometry geometry) => 0;
    public static CollisionBounds Bounds(in CylinderGeometry geometry, in BodyPose pose) =>
        CollisionConvex.Bounds(in geometry, in pose);

    public static CollisionFeaturePoint SupportCore(in CylinderGeometry geometry, in BodyPose pose, Vector3 direction)
    {
        Vector3 local = GeometrySpace.DirectionToLocal(in pose, direction);
        float radialLength = MathF.Sqrt(local.X * local.X + local.Z * local.Z);
        Vector3 point = radialLength > 0 ? new(local.X * geometry.Radius / radialLength, 0,
            local.Z * geometry.Radius / radialLength) : Vector3.Zero;
        point.Y = local.Y >= 0 ? geometry.HalfHeight : -geometry.HalfHeight;
        return new(GeometrySpace.ToWorld(in pose, point), local.Y >= 0 ? 1 : 2);
    }

    public static GeometryPointDistance DistanceToPoint(in CylinderGeometry geometry, in BodyPose pose, Vector3 point)
    {
        Vector3 local = GeometrySpace.ToLocal(in pose, point);
        float radius = MathF.Sqrt(local.X * local.X + local.Z * local.Z);
        Vector3 radial = radius > 0 ? new(local.X / radius, 0, local.Z / radius) : Vector3.UnitX;
        float radialDistance = radius - geometry.Radius;
        float capDistance = MathF.Abs(local.Y) - geometry.HalfHeight;
        float heightSign = local.Y >= 0 ? 1 : -1;
        Vector3 closest, normal;
        float distance;
        int feature;
        if (radialDistance > 0 && capDistance > 0)
        {
            closest = radial * geometry.Radius + Vector3.UnitY * (heightSign * geometry.HalfHeight);
            Vector3 difference = local - closest;
            distance = difference.Length();
            normal = difference / distance;
            feature = heightSign > 0 ? 3 : 4;
        }
        else if (radialDistance >= capDistance)
        {
            closest = radial * geometry.Radius + Vector3.UnitY * local.Y;
            normal = radial;
            distance = radialDistance;
            feature = 0;
        }
        else
        {
            closest = new(local.X, heightSign * geometry.HalfHeight, local.Z);
            normal = Vector3.UnitY * heightSign;
            distance = capDistance;
            feature = heightSign > 0 ? 1 : 2;
        }
        return new(GeometrySpace.ToWorld(in pose, closest), GeometrySpace.DirectionToWorld(in pose, normal),
            distance, feature);
    }

    public static int FaceCapacity(in CylinderGeometry geometry) => 4;
    public static int GetFace(in CylinderGeometry geometry, in BodyPose pose, Vector3 direction,
        Span<CollisionFeaturePoint> points)
    {
        Vector3 local = Vector3.Normalize(GeometrySpace.DirectionToLocal(in pose, direction));
        if (MathF.Abs(local.Y) >= 1 - 1e-5f)
        {
            float sign = local.Y > 0 ? 1 : -1;
            int feature = sign > 0 ? 1 : 2;
            float height = sign * geometry.HalfHeight;
            points[0] = new(GeometrySpace.ToWorld(in pose, new(geometry.Radius, height, 0)), feature);
            points[1] = new(GeometrySpace.ToWorld(in pose, new(0, height, -sign * geometry.Radius)), feature);
            points[2] = new(GeometrySpace.ToWorld(in pose, new(-geometry.Radius, height, 0)), feature);
            points[3] = new(GeometrySpace.ToWorld(in pose, new(0, height, sign * geometry.Radius)), feature);
            return 4;
        }
        if (MathF.Abs(local.Y) <= 1e-5f)
        {
            Vector3 radial = Vector3.Normalize(new Vector3(local.X, 0, local.Z)) * geometry.Radius;
            points[0] = new(GeometrySpace.ToWorld(in pose, radial - Vector3.UnitY * geometry.HalfHeight), 0);
            points[1] = new(GeometrySpace.ToWorld(in pose, radial + Vector3.UnitY * geometry.HalfHeight), 0);
            return 2;
        }
        points[0] = SupportCore(in geometry, in pose, direction);
        return 1;
    }
}
