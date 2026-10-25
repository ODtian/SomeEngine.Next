using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Physics.Tests;

public sealed class DynamicGeometryTests
{
    [Fact]
    public void CapsuleQueryReadsTheReferencedNodesOutsideColliderMembership()
    {
        using var world = new World();
        Entity first = AddNode(world, new Vector3(0, -1, 0));
        Entity second = AddNode(world, new Vector3(0, 1, 0));
        Entity capsule = world.CreateEntity(new DynamicCapsule(new(first, Vector3.Zero), new(second, Vector3.Zero), .25f));
        world.Add(capsule, new CollisionBounds(new(-.25f, -1.25f, -.25f), new(.25f, 1.25f, .25f)));
        QueryHandle members = world.Query(new QueryDefinitionBuilder().All<DynamicCapsule>());
        CollisionTree tree = Collision.Build(world, members);
        using var queries = Collision.Query<CapsuleGeometry, DynamicCapsuleReader>(world, members, tree,
            new DynamicCapsuleReader(), new(1e-5f, 64));

        Assert.True(queries.Raycast(new(new Vector3(3, .5f, 0), -Vector3.UnitX, 4), out CollisionHit hit));

        Assert.Equal(capsule, hit.Entity);
        Near(new Vector3(.25f, .5f, 0), hit.Point);
        Near(Vector3.UnitX, hit.Normal);
        Near(2.75f, hit.Distance);
        Assert.True(queries.TryGetTransform(capsule, out Matrix4x4 transform, out _));
        Near(Vector3.Zero, transform.Translation);
        world.ReleaseQuery(members);
    }

