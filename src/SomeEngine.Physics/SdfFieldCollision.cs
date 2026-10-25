using System.Numerics;

namespace SomeEngine.Physics;

/// <summary>Samples both zero surfaces and measures each point against the other concrete field.</summary>
public readonly struct SdfFieldCollision<TFirst, TSecond>(SdfSurfaceSampling firstSampling,
    SdfSurfaceSampling secondSampling, float tolerance)
    : ICollisionNarrowPhase<SdfGeometry<TFirst>, SdfGeometry<TSecond>>
    where TFirst : struct, ISdfField<TFirst>
    where TSecond : struct, ISdfField<TSecond>
{
    public int ContactCapacity => 4;

    public CollisionDistance Distance(in SdfGeometry<TFirst> first, in BodyPose firstPose,
        in SdfGeometry<TSecond> second, in BodyPose secondPose)
    {
        var contacts = new SdfContactSet(Span<CollisionDistance>.Empty, 0, tolerance);
        Read(in first, in firstPose, in second, in secondPose, ref contacts);
        return contacts.Best;
    }

    public int GetManifold(in SdfGeometry<TFirst> first, in BodyPose firstPose, in SdfGeometry<TSecond> second,
        in BodyPose secondPose, float contactDistance, Span<CollisionDistance> points)
    {
        var contacts = new SdfContactSet(points, contactDistance, tolerance);
        Read(in first, in firstPose, in second, in secondPose, ref contacts);
        return contacts.Count;
    }

    private void Read(in SdfGeometry<TFirst> first, in BodyPose firstPose, in SdfGeometry<TSecond> second,
        in BodyPose secondPose, ref SdfContactSet contacts)
    {
        var firstPoints = new FirstPoints(second, secondPose, contacts);
        SdfSurfacePoints.Read(in first, in firstPose, in firstSampling, ref firstPoints);
        var secondPoints = new SecondPoints(first, firstPose, firstPoints.Contacts);
        SdfSurfacePoints.Read(in second, in secondPose, in secondSampling, ref secondPoints);
        contacts = secondPoints.Contacts;
    }

    private ref struct FirstPoints(SdfGeometry<TSecond> second, BodyPose pose, SdfContactSet contacts) : ISdfSurfaceOutput
    {
        public SdfContactSet Contacts = contacts;
        public void Add(in CollisionFeaturePoint point)
        {
            GeometryPointDistance distance = SdfGeometry<TSecond>.DistanceToPoint(in second, in pose, point.Point);
            Contacts.Add(new(point.Point, distance.Point, -distance.Normal, distance.Distance, point.Feature, distance.Feature));
        }
    }

    private ref struct SecondPoints(SdfGeometry<TFirst> first, BodyPose pose, SdfContactSet contacts) : ISdfSurfaceOutput
    {
        public SdfContactSet Contacts = contacts;
        public void Add(in CollisionFeaturePoint point)
        {
            GeometryPointDistance distance = SdfGeometry<TFirst>.DistanceToPoint(in first, in pose, point.Point);
            Contacts.Add(new(distance.Point, point.Point, distance.Normal, distance.Distance, distance.Feature, point.Feature));
        }
    }
}
