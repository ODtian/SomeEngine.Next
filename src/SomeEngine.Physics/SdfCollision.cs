using System.Numerics;

namespace SomeEngine.Physics;

public readonly record struct SdfCollisionSettings(SdfSurfaceSampling Sampling, int SupportIterations, float Tolerance);

/// <summary>Samples a convex support surface and the concrete field's zero surface in both directions.</summary>
public readonly struct SdfCollision<TGeometry, TField>(SdfCollisionSettings settings)
    : ICollisionNarrowPhase<TGeometry, SdfGeometry<TField>>
    where TGeometry : struct, ICollisionConvex<TGeometry>
    where TField : struct, ISdfField<TField>
{
    public int ContactCapacity => 4;

    public CollisionDistance Distance(in TGeometry first, in BodyPose firstPose,
        in SdfGeometry<TField> second, in BodyPose secondPose)
    {
        var contacts = new SdfContactSet(Span<CollisionDistance>.Empty, 0, settings.Tolerance);
        Read(in first, in firstPose, in second, in secondPose, ref contacts);
        return contacts.Best;
    }

    public int GetManifold(in TGeometry first, in BodyPose firstPose, in SdfGeometry<TField> second,
        in BodyPose secondPose, float contactDistance, Span<CollisionDistance> points)
    {
        var contacts = new SdfContactSet(points, contactDistance, settings.Tolerance);
        Read(in first, in firstPose, in second, in secondPose, ref contacts);
        return contacts.Count;
    }

    private void Read(in TGeometry first, in BodyPose firstPose, in SdfGeometry<TField> second,
        in BodyPose secondPose, ref SdfContactSet contacts)
    {
        for (int axis = 0; axis < 3; axis++)
        {
            Vector3 direction = Vector3.Zero;
            direction[axis] = 1;
            Add(CollisionConvex.Support(in first, in firstPose, direction), in second, in secondPose, ref contacts);
            Add(CollisionConvex.Support(in first, in firstPose, -direction), in second, in secondPose, ref contacts);
        }

        GeometryPointDistance surface = SdfGeometry<TField>.DistanceToPoint(in second, in secondPose, firstPose.Position);
        Vector3 previous = firstPose.Position;
        for (int iteration = 0; iteration < settings.SupportIterations; iteration++)
        {
            CollisionFeaturePoint point = CollisionConvex.Support(in first, in firstPose, -surface.Normal);
            surface = Add(in point, in second, in secondPose, ref contacts);
            if (Vector3.DistanceSquared(point.Point, previous) <= settings.Tolerance * settings.Tolerance) break;
            previous = point.Point;
        }
        Span<CollisionFeaturePoint> face = stackalloc CollisionFeaturePoint[TGeometry.FaceCapacity(in first)];
        int count = TGeometry.GetFace(in first, in firstPose, -surface.Normal, face);
        for (int index = 0; index < count; index++) Add(in face[index], in second, in secondPose, ref contacts);

        var fieldPoints = new FieldPoints(first, firstPose, second, secondPose, contacts);
        SdfSurfaceSampling sampling = settings.Sampling;
        SdfSurfacePoints.Read(in second, in secondPose, in sampling, ref fieldPoints);
        contacts = fieldPoints.Contacts;
    }

    private static GeometryPointDistance Add(in CollisionFeaturePoint point, in SdfGeometry<TField> field,
        in BodyPose pose, ref SdfContactSet contacts)
    {
        GeometryPointDistance distance = SdfGeometry<TField>.DistanceToPoint(in field, in pose, point.Point);
        contacts.Add(new(point.Point, distance.Point, -distance.Normal, distance.Distance, point.Feature, distance.Feature));
        return distance;
    }

    private ref struct FieldPoints(TGeometry first, BodyPose pose, SdfGeometry<TField> second,
        BodyPose secondPose, SdfContactSet contacts) : ISdfSurfaceOutput
    {
        public SdfContactSet Contacts = contacts;
        public void Add(in CollisionFeaturePoint point)
        {
            GeometryPointDistance distance = TGeometry.DistanceToPoint(in first, in pose, point.Point);
            Contacts.Add(new(distance.Point, point.Point, distance.Normal, distance.Distance, distance.Feature, point.Feature));
            var projected = new CollisionFeaturePoint(distance.Point, distance.Feature);
            SdfCollision<TGeometry, TField>.Add(in projected, in second, in secondPose, ref Contacts);
        }
    }
}
