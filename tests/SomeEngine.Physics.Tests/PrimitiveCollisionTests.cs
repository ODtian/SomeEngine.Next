using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Physics.Tests;

public sealed class PrimitiveCollisionTests
{
    [Fact]
    public void SphereDistanceReportsWitnessesNormalAndPenetration()
    {
        var first = new SphereGeometry(1);
        var second = new SphereGeometry(.5f);
        var firstPose = new BodyPose { Rotation = Quaternion.Identity };
        var secondPose = new BodyPose { Position = Vector3.UnitX, Rotation = Quaternion.Identity };

        CollisionDistance distance = new SphereCollision<SphereGeometry>().Distance(
            in first, in firstPose, in second, in secondPose);

        Near(Vector3.UnitX, distance.Normal);
        Near(Vector3.UnitX, distance.FirstPoint);
        Near(new Vector3(.5f, 0, 0), distance.SecondPoint);
        Near(-.5f, distance.Separation);
    }

    [Fact]
    public void SphereBoxDistanceUsesTheCurrentBoxRotation()
    {
        var sphere = new SphereGeometry(.25f);
        var box = new BoxGeometry(new Vector3(1, .5f, .5f));
        var spherePose = new BodyPose { Position = new Vector3(0, 2, 0), Rotation = Quaternion.Identity };
        var boxPose = new BodyPose { Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2) };

        CollisionDistance distance = new SphereCollision<BoxGeometry>().Distance(
            in sphere, in spherePose, in box, in boxPose);