    [Fact]
    public void NodeMotionFeedsDynamicBoundsMaintenanceAndTheNextQuery()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: scheduler);
        Entity first = AddNode(world, new Vector3(0, -1, 0));
        Entity second = AddNode(world, new Vector3(0, 1, 0));
        Entity capsule = world.CreateEntity(new DynamicCapsule(new(first, Vector3.Zero), new(second, Vector3.Zero), .25f));
        world.Add(capsule, new CollisionBounds(new(-.25f, -1.25f, -.25f), new(.25f, 1.25f, .25f)));
        QueryHandle members = world.Query(new QueryDefinitionBuilder().All<DynamicCapsule>());
        var reader = new DynamicCapsuleReader();
        QueryHandle access = world.Query(reader.AddReads(QueryDefinition.Empty));
        CollisionTree tree = Collision.Build(world, members);
        using var maintenance = Collision.Candidates(world, members, tree).CreateMaintenance(scheduler);
        maintenance.Schedule().Complete();
        JobHandle moved = new MoveDynamicNodes(new Vector3(4, 0, 0)).ScheduleParallel(world);
        var physics = PhysicsSimulation.Create(world, QueryDefinition.Empty,
            new UpdateGeometryBounds<CapsuleGeometry, DynamicCapsuleReader>(world, members, access, reader, rowsPerPacket: 1),
            substeps: 1);

        PhysicsStepResult step = physics.Schedule(0, 0, 1, moved);
        maintenance.Schedule(step.Completion).Complete();
        using var queries = Collision.Query<CapsuleGeometry, DynamicCapsuleReader>(world, members, tree, reader, new(1e-5f, 64));

        Assert.True(queries.Raycast(new(new Vector3(7, .5f, 0), -Vector3.UnitX, 4), out CollisionHit hit));
        Assert.Equal(capsule, hit.Entity);
        Near(new Vector3(4.25f, .5f, 0), hit.Point);
        Near(2.75f, hit.Distance);
        world.ReleaseQuery(access);
        world.ReleaseQuery(members);
    }

    [Fact]
    public void EdgeSphereSweepReadsRotatedLocalSamplesOnTheBody()
    {
        using var world = new World();
        Entity body = world.CreateEntity(new BodyPose
        {
            Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2),
        });
        Entity edge = world.CreateEntity(new DynamicEdge(new(body, -Vector3.UnitX), new(body, Vector3.UnitX)));
        world.Add(edge, new CollisionBounds(new(0, -1, 0), new(0, 1, 0)));
        QueryHandle members = world.Query(new QueryDefinitionBuilder().All<DynamicEdge>());
        CollisionTree tree = Collision.Build(world, members);
        using var queries = Collision.Query<CapsuleGeometry, DynamicEdgeReader>(world, members, tree,
            new DynamicEdgeReader(), new(1e-5f, 64));

        Assert.True(queries.SweepSphere(new(new Vector3(3, .5f, 0), .25f, -Vector3.UnitX, 4), out CollisionHit hit));

        Assert.Equal(edge, hit.Entity);
        Near(new Vector3(0, .5f, 0), hit.Point);
        Near(Vector3.UnitX, hit.Normal);
        Near(2.75f, hit.Distance);
        world.ReleaseQuery(members);
    }

    [Fact]
    public void FaceContactDistributesItsAreaPointAcrossThreeActualNodes()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: scheduler);
        Entity first = AddNode(world, new Vector3(0, 2, 0));
        Entity second = AddNode(world, new Vector3(2, 2, 0));
        Entity third = AddNode(world, new Vector3(0, 2, 2));
        Entity face = world.CreateEntity(new DynamicFace(new(first, Vector3.Zero), new(second, Vector3.Zero),
            new(third, Vector3.Zero), Radius: 0));
        Entity sphere = world.CreateEntity(new SphereGeometry(.25f));
        world.Add(sphere, new BodyPose { Position = new Vector3(.5f, 1.9f, .5f), Rotation = Quaternion.Identity });
        QueryHandle faces = world.Query(new QueryDefinitionBuilder().All<DynamicFace>());
        QueryHandle spheres = world.Query(new QueryDefinitionBuilder().All<SphereGeometry>());
        var pairs = world.Between(faces, spheres).PartitionPairs();
        using var bins = new CollisionPairBins<RelationPairPartition>(scheduler, 1, 1);
        var results = new List<CapturedContact>();
        pairs.FillPairBins(bins, new DynamicPairType());

        pairs.DetectPairsWithQuery(bins, 0, CollisionGeometry<TriangleGeometry, SphereGeometry>.Detect(
            new DynamicFaceReader(), new ComponentGeometry<SphereGeometry>(),
            new ConvexCollision<TriangleGeometry, SphereGeometry>(new(1e-5f, 64, 128)),
            new CaptureContacts(results), contactDistance: 0));

        CapturedContact captured = Assert.Single(results);
        Assert.Equal(face, captured.Contact.First);
        Assert.Equal(sphere, captured.Contact.Second);
        Near(-Vector3.UnitY, captured.Contact.Normal);
        Near(-.15f, captured.Contact.Separation);
        Near(new Vector3(.5f, 0, .5f), captured.Contact.FirstLocalPoint);
        Assert.Equal(new ContactBody(first, Vector3.Zero, .5f), captured.First[0]);
        Assert.Equal(new ContactBody(second, Vector3.Zero, .25f), captured.First[1]);
        Assert.Equal(new ContactBody(third, Vector3.Zero, .25f), captured.First[2]);
        Assert.Equal(sphere, Assert.Single(captured.Second).Body);
        world.ReleaseQuery(faces);
        world.ReleaseQuery(spheres);
    }

    [Fact]
    public void FaceDeformationUpdatesItsBoundsAndIndependentQueries()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: scheduler);
        Entity first = AddNode(world, Vector3.Zero);
        Entity second = AddNode(world, new Vector3(2, 0, 0));
        Entity third = AddNode(world, new Vector3(0, 0, 2));
        Entity face = world.CreateEntity(new DynamicFace(new(first, Vector3.Zero), new(second, Vector3.Zero),
            new(third, Vector3.Zero), Radius: 0));
        world.Add(face, new CollisionBounds());
        var reader = new DynamicFaceReader();
        QueryHandle members = world.Query(new QueryDefinitionBuilder().All<DynamicFace>());
        QueryHandle access = world.Query(reader.AddReads(QueryDefinition.Empty));
        var physics = PhysicsSimulation.Create(world, QueryDefinition.Empty,
            new UpdateGeometryBounds<TriangleGeometry, DynamicFaceReader>(world, members, access, reader, rowsPerPacket: 1),
            substeps: 1);
        physics.Schedule(0, 0, 1).Complete();
        CollisionTree tree = Collision.Build(world, members);
        using var maintenance = Collision.Candidates(world, members, tree).CreateMaintenance(scheduler);
        maintenance.Schedule().Complete();
        world.Replace(third, new BodyPose { Position = new Vector3(0, 1, 2), Rotation = Quaternion.Identity });
        PhysicsStepResult step = physics.Schedule(1, 1, 1);
        maintenance.Schedule(step.Completion).Complete();
        using var queries = Collision.Query<TriangleGeometry, DynamicFaceReader>(world, members, tree, reader, new(1e-5f, 64));

        Assert.True(queries.Raycast(new(new Vector3(.5f, 3, .5f), -Vector3.UnitY, 4), out CollisionHit ray));
        var sphere = new SphereGeometry(.25f);
        var spherePose = new BodyPose { Position = new Vector3(.5f, 1, .5f), Rotation = Quaternion.Identity };
        Assert.True(queries.Distance(in sphere, in spherePose, new SphereCollision<TriangleGeometry>(), out CollisionDistanceHit distance));
        spherePose.Position = new Vector3(.5f, .3f, .5f);
        Span<Entity> matches = stackalloc Entity[1];
        int count = queries.Overlap(in sphere, in spherePose, new SphereCollision<TriangleGeometry>(), matches);

        Assert.Equal(face, ray.Entity);
        Near(new Vector3(.5f, .25f, .5f), ray.Point);
        Near(Vector3.Normalize(new Vector3(0, 2, -1)), ray.Normal);
        Assert.Equal(face, distance.Entity);
        Near(.4208204f, distance.Distance.Separation);
        Assert.Equal(1, count);
        Assert.Equal(face, matches[0]);
        world.ReleaseQuery(access);
        world.ReleaseQuery(members);
    }

    [Fact]
    public void CapsuleContactKeepsSamplingAnchorsAndTheRadiusGap()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: scheduler);
        Entity first = AddNode(world, new Vector3(0, -1, 0));
        Entity second = AddNode(world, new Vector3(0, 1, 0));
        Entity capsule = world.CreateEntity(new DynamicCapsule(new(first, Vector3.Zero), new(second, Vector3.Zero), .25f));
        Entity sphere = world.CreateEntity(new SphereGeometry(.25f));
        world.Add(sphere, new BodyPose { Position = new Vector3(.4f, .5f, 0), Rotation = Quaternion.Identity });
        QueryHandle capsules = world.Query(new QueryDefinitionBuilder().All<DynamicCapsule>());
        QueryHandle spheres = world.Query(new QueryDefinitionBuilder().All<SphereGeometry>());
        var pairs = world.Between(capsules, spheres).PartitionPairs();
        using var bins = new CollisionPairBins<RelationPairPartition>(scheduler, 1, 1);
        var results = new List<CapturedContact>();
        pairs.FillPairBins(bins, new DynamicPairType());

        pairs.DetectPairsWithQuery(bins, 0, CollisionGeometry<CapsuleGeometry, SphereGeometry>.Detect(
            new DynamicCapsuleReader(), new ComponentGeometry<SphereGeometry>(),
            new ConvexCollision<CapsuleGeometry, SphereGeometry>(new(1e-5f, 64, 128)),
            new CaptureContacts(results), contactDistance: 0));

        CapturedContact captured = Assert.Single(results);
        Assert.Equal(capsule, captured.Contact.First);
        Assert.Equal(sphere, captured.Contact.Second);
        Near(Vector3.UnitX, captured.Contact.Normal);
        Near(-.1f, captured.Contact.Separation);
        Near(new Vector3(.25f, .5f, 0), captured.Contact.FirstLocalPoint);
        Assert.Equal(new ContactBody(first, Vector3.Zero, .25f), captured.First[0]);
        Assert.Equal(new ContactBody(second, Vector3.Zero, .75f), captured.First[1]);
        Near(-Vector3.UnitX * .25f, Assert.Single(captured.Second).LocalPoint);
        world.ReleaseQuery(capsules);
        world.ReleaseQuery(spheres);
    }

    [Fact]
    public void FaceContactKeepsDifferentSamplingAnchorsOnOneBody()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: scheduler);
        Entity body = AddNode(world, new Vector3(0, 2, 0));
        Entity face = world.CreateEntity(new DynamicFace(new(body, Vector3.Zero), new(body, new Vector3(2, 0, 0)),
            new(body, new Vector3(0, 0, 2)), Radius: 0));
        Entity sphere = world.CreateEntity(new SphereGeometry(.25f));
        world.Add(sphere, new BodyPose { Position = new Vector3(.5f, 1.9f, .5f), Rotation = Quaternion.Identity });
        QueryHandle faces = world.Query(new QueryDefinitionBuilder().All<DynamicFace>());
        QueryHandle spheres = world.Query(new QueryDefinitionBuilder().All<SphereGeometry>());
        var pairs = world.Between(faces, spheres).PartitionPairs();
        using var bins = new CollisionPairBins<RelationPairPartition>(scheduler, 1, 1);
        var results = new List<CapturedContact>();
        pairs.FillPairBins(bins, new DynamicPairType());

        pairs.DetectPairsWithQuery(bins, 0, CollisionGeometry<TriangleGeometry, SphereGeometry>.Detect(
            new DynamicFaceReader(), new ComponentGeometry<SphereGeometry>(),
            new ConvexCollision<TriangleGeometry, SphereGeometry>(new(1e-5f, 64, 128)),
            new CaptureContacts(results), contactDistance: 0));

        CapturedContact captured = Assert.Single(results);
        Assert.Equal(face, captured.Contact.First);
        Near(-.15f, captured.Contact.Separation);
        Assert.Equal(3, captured.First.Length);
        Assert.Equal(new ContactBody(body, Vector3.Zero, .5f), captured.First[0]);
        Assert.Equal(new ContactBody(body, new Vector3(2, 0, 0), .25f), captured.First[1]);
        Assert.Equal(new ContactBody(body, new Vector3(0, 0, 2), .25f), captured.First[2]);
        world.ReleaseQuery(faces);
        world.ReleaseQuery(spheres);
    }

    private static Entity AddNode(World world, Vector3 position) =>
        world.CreateEntity(new BodyPose { Position = position, Rotation = Quaternion.Identity });

    private readonly record struct CapturedContact(CollisionContact Contact, ContactBody[] First, ContactBody[] Second);

    private readonly struct CaptureContacts(List<CapturedContact> results) : ICollisionContactOutput
    {
        public void Add(in CollisionContact contact, ReadOnlySpan<ContactBody> first, ReadOnlySpan<ContactBody> second) =>
            results.Add(new(contact, first.ToArray(), second.ToArray()));
    }

    private readonly struct DynamicPairType : ICollisionPairClassifier
    {
        public QueryDefinition AddReads(QueryDefinition definition) => definition;
        public CollisionPairType Classify(scoped QueryRow first, scoped QueryRow second) => new(0, 0, 0);
    }

    private static void Near(Vector3 expected, Vector3 actual) =>
        Assert.InRange(Vector3.Distance(expected, actual), 0, 1e-4f);

    private static void Near(float expected, float actual) =>
        Assert.InRange(MathF.Abs(expected - actual), 0, 1e-4f);
}

public readonly partial struct MoveDynamicNodes(Vector3 displacement) : IJobEntity
{
    private readonly Vector3 _displacement = displacement;
    public void Execute(ref BodyPose pose) => pose.Position += _displacement;
}
