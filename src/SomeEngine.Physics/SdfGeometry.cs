using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Systems;

namespace SomeEngine.Physics;

public readonly record struct SdfSample(float Distance, Vector3 Gradient, int Feature);

public interface ISdfField<TField> where TField : struct, ISdfField<TField>
{
    static abstract CollisionBounds Bounds(in TField field);
    static abstract SdfSample Sample(in TField field, Vector3 point);
    static virtual void CollectResources(in TField field, ref JobResourceAccessBuilder resources) { }
    static virtual long GetResourceVersion(in TField field) => 0;
}

/// <summary>A concrete local distance field, uniformly scaled before applying its body's rigid pose.</summary>
public readonly record struct SdfGeometry<TField>(TField Field, float Scale, float TraceStepScale)
    : IComponent, ICollisionGeometry<SdfGeometry<TField>> where TField : struct, ISdfField<TField>
{
    public static CollisionBounds Bounds(in SdfGeometry<TField> geometry, in BodyPose pose)
    {
        CollisionBounds local = TField.Bounds(geometry.Field);
        var box = new BoxGeometry((local.Max - local.Min) * (geometry.Scale * .5f));
        BodyPose center = pose;
        center.Position = GeometrySpace.ToWorld(in pose, (local.Min + local.Max) * (geometry.Scale * .5f));
        return BoxGeometry.Bounds(in box, in center);
    }

    public static GeometryPointDistance DistanceToPoint(in SdfGeometry<TField> geometry, in BodyPose pose, Vector3 point)
    {
        Vector3 local = GeometrySpace.ToLocal(in pose, point) / geometry.Scale;
        SdfSample sample = TField.Sample(geometry.Field, local);
        float squared = sample.Gradient.LengthSquared();
        Vector3 direction = squared > 0 ? sample.Gradient / MathF.Sqrt(squared) : Vector3.UnitX;
        Vector3 normal = GeometrySpace.DirectionToWorld(in pose, direction);
        float distance = sample.Distance * geometry.Scale;
        return new(point - normal * distance, normal, distance, sample.Feature);
    }

    public static int FaceCapacity(in SdfGeometry<TField> geometry) => 0;
    public static int GetFace(in SdfGeometry<TField> geometry, in BodyPose pose, Vector3 direction,
        Span<CollisionFeaturePoint> points) => 0;

    public static bool CastSphere(in SdfGeometry<TField> geometry, in BodyPose pose, in CollisionSphereSweep sweep,
        in CollisionQuerySettings settings, out GeometryCastHit hit)
    {
        float distance = 0, last = sweep.Length;
        if (!ClipTrace(in geometry, in pose, in sweep, ref distance, ref last))
        {
            hit = default;
            return false;
        }
        for (int iteration = 0; iteration < settings.Iterations; iteration++)
        {
            GeometryPointDistance surface = DistanceToPoint(in geometry, in pose,
                sweep.Center + sweep.Direction * distance);
            float gap = surface.Distance - sweep.Radius;
            if (gap <= settings.Tolerance)
            {
                hit = new(distance, surface.Point, surface.Normal, surface.Feature);
                return true;
            }
            distance += gap * geometry.TraceStepScale;
            if (distance > last) break;
        }
        hit = default;
        return false;
    }

    private static bool ClipTrace(in SdfGeometry<TField> geometry, in BodyPose pose,
        in CollisionSphereSweep sweep, ref float first, ref float last)
    {
        CollisionBounds bounds = TField.Bounds(geometry.Field);
        Vector3 origin = GeometrySpace.ToLocal(in pose, sweep.Center) / geometry.Scale;
        Vector3 direction = GeometrySpace.DirectionToLocal(in pose, sweep.Direction) / geometry.Scale;
        float radius = sweep.Radius / geometry.Scale;
        for (int axis = 0; axis < 3; axis++)
        {
            float minimum = bounds.Min[axis] - radius, maximum = bounds.Max[axis] + radius;
            if (direction[axis] == 0)
            {
                if (origin[axis] < minimum || origin[axis] > maximum) return false;
                continue;
            }
            float near = (minimum - origin[axis]) / direction[axis], far = (maximum - origin[axis]) / direction[axis];
            first = MathF.Max(first, MathF.Min(near, far));
            last = MathF.Min(last, MathF.Max(near, far));
            if (last < first) return false;
        }
        return true;
    }
}

public readonly struct SdfGeometryResources<TField> : IJobComponentResources<SdfGeometry<TField>>
    where TField : struct, ISdfField<TField>
{
    public static void Collect(in SdfGeometry<TField> component, ref JobResourceAccessBuilder resources) =>
        TField.CollectResources(component.Field, ref resources);
    public static long GetVersion(in SdfGeometry<TField> component) => TField.GetResourceVersion(component.Field);
}
