using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Physics.Tests;

public sealed class SdfGeometryTests
{
    [Fact]
    public void GridDistanceAndDirectionFollowUniformScaleAndTheBodyFrame()
    {
        using var world = new World();
        var data = new SdfGridData(2, 2, 2, new float[] { -1, 1, -1, 1, -1, 1, -1, 1 });
        var field = new TrilinearSdfGrid(data, new SdfGridSampling(new Vector3(-1), new Vector3(2)));
        var geometry = new SdfGeometry<TrilinearSdfGrid>(field, Scale: 2, TraceStepScale: .8f);
        var pose = new BodyPose
        {
            Position = new Vector3(4, 0, 0),
            Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2),
        };
        Entity entity = AddGeometry(world, geometry, pose);
        QueryHandle members = world.Query(new QueryDefinitionBuilder().All<SdfGeometry<TrilinearSdfGrid>>());
        CollisionTree tree = Collision.Build(world, members);
        using var queries = Collision.Query<SdfGeometry<TrilinearSdfGrid>>(world, members, tree, new(1e-5f, 128));
        var sphere = new SphereGeometry(.25f);
        var spherePose = new BodyPose { Position = new Vector3(4, 1, 0), Rotation = Quaternion.Identity };

        Assert.True(queries.Distance(in sphere, in spherePose, new SphereCollision<SdfGeometry<TrilinearSdfGrid>>(),
            out CollisionDistanceHit hit));