        Near(-Vector3.UnitY, distance.Normal);
        Near(new Vector3(0, 1.75f, 0), distance.FirstPoint);
        Near(Vector3.UnitY, distance.SecondPoint);
        Near(.75f, distance.Separation);
    }

    [Fact]
    public void TypeBinDetectionReadsCurrentComponentsAndDeliversTheActingBodies()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: scheduler);
        Entity first = world.CreateEntity(new SphereGeometry(1));
        Entity second = world.CreateEntity(new SphereGeometry(1));
        world.Add(first, new BodyPose { Rotation = Quaternion.Identity });
        world.Add(second, new BodyPose { Position = new Vector3(5, 0, 0), Rotation = Quaternion.Identity });
        QueryHandle bodies = world.Query(new QueryDefinitionBuilder().All<SphereGeometry>());
        var pairs = world.Within(bodies).PartitionPairs();
        using var bins = new CollisionPairBins<RelationPairPartition>(scheduler, 1, 1);
        var results = new List<CapturedContact>();
        pairs.FillPairBins(bins, new SpherePairTypes());
        world.Replace(second, new BodyPose { Position = new Vector3(1.5f, 0, 0), Rotation = Quaternion.Identity });

        pairs.DetectPairsWithQuery(bins, 0, CollisionGeometry<SphereGeometry, SphereGeometry>.Detect(
            new SphereCollision<SphereGeometry>(), new CaptureContacts(results), contactDistance: 0));

        CapturedContact captured = Assert.Single(results);
        Assert.Equal(first, captured.Contact.First);
        Assert.Equal(second, captured.Contact.Second);
        Near(-.5f, captured.Contact.Separation);
        Near(Vector3.UnitX, captured.Contact.Normal);
        Assert.Equal(new ContactBody(first, Vector3.UnitX, 1), Assert.Single(captured.First));
        Assert.Equal(new ContactBody(second, -Vector3.UnitX, 1), Assert.Single(captured.Second));
        world.ReleaseQuery(bodies);
    }

    [Fact]
    public void ParallelCapsulesReturnTheTwoEndsOfTheirSideContact()
    {
        var first = new CapsuleGeometry(.5f, 1);
        var second = new CapsuleGeometry(.5f, 1);
        var firstPose = new BodyPose { Rotation = Quaternion.Identity };
        var secondPose = new BodyPose { Position = new Vector3(.75f, 0, 0), Rotation = Quaternion.Identity };
        var collision = new CapsuleCollision(1e-5f);
        Span<CollisionDistance> points = stackalloc CollisionDistance[2];

        int count = collision.GetManifold(in first, in firstPose, in second, in secondPose, 0, points);

        Assert.Equal(2, count);
        Near(new Vector3(.5f, -1, 0), points[0].FirstPoint);
        Near(new Vector3(.25f, -1, 0), points[0].SecondPoint);
        Near(new Vector3(.5f, 1, 0), points[1].FirstPoint);
        Near(new Vector3(.25f, 1, 0), points[1].SecondPoint);
        Near(-.25f, points[0].Separation);
        Near(-.25f, points[1].Separation);
    }

    [Fact]
    public void BoxFacesReturnFourAnchorsAcrossTheContactArea()
    {
        var box = new BoxGeometry(Vector3.One);
        var firstPose = new BodyPose { Rotation = Quaternion.Identity };
        var secondPose = new BodyPose { Position = new Vector3(0, 1.5f, 0), Rotation = Quaternion.Identity };
        var collision = new ConvexCollision<BoxGeometry, BoxGeometry>(new(1e-5f, 32, 64));
        Span<CollisionDistance> points = stackalloc CollisionDistance[4];

        int count = collision.GetManifold(in box, in firstPose, in box, in secondPose, 0, points);

        Assert.Equal(4, count);
        var firstPoints = points[..count].ToArray().Select(point => point.FirstPoint).ToArray();
        Assert.Contains(new Vector3(-1, 1, -1), firstPoints);
        Assert.Contains(new Vector3(-1, 1, 1), firstPoints);
        Assert.Contains(new Vector3(1, 1, -1), firstPoints);
        Assert.Contains(new Vector3(1, 1, 1), firstPoints);
        foreach (CollisionDistance point in points[..count])
        {
            Near(Vector3.UnitY, point.Normal);
            Near(-.5f, point.Separation);
            Near(.5f, point.SecondPoint.Y);
        }
    }

    [Fact]
    public void CylinderDistanceFindsTheRoundCapRim()
    {
        var sphere = new SphereGeometry(.25f);
        var cylinder = new CylinderGeometry(1, 1);
        var spherePose = new BodyPose { Position = new Vector3(2, 2, 0), Rotation = Quaternion.Identity };
        var cylinderPose = new BodyPose { Rotation = Quaternion.Identity };

        CollisionDistance distance = new SphereCollision<CylinderGeometry>().Distance(
            in sphere, in spherePose, in cylinder, in cylinderPose);

        Near(new Vector3(1, 1, 0), distance.SecondPoint);
        Near(Vector3.Normalize(new Vector3(-1, -1, 0)), distance.Normal);
        Near(MathF.Sqrt(2) - .25f, distance.Separation);
    }

    [Fact]
    public void BoxPlaneManifoldKeepsTheFourSupportAnchors()
    {
        var box = new BoxGeometry(new Vector3(1, .5f, 1));
        var plane = new PlaneGeometry(Vector3.UnitY, 0);
        var boxPose = new BodyPose { Position = new Vector3(0, .25f, 0), Rotation = Quaternion.Identity };
        var planePose = new BodyPose { Rotation = Quaternion.Identity };
        var collision = new ConvexPlaneCollision<BoxGeometry>(1e-5f);
        Span<CollisionDistance> points = stackalloc CollisionDistance[4];

        int count = collision.GetManifold(in box, in boxPose, in plane, in planePose, 0, points);

        Assert.Equal(4, count);
        foreach (CollisionDistance point in points[..count])
        {
            Near(-.25f, point.FirstPoint.Y);
            Near(0, point.SecondPoint.Y);
            Near(-Vector3.UnitY, point.Normal);
            Near(-.25f, point.Separation);
        }
    }

    [Fact]
    public void GeometryReadersUseSimulationEntitiesOutsidePairMembership()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: scheduler);
        Entity firstBody = world.CreateEntity(new BodyPose { Rotation = Quaternion.Identity });
        Entity secondBody = world.CreateEntity(new BodyPose { Position = new Vector3(1.5f, 0, 0), Rotation = Quaternion.Identity });
        Entity first = world.CreateEntity(new SphereGeometry(1));
        Entity second = world.CreateEntity(new SphereGeometry(1));
        world.Add(first, new PrimitiveBodyReference(firstBody));
        world.Add(second, new PrimitiveBodyReference(secondBody));
        QueryHandle members = world.Query(new QueryDefinitionBuilder().All<SphereGeometry>());
        var pairs = world.Within(members).PartitionPairs();
        using var bins = new CollisionPairBins<RelationPairPartition>(scheduler, 1, 1);
        var results = new List<CapturedContact>();
        pairs.FillPairBins(bins, new SpherePairTypes());

        pairs.DetectPairsWithQuery(bins, 0, CollisionGeometry<SphereGeometry, SphereGeometry>.Detect(
            new ReferencedSphere(), new ReferencedSphere(), new SphereCollision<SphereGeometry>(),
            new CaptureContacts(results), contactDistance: 0));

        CapturedContact captured = Assert.Single(results);
        Near(-.5f, captured.Contact.Separation);
        Assert.Equal(new ContactBody(firstBody, Vector3.UnitX, 1), Assert.Single(captured.First));
        Assert.Equal(new ContactBody(secondBody, -Vector3.UnitX, 1), Assert.Single(captured.Second));
        world.ReleaseQuery(members);
    }

    [Theory]
    [InlineData(3f, 1f)]
    [InlineData(1.5f, -.5f)]
    public void SupportMappedPrimitiveCombinationsReportTheSignedGap(float centerDistance, float expected)
    {
        var sphere = new SphereGeometry(1);
        var box = new BoxGeometry(Vector3.One);
        var capsule = new CapsuleGeometry(1, .5f);
        var cylinder = new CylinderGeometry(1, 1);
        var first = new BodyPose { Rotation = Quaternion.Identity };
        var second = new BodyPose { Position = new Vector3(centerDistance, 0, 0), Rotation = Quaternion.Identity };
        var settings = new ConvexCollisionSettings(1e-5f, 32, 64);

        Near(expected, new ConvexCollision<SphereGeometry, SphereGeometry>(settings)
            .Distance(in sphere, in first, in sphere, in second).Separation);
        Near(expected, new ConvexCollision<SphereGeometry, BoxGeometry>(settings)
            .Distance(in sphere, in first, in box, in second).Separation);
        Near(expected, new ConvexCollision<SphereGeometry, CapsuleGeometry>(settings)
            .Distance(in sphere, in first, in capsule, in second).Separation);
        Near(expected, new ConvexCollision<SphereGeometry, CylinderGeometry>(settings)
            .Distance(in sphere, in first, in cylinder, in second).Separation);
        Near(expected, new ConvexCollision<BoxGeometry, BoxGeometry>(settings)
            .Distance(in box, in first, in box, in second).Separation);
        Near(expected, new ConvexCollision<BoxGeometry, CapsuleGeometry>(settings)
            .Distance(in box, in first, in capsule, in second).Separation);
        Near(expected, new ConvexCollision<BoxGeometry, CylinderGeometry>(settings)
            .Distance(in box, in first, in cylinder, in second).Separation);
        Near(expected, new ConvexCollision<CapsuleGeometry, CapsuleGeometry>(settings)
            .Distance(in capsule, in first, in capsule, in second).Separation);
        Near(expected, new ConvexCollision<CapsuleGeometry, CylinderGeometry>(settings)
            .Distance(in capsule, in first, in cylinder, in second).Separation);
        Near(expected, new ConvexCollision<CylinderGeometry, CylinderGeometry>(settings)
            .Distance(in cylinder, in first, in cylinder, in second).Separation);
    }

    [Fact]
    public void PrimitivePlaneDistancesUseTheCurrentPlaneFrame()
    {
        var sphere = new SphereGeometry(1);
        var box = new BoxGeometry(Vector3.One);
        var capsule = new CapsuleGeometry(1, .5f);
        var cylinder = new CylinderGeometry(1, 1);
        var plane = new PlaneGeometry(Vector3.UnitY, 0);
        var first = new BodyPose { Position = new Vector3(2, 0, 0), Rotation = Quaternion.Identity };
        var second = new BodyPose
        {
            Position = new Vector3(4, 0, 0), Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2),
        };

        Near(1, new ConvexPlaneCollision<SphereGeometry>(1e-5f).Distance(in sphere, in first, in plane, in second).Separation);
        Near(1, new ConvexPlaneCollision<BoxGeometry>(1e-5f).Distance(in box, in first, in plane, in second).Separation);
        Near(1, new ConvexPlaneCollision<CapsuleGeometry>(1e-5f).Distance(in capsule, in first, in plane, in second).Separation);
        Near(1, new ConvexPlaneCollision<CylinderGeometry>(1e-5f).Distance(in cylinder, in first, in plane, in second).Separation);
    }

    private readonly struct ReferencedSphere : ICollisionGeometryReader<SphereGeometry>
    {
        public QueryDefinition AddReads(QueryDefinition definition) => definition.WithOptionalRead<SphereGeometry>()
            .WithOptionalRead<PrimitiveBodyReference>().WithOptionalRead<BodyPose>();

        public SphereGeometry ReadGeometry(scoped QueryRow row, scoped QueryCursor access, out BodyPose pose)
        {
            access.TryGetRow(row.Read<PrimitiveBodyReference>().Body, out QueryRow body);
            pose = body.Read<BodyPose>();
            return row.Read<SphereGeometry>();
        }

        public int GetBodyCount(scoped QueryRow row, scoped QueryCursor access) => 1;
        public void GetBodies(scoped QueryRow row, scoped QueryCursor access, Vector3 point, Span<ContactBody> bodies)
        {
            Entity entity = row.Read<PrimitiveBodyReference>().Body;
            access.TryGetRow(entity, out QueryRow body);
            BodyPose pose = body.Read<BodyPose>();
            bodies[0] = new(entity, Vector3.Transform(point - pose.Position, Quaternion.Conjugate(pose.Rotation)), 1);
        }
    }

    private readonly record struct CapturedContact(CollisionContact Contact, ContactBody[] First, ContactBody[] Second);

    private readonly struct CaptureContacts(List<CapturedContact> results) : ICollisionContactOutput
    {
        public void Add(in CollisionContact contact, ReadOnlySpan<ContactBody> first, ReadOnlySpan<ContactBody> second) =>
            results.Add(new(contact, first.ToArray(), second.ToArray()));
    }

    private readonly struct SpherePairTypes : ICollisionPairClassifier
    {
        public QueryDefinition AddReads(QueryDefinition definition) => definition.WithOptionalRead<SphereGeometry>();
        public CollisionPairType Classify(scoped QueryRow first, scoped QueryRow second) => new(0, 0, 0);
    }

    private static void Near(Vector3 expected, Vector3 actual) =>
        Assert.InRange(Vector3.Distance(expected, actual), 0, 1e-4f);

    private static void Near(float expected, float actual) =>
        Assert.InRange(MathF.Abs(expected - actual), 0, 1e-4f);
}

public readonly record struct PrimitiveBodyReference(Entity Body) : IComponent;
