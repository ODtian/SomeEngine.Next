using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Physics.Tests;

public sealed class ConvexHullTests
{
    [Fact]
    public void HullRayReturnsTheProjectFaceAndItsFeature()
    {
        using var world = new World();
        var geometry = new ConvexHullGeometry(Tetrahedron(), 0);
        var pose = new BodyPose { Rotation = Quaternion.Identity };
        Entity entity = AddGeometry(world, geometry, pose);
        QueryHandle members = world.Query(new QueryDefinitionBuilder().All<ConvexHullGeometry>());
        CollisionTree tree = Collision.Build(world, members);
        using var queries = Collision.QueryWithResources<ConvexHullGeometry, ConvexHullResources>(world, members, tree, new(1e-5f, 64));

        Assert.True(queries.Raycast(new(new Vector3(3, .5f, .5f), -Vector3.UnitX, 4), out CollisionHit hit));

        Assert.Equal(entity, hit.Entity);
        Near(new Vector3(1, .5f, .5f), hit.Point);
        Near(Vector3.Normalize(Vector3.One), hit.Normal);
        Near(2, hit.Distance);
        Assert.Equal(3, hit.Feature);
        world.ReleaseQuery(members);
    }

    [Fact]
    public void HullTypeBinReturnsFaceAnchorsAndTheActingEntities()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: scheduler);
        var hull = new ConvexHullGeometry(Cube(), 0);
        Entity first = AddGeometry(world, hull, new BodyPose { Rotation = Quaternion.Identity });
        Entity second = AddGeometry(world, hull,
            new BodyPose { Position = new Vector3(0, 1.5f, 0), Rotation = Quaternion.Identity });
        QueryHandle members = world.Query(new QueryDefinitionBuilder().All<ConvexHullGeometry>());
        var pairs = world.Within(members).PartitionPairs();
        using var bins = new CollisionPairBins<RelationPairPartition>(scheduler, 1, 1);
        var results = new List<CapturedContact>();
        pairs.FillPairBins(bins, new HullPairTypes());

        pairs.DetectPairsWithQuery(bins, 0, CollisionGeometry<ConvexHullGeometry, ConvexHullGeometry>.Detect(
            new ConvexCollision<ConvexHullGeometry, ConvexHullGeometry>(new(1e-5f, 32, 64)),
            new CaptureContacts(results), contactDistance: 0));

        Assert.Equal(4, results.Count);
        Assert.All(results, captured =>
        {
            Assert.Equal(first, captured.Contact.First);
            Assert.Equal(second, captured.Contact.Second);
            Assert.Equal(3, captured.Contact.FirstFeature);
            Assert.Equal(2, captured.Contact.SecondFeature);
            Near(Vector3.UnitY, captured.Contact.Normal);
            Near(-.5f, captured.Contact.Separation);
            ContactBody firstBody = Assert.Single(captured.First);
            ContactBody secondBody = Assert.Single(captured.Second);
            Assert.Equal(first, firstBody.Body);
            Assert.Equal(second, secondBody.Body);
            Near(1, firstBody.LocalPoint.Y);
            Near(-1, secondBody.LocalPoint.Y);
        });
        Assert.Equal(4, results.Select(captured => captured.Contact.FirstLocalPoint).Distinct().Count());
        world.ReleaseQuery(members);
    }

    [Fact]
    public void CompletedHullReplacementFeedsBoundsMaintenanceAndQueries()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: scheduler);
        var shared = new ConvexHullGeometry(Cube(), 0);
        Entity changed = AddGeometry(world, shared, new BodyPose { Rotation = Quaternion.Identity });
        Entity retained = AddGeometry(world, shared,
            new BodyPose { Position = new Vector3(8, 0, 0), Rotation = Quaternion.Identity });
        QueryHandle members = world.Query(new QueryDefinitionBuilder().All<ConvexHullGeometry>());
        CollisionTree tree = Collision.Build(world, members);
        using var maintenance = Collision.Candidates(world, members, tree).CreateMaintenance(scheduler);
        maintenance.Schedule().Complete();
        var ready = new TaskCompletionSource();
        JobHandle input = scheduler.CreateExternalFenceHandle(ready.Task);
        JobHandle replaced = new SetConvexHullGeometry(changed, new(Tetrahedron(), 0)).Schedule(world, input);
        var physics = PhysicsSimulation.Create(world, QueryDefinition.Empty,
            new UpdateConvexHullBounds(rowsPerPacket: 1), substeps: 1);
        PhysicsStepResult step = physics.Schedule(0, 0, 1, replaced);
        JobHandle maintained = maintenance.Schedule(step.Completion);

        ready.SetResult();
        maintained.Complete();
        using var queries = Collision.Query<ConvexHullGeometry>(world, members, tree, new(1e-5f, 64));

        Assert.True(queries.Raycast(new(new Vector3(1.25f, .25f, 3), -Vector3.UnitZ, 4), out CollisionHit changedHit));
        Assert.Equal(changed, changedHit.Entity);
        Near(new Vector3(1.25f, .25f, .5f), changedHit.Point);
        Assert.True(queries.Raycast(new(new Vector3(8, 3, 0), -Vector3.UnitY, 4), out CollisionHit retainedHit));
        Assert.Equal(retained, retainedHit.Entity);
        Near(new Vector3(8, 1, 0), retainedHit.Point);
        world.ReleaseQuery(members);
    }

    [Theory]
    [InlineData(3, -2, 1)]
    [InlineData(1.5f, -.5f, -.5f)]
    public void HullDistanceReturnsTheSignedGapForEverySupportedConvexKind(float x, float planeY, float gap)
    {
        using var world = new World();
        var hull = new ConvexHullGeometry(Cube(), 0);
        Entity entity = AddGeometry(world, hull, new BodyPose { Rotation = Quaternion.Identity });
        QueryHandle members = world.Query(new QueryDefinitionBuilder().All<ConvexHullGeometry>());
        CollisionTree tree = Collision.Build(world, members);
        using var queries = Collision.Query<ConvexHullGeometry>(world, members, tree, new(1e-5f, 64));
        var pose = new BodyPose { Position = new Vector3(x, 0, 0), Rotation = Quaternion.Identity };
        var settings = new ConvexCollisionSettings(1e-5f, 64, 128);
        var sphere = new SphereGeometry(1);
        var box = new BoxGeometry(Vector3.One);
        var capsule = new CapsuleGeometry(1, .5f);
        var cylinder = new CylinderGeometry(1, 1);
        var plane = new PlaneGeometry(Vector3.UnitY, 0);
        var planePose = new BodyPose { Position = new Vector3(0, planeY, 0), Rotation = Quaternion.Identity };

        Assert.True(queries.Distance(in sphere, in pose,
            new ConvexCollision<SphereGeometry, ConvexHullGeometry>(settings), out CollisionDistanceHit sphereHit));
        Assert.True(queries.Distance(in box, in pose,
            new ConvexCollision<BoxGeometry, ConvexHullGeometry>(settings), out CollisionDistanceHit boxHit));
        Assert.True(queries.Distance(in capsule, in pose,
            new ConvexCollision<CapsuleGeometry, ConvexHullGeometry>(settings), out CollisionDistanceHit capsuleHit));
        Assert.True(queries.Distance(in cylinder, in pose,
            new ConvexCollision<CylinderGeometry, ConvexHullGeometry>(settings), out CollisionDistanceHit cylinderHit));
        Assert.True(queries.Distance(in hull, in pose,
            new ConvexCollision<ConvexHullGeometry, ConvexHullGeometry>(settings), out CollisionDistanceHit hullHit));
        Assert.True(queries.Distance(in plane, in planePose,
            CollisionGeometry<ConvexHullGeometry, PlaneGeometry>.Reverse(new ConvexPlaneCollision<ConvexHullGeometry>(1e-5f)),
            out CollisionDistanceHit planeHit));

        foreach (CollisionDistanceHit hit in new[] { sphereHit, boxHit, capsuleHit, cylinderHit, hullHit, planeHit })
        {
            Assert.Equal(entity, hit.Entity);
            Near(gap, hit.Distance.Separation);
            Near(1, hit.Distance.Normal.Length());
            Near(gap, Vector3.Dot(hit.Distance.SecondPoint - hit.Distance.FirstPoint, hit.Distance.Normal));
        }
        world.ReleaseQuery(members);
    }

    [Fact]
    public void RoundedHullSweepsAndOverlapUseItsExpandedSurface()
    {
        using var world = new World();
        var hull = new ConvexHullGeometry(Cube(), .1f);
        Entity entity = AddGeometry(world, hull, new BodyPose { Rotation = Quaternion.Identity });
        QueryHandle members = world.Query(new QueryDefinitionBuilder().All<ConvexHullGeometry>());
        CollisionTree tree = Collision.Build(world, members);
        using var queries = Collision.Query<ConvexHullGeometry>(world, members, tree, new(1e-5f, 64));
        var moving = new BoxGeometry(new Vector3(.25f));
        var pose = new BodyPose { Position = new Vector3(3, 0, 0), Rotation = Quaternion.Identity };
        var sweep = new ConvexShapeSweep<BoxGeometry, ConvexHullGeometry, ConvexCollision<BoxGeometry, ConvexHullGeometry>>(
            new(new(1e-5f, 64, 128)), new(1e-5f, 64));

        Assert.True(queries.SweepSphere(new(new Vector3(3, 0, 0), .25f, -Vector3.UnitX, 4), out CollisionHit sphereHit));
        Assert.True(queries.Sweep(in moving, in pose, -Vector3.UnitX, 4, sweep, out CollisionHit boxHit));
        var overlapping = new SphereGeometry(.25f);
        var overlapPose = new BodyPose { Position = new Vector3(1.3f, 0, 0), Rotation = Quaternion.Identity };
        Span<Entity> matches = stackalloc Entity[1];
        int count = queries.Overlap(in overlapping, in overlapPose, new SphereCollision<ConvexHullGeometry>(), matches);

        foreach (CollisionHit hit in new[] { sphereHit, boxHit })
        {
            Assert.Equal(entity, hit.Entity);
            Near(1.65f, hit.Distance);
            Near(1.1f, hit.Point.X);
            Near(Vector3.UnitX, hit.Normal);
        }
        Assert.Equal(1, count);
        Assert.Equal(entity, matches[0]);
        world.ReleaseQuery(members);
    }

    [Fact]
    public void ProjectConvexAndDetectionUseTheSameTypedBinEntry()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: scheduler);
        var projectGeometry = new ProjectHullGeometry(new(Tetrahedron(), 0));
        Entity hull = AddGeometry(world, projectGeometry, new BodyPose { Rotation = Quaternion.Identity });
        Entity sphere = AddGeometry(world, new SphereGeometry(.25f),
            new BodyPose { Position = new Vector3(-.1f, .5f, .5f), Rotation = Quaternion.Identity });
        QueryHandle hulls = world.Query(new QueryDefinitionBuilder().All<ProjectHullGeometry>());
        QueryHandle spheres = world.Query(new QueryDefinitionBuilder().All<SphereGeometry>());
        var pairs = world.Between(hulls, spheres).PartitionPairs();
        using var bins = new CollisionPairBins<RelationPairPartition>(scheduler, 1, 1);
        var results = new List<CapturedContact>();
        pairs.FillPairBins(bins, new ProjectHullPairType());

        pairs.DetectPairsWithQuery(bins, 0, CollisionGeometry<ProjectHullGeometry, SphereGeometry>.Detect(
            new ProjectHullDetection(new(1e-5f, 64, 128)), new CaptureContacts(results), contactDistance: 0));

        CapturedContact captured = Assert.Single(results);
        Assert.Equal(hull, captured.Contact.First);
        Assert.Equal(sphere, captured.Contact.Second);
        Assert.Equal(0, captured.Contact.FirstFeature);
        Near(-Vector3.UnitX, captured.Contact.Normal);
        Near(-.15f, captured.Contact.Separation);
        Near(new Vector3(0, .5f, .5f), captured.Contact.FirstLocalPoint);
        Near(Vector3.UnitX * .25f, captured.Contact.SecondLocalPoint);
        Assert.Equal(hull, Assert.Single(captured.First).Body);
        Assert.Equal(sphere, Assert.Single(captured.Second).Body);
        world.ReleaseQuery(hulls);
        world.ReleaseQuery(spheres);
    }

    [Fact]
    public void TetrahedronVertexPenetrationKeepsBothSurfaceWitnesses()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: scheduler);
        var hull = new ConvexHullGeometry(Tetrahedron(), 0);
        Entity first = AddGeometry(world, hull,
            new BodyPose { Position = new Vector3(.5f), Rotation = Quaternion.Identity });
        Entity second = AddGeometry(world, hull, new BodyPose { Rotation = Quaternion.Identity });
        QueryHandle members = world.Query(new QueryDefinitionBuilder().All<ConvexHullGeometry>());
        var pairs = world.Within(members).PartitionPairs();
        using var bins = new CollisionPairBins<RelationPairPartition>(scheduler, 1, 1);
        var results = new List<CapturedContact>();
        pairs.FillPairBins(bins, new HullPairTypes());

        pairs.DetectPairsWithQuery(bins, 0, CollisionGeometry<ConvexHullGeometry, ConvexHullGeometry>.Detect(
            new ConvexCollision<ConvexHullGeometry, ConvexHullGeometry>(new(1e-5f, 64, 128)),
            new CaptureContacts(results), contactDistance: 0));

        CapturedContact captured = Assert.Single(results);
        Assert.Equal(first, captured.Contact.First);
        Assert.Equal(second, captured.Contact.Second);
        Near(-Vector3.Normalize(Vector3.One), captured.Contact.Normal);
        Near(-.28867513f, captured.Contact.Separation);
        Near(Vector3.Zero, captured.Contact.FirstLocalPoint);
        Near(new Vector3(2f / 3), captured.Contact.SecondLocalPoint);
        Assert.Equal(3, captured.Contact.SecondFeature);
        world.ReleaseQuery(members);
    }

    private static ConvexHullData Tetrahedron() => new(
        (Vector3[])[Vector3.Zero, new(2, 0, 0), new(0, 2, 0), new(0, 0, 2)],
        (HullFace[])[new(-Vector3.UnitX, 0, 3), new(-Vector3.UnitY, 3, 3), new(-Vector3.UnitZ, 6, 3),
            new(Vector3.Normalize(Vector3.One), 9, 3)],
        (int[])[0, 3, 2, 0, 1, 3, 0, 2, 1, 1, 2, 3]);

    private static ConvexHullData Cube() => new(
        (Vector3[])[new(-1, -1, -1), new(1, -1, -1), new(-1, 1, -1), new(1, 1, -1),
            new(-1, -1, 1), new(1, -1, 1), new(-1, 1, 1), new(1, 1, 1)],
        (HullFace[])[new(-Vector3.UnitX, 0, 4), new(Vector3.UnitX, 4, 4), new(-Vector3.UnitY, 8, 4),
            new(Vector3.UnitY, 12, 4), new(-Vector3.UnitZ, 16, 4), new(Vector3.UnitZ, 20, 4)],
        (int[])[0, 4, 6, 2, 1, 3, 7, 5, 0, 1, 5, 4, 2, 6, 7, 3, 0, 2, 3, 1, 4, 5, 7, 6]);

    private readonly record struct CapturedContact(CollisionContact Contact, ContactBody[] First, ContactBody[] Second);

    private readonly struct CaptureContacts(List<CapturedContact> results) : ICollisionContactOutput
    {
        public void Add(in CollisionContact contact, ReadOnlySpan<ContactBody> first, ReadOnlySpan<ContactBody> second) =>
            results.Add(new(contact, first.ToArray(), second.ToArray()));
    }

    private readonly struct HullPairTypes : ICollisionPairClassifier
    {
        public QueryDefinition AddReads(QueryDefinition definition) => definition.WithOptionalRead<ConvexHullGeometry>();
        public CollisionPairType Classify(scoped QueryRow first, scoped QueryRow second) => new(0, 0, 0);
    }

    private readonly struct ProjectHullPairType : ICollisionPairClassifier
    {
        public QueryDefinition AddReads(QueryDefinition definition) => definition;
        public CollisionPairType Classify(scoped QueryRow first, scoped QueryRow second) => new(0, 0, 0);
    }

    private readonly record struct ProjectHullGeometry(ConvexHullGeometry Hull)
        : IComponent, ICollisionConvex<ProjectHullGeometry>
    {
        public static float GetConvexRadius(in ProjectHullGeometry geometry) => geometry.Hull.ConvexRadius;
        public static CollisionBounds Bounds(in ProjectHullGeometry geometry, in BodyPose pose) =>
            ConvexHullGeometry.Bounds(geometry.Hull, in pose);
        public static CollisionFeaturePoint SupportCore(in ProjectHullGeometry geometry, in BodyPose pose,
            Vector3 direction) => ConvexHullGeometry.SupportCore(geometry.Hull, in pose, direction);
        public static int FaceCapacity(in ProjectHullGeometry geometry) => ConvexHullGeometry.FaceCapacity(geometry.Hull);
        public static int GetFace(in ProjectHullGeometry geometry, in BodyPose pose, Vector3 direction,
            Span<CollisionFeaturePoint> points) => ConvexHullGeometry.GetFace(geometry.Hull, in pose, direction, points);
        public static GeometryPointDistance DistanceToPoint(in ProjectHullGeometry geometry, in BodyPose pose,
            Vector3 point) => ConvexHullGeometry.DistanceToPoint(geometry.Hull, in pose, point);
    }

    private readonly struct ProjectHullDetection(ConvexCollisionSettings settings)
        : ICollisionNarrowPhase<ProjectHullGeometry, SphereGeometry>
    {
        public int ContactCapacity => 4;
        public CollisionDistance Distance(in ProjectHullGeometry first, in BodyPose firstPose,
            in SphereGeometry second, in BodyPose secondPose) =>
            new ConvexCollision<ProjectHullGeometry, SphereGeometry>(settings)
                .Distance(in first, in firstPose, in second, in secondPose);
        public int GetManifold(in ProjectHullGeometry first, in BodyPose firstPose, in SphereGeometry second,
            in BodyPose secondPose, float contactDistance, Span<CollisionDistance> points) =>
            new ConvexCollision<ProjectHullGeometry, SphereGeometry>(settings)
                .GetManifold(in first, in firstPose, in second, in secondPose, contactDistance, points);
    }

    private static Entity AddGeometry<TGeometry>(World world, TGeometry geometry, BodyPose pose)
        where TGeometry : struct, IComponent, ICollisionGeometry<TGeometry>
    {
        Entity entity = world.CreateEntity(geometry);
        world.Add(entity, pose);
        world.Add(entity, TGeometry.Bounds(in geometry, in pose));
        return entity;
    }

    private static void Near(Vector3 expected, Vector3 actual) =>
        Assert.InRange(Vector3.Distance(expected, actual), 0, 1e-4f);

    private static void Near(float expected, float actual) =>
        Assert.InRange(MathF.Abs(expected - actual), 0, 1e-4f);
}

public readonly struct ConvexHullReferenceResources : IJobComponentResources<ConvexHullGeometry>
{
    public static void Collect(in ConvexHullGeometry component, ref JobResourceAccessBuilder resources) { }
}

public readonly partial struct SetConvexHullGeometry(Entity target, ConvexHullGeometry geometry) : IJobEntity
{
    private readonly Entity _target = target;
    private readonly ConvexHullGeometry _geometry = geometry;

    public void Execute(Entity entity, [JobResources<ConvexHullReferenceResources>] ref ConvexHullGeometry geometry)
    {
        if (entity == _target) geometry = _geometry;
    }
}