        Assert.Equal(entity, hit.Entity);
        Near(.75f, hit.Distance.Separation);
        Near(-Vector3.UnitY, hit.Distance.Normal);
        Near(new Vector3(4, .75f, 0), hit.Distance.FirstPoint);
        Near(new Vector3(4, 0, 0), hit.Distance.SecondPoint);
        Assert.Equal(0, hit.Distance.SecondFeature);
        world.ReleaseQuery(members);
    }

    [Fact]
    public void UserNonconvexFieldRayAndSphereSweepCrossTheHoleToItsInnerSurface()
    {
        using var world = new World();
        var geometry = new SdfGeometry<TorusSdfField>(new(2, .5f), Scale: 1, TraceStepScale: .8f);
        Entity entity = AddGeometry(world, geometry, new BodyPose { Rotation = Quaternion.Identity });
        QueryHandle members = world.Query(new QueryDefinitionBuilder().All<SdfGeometry<TorusSdfField>>());
        CollisionTree tree = Collision.Build(world, members);
        using var queries = Collision.Query<SdfGeometry<TorusSdfField>>(world, members, tree, new(1e-5f, 128));

        Assert.True(queries.Raycast(new(new Vector3(.1f, 0, 0), Vector3.UnitZ, 4), out CollisionHit ray));
        Assert.True(queries.SweepSphere(new(new Vector3(.1f, 0, 0), .25f, Vector3.UnitZ, 4), out CollisionHit sphere));

        Assert.Equal(entity, ray.Entity);
        Assert.Equal(entity, sphere.Entity);
        Near(1.49666295f, ray.Distance);
        Near(new Vector3(.1f, 0, 1.49666295f), ray.Point);
        Near(new Vector3(-.06666667f, 0, -.9977753f), ray.Normal);
        Near(1.2459936f, sphere.Distance);
        Near(1.5f, sphere.Point.Length());
        world.ReleaseQuery(members);
    }

    [Fact]
    public void TwoFieldContactKeepsBothLobesOfTheSampledNonconvexSurface()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: scheduler);
        var firstGeometry = new SdfGeometry<TrilinearSdfGrid>(TwoSphereGrid(), 1, .8f);
        var planeData = new SdfGridData(2, 2, 2, new float[] { -1.25f, -1.25f, 2.75f, 2.75f, -1.25f, -1.25f, 2.75f, 2.75f });
        var secondGeometry = new SdfGeometry<TrilinearSdfGrid>(new(planeData,
            new(new Vector3(-4, -2, -2), new Vector3(8, 4, 4))), 1, .8f);
        Entity first = AddGeometry(world, firstGeometry, new BodyPose { Rotation = Quaternion.Identity });
        Entity second = AddGeometry(world, secondGeometry, new BodyPose { Rotation = Quaternion.Identity });
        QueryHandle members = world.Query(new QueryDefinitionBuilder().All<SdfGeometry<TrilinearSdfGrid>>());
        var pairs = world.Within(members).PartitionPairs();
        using var bins = new CollisionPairBins<RelationPairPartition>(scheduler, 1, 1);
        pairs.FillPairBins(bins, new SdfPairType());
        var contacts = new List<CollisionContact>();
        var sampling = new SdfSurfaceSampling(16, 8, 8, RootIterations: 24, Tolerance: 1e-5f);

        pairs.DetectPairsWithQuery(bins, 0, CollisionGeometry<SdfGeometry<TrilinearSdfGrid>, SdfGeometry<TrilinearSdfGrid>>.Detect(
            new SdfFieldCollision<TrilinearSdfGrid, TrilinearSdfGrid>(sampling, sampling, tolerance: 1e-5f),
            new CaptureContacts(contacts), contactDistance: 0));

        Assert.InRange(contacts.Count, 1, 4);
        Assert.All(contacts, contact =>
        {
            Assert.Equal(first, contact.First);
            Assert.Equal(second, contact.Second);
            Assert.InRange(contact.FirstFeature, 0, int.MaxValue);
            Assert.InRange(contact.SecondFeature, 0, int.MaxValue);
            Assert.True(contact.Separation <= 0);
            Near(1, contact.Normal.Length());
        });
        Assert.Contains(contacts, contact => contact.FirstLocalPoint.X < -1);
        Assert.Contains(contacts, contact => contact.FirstLocalPoint.X > 1);
        Assert.InRange(contacts.Min(contact => contact.Separation), -.3f, -.2f);
        world.ReleaseQuery(members);
    }

    [Theory]
    [InlineData(-1.1f, -.15f)]
    [InlineData(-1.75f, .5f)]
    public void ConvexKindsMeasureTheSameGapAgainstTheSampledNonconvexField(float y, float gap)
    {
        using var world = new World();
        var target = new SdfGeometry<TrilinearSdfGrid>(TwoSphereGrid(), 1, .8f);
        Entity entity = AddGeometry(world, target, new BodyPose { Rotation = Quaternion.Identity });
        QueryHandle members = world.Query(new QueryDefinitionBuilder().All<SdfGeometry<TrilinearSdfGrid>>());
        CollisionTree tree = Collision.Build(world, members);
        using var queries = Collision.Query<SdfGeometry<TrilinearSdfGrid>>(world, members, tree, new(1e-5f, 128));
        var pose = new BodyPose { Position = new Vector3(-2, y, 0), Rotation = Quaternion.Identity };
        var sampling = new SdfSurfaceSampling(16, 8, 8, 24, 1e-5f);
        var settings = new SdfCollisionSettings(sampling, SupportIterations: 16, Tolerance: 1e-5f);
        var sphere = new SphereGeometry(.25f);
        var box = new BoxGeometry(new Vector3(.25f));
        var capsule = new CapsuleGeometry(.1f, .15f);
        var cylinder = new CylinderGeometry(.25f, .25f);
        var hull = new ConvexHullGeometry(CubeHull(), 0);
        var edge = new CapsuleGeometry(0, .25f);
        var face = new TriangleGeometry(new(-.25f, 0, -.25f), new(.25f, 0, -.25f), new(0, 0, .25f), .25f);

        Assert.True(queries.Distance(in sphere, in pose, new SdfCollision<SphereGeometry, TrilinearSdfGrid>(settings), out CollisionDistanceHit sphereHit));
        Assert.True(queries.Distance(in box, in pose, new SdfCollision<BoxGeometry, TrilinearSdfGrid>(settings), out CollisionDistanceHit boxHit));
        Assert.True(queries.Distance(in capsule, in pose, new SdfCollision<CapsuleGeometry, TrilinearSdfGrid>(settings), out CollisionDistanceHit capsuleHit));
        Assert.True(queries.Distance(in cylinder, in pose, new SdfCollision<CylinderGeometry, TrilinearSdfGrid>(settings), out CollisionDistanceHit cylinderHit));
        Assert.True(queries.Distance(in hull, in pose, new SdfCollision<ConvexHullGeometry, TrilinearSdfGrid>(settings), out CollisionDistanceHit hullHit));
        Assert.True(queries.Distance(in edge, in pose, new SdfCollision<CapsuleGeometry, TrilinearSdfGrid>(settings), out CollisionDistanceHit edgeHit));
        Assert.True(queries.Distance(in face, in pose, new SdfCollision<TriangleGeometry, TrilinearSdfGrid>(settings), out CollisionDistanceHit faceHit));

        foreach (CollisionDistanceHit hit in new[] { sphereHit, boxHit, capsuleHit, cylinderHit, hullHit, edgeHit, faceHit })
        {
            Assert.Equal(entity, hit.Entity);
            Near(gap, hit.Distance.Separation);
            Near(1, hit.Distance.Normal.Length());
            Near(gap, Vector3.Dot(hit.Distance.SecondPoint - hit.Distance.FirstPoint, hit.Distance.Normal));
        }
        world.ReleaseQuery(members);
    }

    [Fact]
    public void FieldQueryConsumesTheCompletedSharedSampleProducer()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 0 });
        using var world = WorldJobs.CreateWorld(scheduler: scheduler);
        float[] samples = [-1, 1, -1, 1, -1, 1, -1, 1];
        var geometry = new SdfGeometry<TrilinearSdfGrid>(new(new SdfGridData(2, 2, 2, samples),
            new(new Vector3(-1), new Vector3(2))), 1, .8f);
        Entity entity = AddGeometry(world, geometry, new BodyPose { Rotation = Quaternion.Identity });
        QueryHandle members = world.Query(new QueryDefinitionBuilder().All<SdfGeometry<TrilinearSdfGrid>>());
        CollisionTree tree = Collision.Build(world, members);
        using var queries = Collision.QueryWithResources<SdfGeometry<TrilinearSdfGrid>, SdfGeometryResources<TrilinearSdfGrid>>(
            world, members, tree, new(1e-5f, 128));
        JobHandle written = scheduler.Schedule(new ShiftSdfSamples(samples, -.5f), JobResourceAccess.Write(scheduler, samples));
        var sphere = new SphereGeometry(.25f);
        var pose = new BodyPose { Position = Vector3.UnitX, Rotation = Quaternion.Identity };

        Assert.True(queries.Distance(in sphere, in pose, new SphereCollision<SdfGeometry<TrilinearSdfGrid>>(), out CollisionDistanceHit hit));

        Assert.Equal(entity, hit.Entity);
        Near(.25f, hit.Distance.Separation);
        Near(new Vector3(.5f, 0, 0), hit.Distance.SecondPoint);
        written.Complete();
        world.ReleaseQuery(members);
    }

    [Fact]
    public void PhysicsDetectionBindsAllFieldsThenRunsEachPairOnce()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 0 });
        using var world = WorldJobs.CreateWorld(scheduler: scheduler);
        var original = new SdfGeometry<TrilinearSdfGrid>(new(new SdfGridData(2, 2, 2,
            new float[] { -1, 1, -1, 1, -1, 1, -1, 1 }), new(new Vector3(9, -1, -1), new Vector3(2))), 1, .8f);
        Entity firstSphere = AddGeometry(world, new SphereGeometry(.25f),
            new BodyPose { Position = new Vector3(.5f, 0, 0), Rotation = Quaternion.Identity });
        Entity secondSphere = AddGeometry(world, new SphereGeometry(.25f),
            new BodyPose { Position = new Vector3(4.5f, 0, 0), Rotation = Quaternion.Identity });
        Entity firstField = AddGeometry(world, original, new BodyPose { Rotation = Quaternion.Identity });
        Entity secondField = AddGeometry(world, original,
            new BodyPose { Position = new Vector3(4, 0, 0), Rotation = Quaternion.Identity });
        world.Add(firstField, new FirstSdfGroup());
        world.Add(secondField, new SecondSdfGroup());
        QueryHandle fields = world.Query(new QueryDefinitionBuilder().All<SdfGeometry<TrilinearSdfGrid>>());
        QueryHandle spheres = world.Query(new QueryDefinitionBuilder().All<SphereGeometry>());
        var pairs = world.Between(spheres, fields).Where(new SdfBoundsOverlap()).PartitionPairs();
        using var bins = new CollisionPairBins<RelationPairPartition>(scheduler, 2, 1);
        using var output = new ContactPointBuffer(scheduler, 2, 4);
        var resources = new QueryReadResources();
        resources.Add<SdfGeometry<TrilinearSdfGrid>, SdfGeometryResources<TrilinearSdfGrid>>();
        var contacts = new List<CollisionContact>();
        var narrow = ContactNarrowPhase.Create(0, CollisionGeometry<SphereGeometry, SdfGeometry<TrilinearSdfGrid>>.Detect(
            new SphereCollision<SdfGeometry<TrilinearSdfGrid>>(), new SdfBufferedContacts(output, contacts), 0), resources);
        using var detection = DetectContactPoints.Create(world, pairs, bins, new SdfPairType(), narrow, output,
            ReadOnlySpan<JobResourceAccess>.Empty);
        var stages = new UpdateSdfBounds<TrilinearSdfGrid>(rowsPerPacket: 1).Then(detection);
        var physics = PhysicsSimulation.Create(world, QueryDefinition.Empty, stages, substeps: 1);
        float[] firstSamples = [-1, 1, -1, 1, -1, 1, -1, 1];
        float[] secondSamples = [-1, 1, -1, 1, -1, 1, -1, 1];
        var firstReplacement = new SdfGeometry<TrilinearSdfGrid>(new(new SdfGridData(2, 2, 2, firstSamples),
            new(new Vector3(-1), new Vector3(2))), 1, .8f);
        var secondReplacement = new SdfGeometry<TrilinearSdfGrid>(new(new SdfGridData(2, 2, 2, secondSamples),
            new(new Vector3(-1), new Vector3(2))), 1, .8f);
        var ready = new TaskCompletionSource();
        JobHandle input = scheduler.CreateExternalFenceHandle(ready.Task);
        JobHandle firstReplaced = new ReplaceSdfTestGeometry(firstReplacement).Schedule(world,
            new JobEntityScheduleOptions(filter: new QueryDefinitionBuilder().All<FirstSdfGroup>().Build()), input);
        JobHandle secondReplaced = new ReplaceSdfTestGeometry(secondReplacement).Schedule(world,
            new JobEntityScheduleOptions(filter: new QueryDefinitionBuilder().All<SecondSdfGroup>().Build()), input);
        JobHandle firstWritten = scheduler.Schedule(new ShiftSdfSamples(firstSamples, -.5f),
            JobResourceAccess.Write(scheduler, firstSamples), input);
        JobHandle secondWritten = scheduler.Schedule(new ShiftSdfSamples(secondSamples, -.75f),
            JobResourceAccess.Write(scheduler, secondSamples), input);
        JobHandle inputs = scheduler.CombineDependencies([firstReplaced, secondReplaced, firstWritten, secondWritten]);
        PhysicsStepResult step = physics.Schedule(0, 0, 1, inputs);

        ready.SetResult();
        step.Complete();

        Assert.Equal(2, output.Count);
        Assert.Equal(2, contacts.Count);
        Near(-.25f, Assert.Single(contacts, contact => contact.First == firstSphere && contact.Second == firstField).Separation);
        Near(-.5f, Assert.Single(contacts, contact => contact.First == secondSphere && contact.Second == secondField).Separation);
        world.ReleaseQuery(spheres);
        world.ReleaseQuery(fields);
    }

    [Theory]
    [InlineData(-1.5f, .5f)]
    [InlineData(-.75f, -.25f)]
    public void PlaneDistanceSamplesBothLobesThroughTheCommonQuery(float y, float gap)
    {
        using var world = new World();
        var geometry = new SdfGeometry<TrilinearSdfGrid>(TwoSphereGrid(), 1, .8f);
        Entity entity = AddGeometry(world, geometry, new BodyPose { Rotation = Quaternion.Identity });
        QueryHandle members = world.Query(new QueryDefinitionBuilder().All<SdfGeometry<TrilinearSdfGrid>>());
        CollisionTree tree = Collision.Build(world, members);
        using var queries = Collision.QueryWithResources<SdfGeometry<TrilinearSdfGrid>, SdfGeometryResources<TrilinearSdfGrid>>(
            world, members, tree, new(1e-5f, 128));
        var plane = new PlaneGeometry(Vector3.UnitY, 0);
        var pose = new BodyPose { Position = new Vector3(0, y, 0), Rotation = Quaternion.Identity };
        var collision = new SdfPlaneCollision<TrilinearSdfGrid>(new(16, 8, 8, 24, 1e-5f), tolerance: 1e-5f);

        Assert.True(queries.Distance(in plane, in pose,
            CollisionGeometry<SdfGeometry<TrilinearSdfGrid>, PlaneGeometry>.Reverse(collision), out CollisionDistanceHit hit));

        Assert.Equal(entity, hit.Entity);
        Near(gap, hit.Distance.Separation);
        Near(Vector3.UnitY, hit.Distance.Normal);
        Near(y, hit.Distance.FirstPoint.Y);
        Near(-1, hit.Distance.SecondPoint.Y);
        world.ReleaseQuery(members);
    }

    [Fact]
    public void FieldContactReachesTheDynamicFaceInteriorAndItsThreeNodes()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: scheduler);
        Entity first = world.CreateEntity(new BodyPose { Position = new Vector3(-4, -.5f, -2), Rotation = Quaternion.Identity });
        Entity second = world.CreateEntity(new BodyPose { Position = new Vector3(0, -.5f, -2), Rotation = Quaternion.Identity });
        Entity third = world.CreateEntity(new BodyPose { Position = new Vector3(-2, -.5f, 2), Rotation = Quaternion.Identity });
        Entity face = world.CreateEntity(new DynamicFace(new(first, Vector3.Zero), new(second, Vector3.Zero),
            new(third, Vector3.Zero), Radius: 0));
        Entity field = AddGeometry(world, new SdfGeometry<TrilinearSdfGrid>(TwoSphereGrid(), 1, .8f),
            new BodyPose { Rotation = Quaternion.Identity });
        QueryHandle faces = world.Query(new QueryDefinitionBuilder().All<DynamicFace>());
        QueryHandle fields = world.Query(new QueryDefinitionBuilder().All<SdfGeometry<TrilinearSdfGrid>>());
        var pairs = world.Between(faces, fields).PartitionPairs();
        using var bins = new CollisionPairBins<RelationPairPartition>(scheduler, 1, 1);
        pairs.FillPairBins(bins, new SdfPairType());
        var resources = new QueryReadResources();
        resources.Add<SdfGeometry<TrilinearSdfGrid>, SdfGeometryResources<TrilinearSdfGrid>>();
        var contacts = new List<SdfBodyContact>();
        var settings = new SdfCollisionSettings(new(16, 8, 8, 24, 1e-5f), 16, 1e-5f);

        pairs.DetectPairsWithQuery(bins, 0, CollisionGeometry<TriangleGeometry, SdfGeometry<TrilinearSdfGrid>>.Detect(
            new DynamicFaceReader(), new ComponentGeometry<SdfGeometry<TrilinearSdfGrid>>(),
            new SdfCollision<TriangleGeometry, TrilinearSdfGrid>(settings), new CaptureBodyContacts(contacts), 0), resources);

        Assert.NotEmpty(contacts);
        SdfBodyContact deepest = contacts.MinBy(item => item.Contact.Separation);
        Assert.Equal(face, deepest.Contact.First);
        Assert.Equal(field, deepest.Contact.Second);
        Near(-.5f, deepest.Contact.Separation);
        Assert.True(deepest.Contact.Normal.Y > .8f);
        Near(new Vector3(2, 0, 2), deepest.Contact.FirstLocalPoint);
        Assert.Equal(new ContactBody(first, Vector3.Zero, .25f), deepest.First[0]);
        Assert.Equal(new ContactBody(second, Vector3.Zero, .25f), deepest.First[1]);
        Assert.Equal(new ContactBody(third, Vector3.Zero, .5f), deepest.First[2]);
        world.ReleaseQuery(faces);
        world.ReleaseQuery(fields);
    }

    [Fact]
    public void FieldContactDistributesDistinctAnchorsAlongTheDynamicEdge()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: scheduler);
        Entity first = world.CreateEntity(new BodyPose { Position = new Vector3(-3, -.5f, 0), Rotation = Quaternion.Identity });
        Entity second = world.CreateEntity(new BodyPose { Position = new Vector3(-1, -.5f, 0), Rotation = Quaternion.Identity });
        Entity edge = world.CreateEntity(new DynamicEdge(new(first, Vector3.Zero), new(second, Vector3.Zero)));
        Entity field = AddGeometry(world, new SdfGeometry<TrilinearSdfGrid>(TwoSphereGrid(), 1, .8f),
            new BodyPose { Rotation = Quaternion.Identity });
        QueryHandle edges = world.Query(new QueryDefinitionBuilder().All<DynamicEdge>());
        QueryHandle fields = world.Query(new QueryDefinitionBuilder().All<SdfGeometry<TrilinearSdfGrid>>());
        var pairs = world.Between(edges, fields).PartitionPairs();
        using var bins = new CollisionPairBins<RelationPairPartition>(scheduler, 1, 1);
        pairs.FillPairBins(bins, new SdfPairType());
        var resources = new QueryReadResources();
        resources.Add<SdfGeometry<TrilinearSdfGrid>, SdfGeometryResources<TrilinearSdfGrid>>();
        var contacts = new List<SdfBodyContact>();

        pairs.DetectPairsWithQuery(bins, 0, CollisionGeometry<CapsuleGeometry, SdfGeometry<TrilinearSdfGrid>>.Detect(
            new DynamicEdgeReader(), new ComponentGeometry<SdfGeometry<TrilinearSdfGrid>>(),
            new SdfCollision<CapsuleGeometry, TrilinearSdfGrid>(new(new(16, 8, 8, 24, 1e-5f), 16, 1e-5f)),
            new CaptureBodyContacts(contacts), 0), resources);

        Assert.InRange(contacts.Count, 2, 4);
        Assert.Equal(contacts.Count, contacts.Select(item => item.Contact.FirstLocalPoint).Distinct().Count());
        Near(-.5f, contacts.Min(item => item.Contact.Separation));
        Assert.All(contacts, item =>
        {
            Assert.Equal(edge, item.Contact.First);
            Assert.Equal(field, item.Contact.Second);
            Assert.Equal(first, item.First[0].Body);
            Assert.Equal(second, item.First[1].Body);
            Near(1, item.First[0].Weight + item.First[1].Weight);
        });
        Assert.Contains(contacts, item => item.First[0].Weight > .7f);
        Assert.Contains(contacts, item => item.First[1].Weight > .7f);
        world.ReleaseQuery(edges);
        world.ReleaseQuery(fields);
    }

    [Fact]
    public void ShapeSweepUsesTheSelectedDistanceAcrossTheNonconvexHole()
    {
        using var world = new World();
        var geometry = new SdfGeometry<TorusSdfField>(new(2, .5f), 1, .8f);
        Entity entity = AddGeometry(world, geometry, new BodyPose { Rotation = Quaternion.Identity });
        QueryHandle members = world.Query(new QueryDefinitionBuilder().All<SdfGeometry<TorusSdfField>>());
        CollisionTree tree = Collision.Build(world, members);
        using var queries = Collision.Query<SdfGeometry<TorusSdfField>>(world, members, tree, new(1e-5f, 128));
        var sphere = new SphereGeometry(.25f);
        var pose = new BodyPose { Position = new Vector3(.1f, 0, 0), Rotation = Quaternion.Identity };
        var sweep = new SdfShapeSweep<SphereGeometry, TorusSdfField, SphereCollision<SdfGeometry<TorusSdfField>>>(
            new(), new(1e-5f, 128));

        Assert.True(queries.Sweep(in sphere, in pose, Vector3.UnitZ, 4, sweep, out CollisionHit hit));

        Assert.Equal(entity, hit.Entity);
        Near(1.2459936f, hit.Distance);
        Near(1.5f, hit.Point.Length());
        Near(1, hit.Normal.Length());
        Assert.True(hit.Normal.Z < -.99f);
        world.ReleaseQuery(members);
    }

    private static ConvexHullData CubeHull() => new(
        (Vector3[])[new(-.25f, -.25f, -.25f), new(.25f, -.25f, -.25f), new(-.25f, .25f, -.25f), new(.25f, .25f, -.25f),
            new(-.25f, -.25f, .25f), new(.25f, -.25f, .25f), new(-.25f, .25f, .25f), new(.25f, .25f, .25f)],
        (HullFace[])[new(-Vector3.UnitX, 0, 4), new(Vector3.UnitX, 4, 4), new(-Vector3.UnitY, 8, 4),
            new(Vector3.UnitY, 12, 4), new(-Vector3.UnitZ, 16, 4), new(Vector3.UnitZ, 20, 4)],
        (int[])[0, 4, 6, 2, 1, 3, 7, 5, 0, 1, 5, 4, 2, 6, 7, 3, 0, 2, 3, 1, 4, 5, 7, 6]);

    private static TrilinearSdfGrid TwoSphereGrid()
    {
        var distances = new float[17 * 9 * 9];
        for (int z = 0; z < 9; z++)
        for (int y = 0; y < 9; y++)
        for (int x = 0; x < 17; x++)
        {
            Vector3 point = new(-4 + x * .5f, -2 + y * .5f, -2 + z * .5f);
            distances[x + 17 * (y + 9 * z)] = MathF.Min(
                Vector3.Distance(point, new Vector3(-2, 0, 0)) - 1,
                Vector3.Distance(point, new Vector3(2, 0, 0)) - 1);
        }
        return new(new SdfGridData(17, 9, 9, distances), new(new Vector3(-4, -2, -2), new Vector3(.5f)));
    }

    private readonly struct CaptureContacts(List<CollisionContact> contacts) : ICollisionContactOutput
    {
        public void Add(in CollisionContact contact, ReadOnlySpan<ContactBody> first, ReadOnlySpan<ContactBody> second) =>
            contacts.Add(contact);
    }

    private readonly record struct SdfBodyContact(CollisionContact Contact, ContactBody[] First);

    private readonly struct CaptureBodyContacts(List<SdfBodyContact> contacts) : ICollisionContactOutput
    {
        public void Add(in CollisionContact contact, ReadOnlySpan<ContactBody> first, ReadOnlySpan<ContactBody> second) =>
            contacts.Add(new(contact, first.ToArray()));
    }

    private readonly struct SdfPairType : ICollisionPairClassifier
    {
        public QueryDefinition AddReads(QueryDefinition definition) => definition;
        public CollisionPairType Classify(scoped QueryRow first, scoped QueryRow second) => new(0, 0, 0);
    }

    public readonly struct SdfBoundsOverlap : IRelation<CollisionBounds, CollisionBounds>
    {
        public bool Matches(in CollisionBounds first, in CollisionBounds second) => first.Overlaps(in second);
    }

    private readonly struct FirstSdfGroup : IComponent;
    private readonly struct SecondSdfGroup : IComponent;

    private readonly struct SdfBufferedContacts(ContactPointBuffer output, List<CollisionContact> contacts) : ICollisionContactOutput
    {
        public void Add(in CollisionContact contact, ReadOnlySpan<ContactBody> first, ReadOnlySpan<ContactBody> second)
        {
            new ContactPointOutput(output).Add(in contact, first, second);
            contacts.Add(contact);
        }
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

public readonly record struct TorusSdfField(float MajorRadius, float MinorRadius) : ISdfField<TorusSdfField>
{
    public static CollisionBounds Bounds(in TorusSdfField field) => new(
        new(-field.MajorRadius - field.MinorRadius, -field.MinorRadius, -field.MajorRadius - field.MinorRadius),
        new(field.MajorRadius + field.MinorRadius, field.MinorRadius, field.MajorRadius + field.MinorRadius));

    public static SdfSample Sample(in TorusSdfField field, Vector3 point)
    {
        float radial = new Vector2(point.X, point.Z).Length();
        Vector2 crossSection = new(radial - field.MajorRadius, point.Y);
        float length = crossSection.Length();
        Vector3 gradient = new(point.X / radial * crossSection.X / length,
            crossSection.Y / length, point.Z / radial * crossSection.X / length);
        return new(length - field.MinorRadius, gradient, 0);
    }
}

public readonly struct ShiftSdfSamples(float[] samples, float offset) : IJob
{
    public void Execute()
    {
        for (int index = 0; index < samples.Length; index++) samples[index] += offset;
    }
}

public readonly struct SdfTestReferenceResources : IJobComponentResources<SdfGeometry<TrilinearSdfGrid>>
{
    public static void Collect(in SdfGeometry<TrilinearSdfGrid> component, ref JobResourceAccessBuilder resources) { }
}

public readonly partial struct ReplaceSdfTestGeometry(SdfGeometry<TrilinearSdfGrid> replacement) : IJobEntity
{
    private readonly SdfGeometry<TrilinearSdfGrid> _replacement = replacement;
    public void Execute([JobResources<SdfTestReferenceResources>] ref SdfGeometry<TrilinearSdfGrid> geometry) =>
        geometry = _replacement;
}
