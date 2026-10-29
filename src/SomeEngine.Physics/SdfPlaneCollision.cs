namespace SomeEngine.Physics;

/// <summary>Measures the sampled concrete field surface against a solid plane.</summary>
public readonly struct SdfPlaneCollision<TField>(SdfSurfaceSampling sampling, float tolerance)
    : ICollisionNarrowPhase<SdfGeometry<TField>, PlaneGeometry>
    where TField : struct, ISdfField<TField>
{
    public int ContactCapacity => 4;

    public CollisionDistance Distance(in SdfGeometry<TField> first, in BodyPose firstPose,
        in PlaneGeometry second, in BodyPose secondPose)
    {
        var output = new PlanePoints(second, secondPose, new(Span<CollisionDistance>.Empty, 0, tolerance));
        SdfSurfacePoints.Read(in first, in firstPose, in sampling, ref output);
        return output.Contacts.Best;
    }

    public int GetManifold(in SdfGeometry<TField> first, in BodyPose firstPose, in PlaneGeometry second,
        in BodyPose secondPose, float contactDistance, Span<CollisionDistance> points)
    {
        var output = new PlanePoints(second, secondPose, new(points, contactDistance, tolerance));
        SdfSurfacePoints.Read(in first, in firstPose, in sampling, ref output);
        return output.Contacts.Count;
    }

    private ref struct PlanePoints(PlaneGeometry plane, BodyPose pose, SdfContactSet contacts) : ISdfSurfaceOutput
    {
        public SdfContactSet Contacts = contacts;
        public void Add(in CollisionFeaturePoint point)
        {
            GeometryPointDistance distance = PlaneGeometry.DistanceToPoint(in plane, in pose, point.Point);
            Contacts.Add(new(point.Point, distance.Point, -distance.Normal, distance.Distance, point.Feature, distance.Feature));
        }
    }
}
