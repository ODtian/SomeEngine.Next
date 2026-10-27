using System.Numerics;
using System.Runtime.InteropServices;
using SlangShaderSharp;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;
using SomeEngine.Physics;
using Xunit;

namespace SomeEngine.Graphics.Direct3D12.Tests;

[Collection("Direct3D12 shader compilation")]
[Trait("Category", "Hardware")]
public sealed partial class DeviceCollisionDetectionTests(D3D12TestCompiler shaderCompiler)
{
    [Fact]
    public void IndependentUserDeviceQueryFeedsItsContactFeaturesThroughTheExistingResponse()
    {
        using var backend = D3D12GraphicsBackend.Create();
        using Device device = CreateDevice(backend);
        using var jobs = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using World world = WorldJobs.CreateWorld(scheduler: jobs);
        using var grid = new DeviceCollisionGrid(backend, device, CompileGrid(),
            new DeviceGridSettings(1, 3), new DeviceGridCapacity(2, 256, 257, 4, 1));
        using var geometry = new DeviceCollisionGeometry(backend, device, new DeviceGeometryCapacity(2, 0, 0, 0));
        Entity first = CreateSphere(world, grid, geometry, 0, Vector3.Zero);
        Entity second = CreateSphere(world, grid, geometry, 1, new Vector3(1.5f, 0, 0));
        world.Add(first, new ContactMaterial { NormalStiffness = 100 });
        world.Add(second, new ContactMaterial { NormalStiffness = 100 });
        world.Add(first, new BodyMass { Motion = BodyMotion.Static, InverseInertia = Matrix4x4.Identity });
        world.Add(second, new BodyMass { Motion = BodyMotion.Dynamic, InverseMass = 1, InverseInertia = Matrix4x4.Identity });
        foreach (Entity body in new[] { first, second })
        {
            world.Add(body, new BodyVelocity());
            world.Add(body, new BodyStepState());
            world.Add(body, new AvbdBodyEquation());
        }
        QueryDefinition bodies = world.QueryDefinition().All<SphereGeometry>().Build();
        QueryHandle members = world.Query(bodies);
        var pairs = world.Within(members);
        using var seed = new DeviceCollisionDetection(backend, device, grid, geometry,
            CompileDetection("DeviceConvexCollision<DeviceSphere, DeviceSphere>"), 0,
            new DeviceDetectionCapacity(4, 4, 1), new ConvexCollisionSettings(1e-5f, 32, 64), 0,
            DeviceInputTransfer.Upload, DeviceCollisionReadback.None);
        var initial = new PredictBodies(Vector3.Zero, 64).Then(grid.BeginInputs())
            .Then(grid.WriteGeometryInputs<SphereGeometry, DeviceSphereReader>(pairs, default, 0, 64))
            .Then(grid.Build(pairs, DevicePairRules.None, DeviceInputTransfer.Upload, DevicePairReadback.None))
            .Then(seed.Detect());
        PhysicsStepResult step = PhysicsSimulation.Create(world, bodies, initial, 1).Schedule(7, 2, 0.25);
        using SomeEngine.Graphics.Buffer features = backend.CreateBuffer(device, new(8, BufferUsages.ShaderRead), MemoryType.Upload);
        using (MappedBuffer mapping = backend.Map(features, MapType.Write, BufferRange.Whole))
        {
            uint[] values = [17, 23];
            MemoryMarshal.AsBytes(values.AsSpan()).CopyTo(mapping.Bytes);
            mapping.Flush(mapping.Range);
        }
        using BufferSrv featuresView = backend.CreateBufferSrv(device, new(features, new(0, 8), StructureStride: 4));
        ComputePipelineDesc userKernel = CompileDetection("ProjectNarrowPhase", """
            StructuredBuffer<uint> ProjectFeatures;
            struct ProjectNarrowPhase : IDeviceCollisionNarrowPhase
            {
                DeviceGeometryDistance distance(uint work, DeviceGeometryInput first, DeviceGeometryInput second)
                {
                    DeviceConvexCollision<DeviceSphere, DeviceSphere> inner;
                    DeviceGeometryDistance result = inner.distance(work, first, second);
                    result.firstFeature = int(ProjectFeatures[first.geometryIndex]);
                    result.secondFeature = int(ProjectFeatures[second.geometryIndex]);
                    return result;
                }
                uint manifold(uint work, DeviceGeometryInput first, DeviceGeometryInput second,
                    DeviceGeometryDistance distance, out DeviceGeometryDistance points[4])
                {
                    DeviceConvexCollision<DeviceSphere, DeviceSphere> inner;
                    uint count = inner.manifold(work, first, second, distance, points);
                    for (uint index = 0; index < count; index++)
                    {
                        points[index].firstFeature = int(ProjectFeatures[first.geometryIndex]);
                        points[index].secondFeature = int(ProjectFeatures[second.geometryIndex]);
                    }
                    return count;
                }
            };
            """);
        var inputAccesses = new List<JobResourceAccess>();
        var inputResources = new JobResourceAccessBuilder(jobs, inputAccesses);
        inputResources.Read(features, 0, 8);
        using var query = new DeviceCollisionDetection(backend, device, grid, geometry, userKernel, 0,
            new DeviceDetectionCapacity(4, 4, 1), new ConvexCollisionSettings(1e-5f, 32, 64), 0,
            DeviceInputTransfer.Resident, DeviceCollisionReadback.Contacts,
            new Dictionary<string, ResourceBinding[]> { ["ProjectFeatures"] = [ResourceBinding.ReadOnlyBuffer(featuresView)] },
            inputAccesses.ToArray());
        using var detected = new ContactPointBuffer(jobs, 4, 8);
        QueryHandle poses = world.Query(world.QueryDefinition().Read<BodyPose>());
        QueryHandle pointScope = world.Query(world.QueryDefinition().All<ContactPoint>());
        using var points = ContactPoints.Create(world, poses, pointScope, detected, new AverageContactMaterials(),
            new AvbdContactComponents(new AvbdContactState { NormalPenalty = 100, TangentPenalty = 100 }),
            new ContactRefreshSettings(0.01f, 0.99f, WarmStart: true));
        var context = new PhysicsStepContext(world, bodies, step.Number, 0, 1, step.StartTime, step.EndTime - step.StartTime);
        JobHandle queried = query.Schedule(in context, step.Completion);
        QueryHandle responseBodies = world.Query(world.QueryDefinition().ReadWrite<BodyPose>().Read<BodyMass>()
            .Read<BodyStepState>().Read<BodyVelocity>().ReadWrite<AvbdBodyEquation>());
        QueryHandle responsePoints = world.Query(world.QueryDefinition().Read<ContactPoint>()
            .Read<UndirectedRelationEndpoints<ContactPoint>>().Read<ContactMaterial>().ReadBuffer<ContactBodyLink>()
            .Optional<DirectedRelationEndpoints<ContactParticipant>>(QueryAccess.Read).ReadWrite<AvbdContactState>()
            .ReadWrite<ContactResponse>());
        var consume = query.WriteContacts(new ContactPointOutput(detected), new JobResourceAccess[] { detected.WriteAccess })
            .Then(points.Refresh())
            .Then(new InitializeContactResponses(world, responseBodies, responsePoints))
            .Then(SolveAvbd.Create(world, responseBodies, new AvbdConstraints<ContactPoint, AvbdContactMath>(responsePoints), 32, 0, 0))
            .Then(new UpdateBodyVelocities(64));

        consume.Schedule(in context, queried).Complete();

        QueryHandle results = world.Query(world.QueryDefinition().Read<ContactPoint>().Read<UndirectedRelationEndpoints<ContactPoint>>()
            .ReadBuffer<ContactBodyLink>());
        var contacts = new List<ContactPoint>();
        world.ExecuteReadQuery(results, rows =>
        {
            foreach (QueryRow row in rows.Rows)
            {
                UndirectedRelationEndpoints<ContactPoint> endpoints = row.Read<UndirectedRelationEndpoints<ContactPoint>>();
                Assert.Equal(first, endpoints.EndpointA);
                Assert.Equal(second, endpoints.EndpointB);
                Assert.Equal(2, row.ReadBuffer<ContactBodyLink>().Count);
                contacts.Add(row.Read<ContactPoint>());
            }
        });
        ContactPoint contact = Assert.Single(contacts);
        Assert.Equal(17, contact.FirstFeature);
        Assert.Equal(23, contact.SecondFeature);
        Assert.InRange(Vector3.Distance(Vector3.UnitX, contact.FirstLocalPoint), 0, 1e-5f);
        Assert.InRange(Vector3.Distance(-Vector3.UnitX, contact.SecondLocalPoint), 0, 1e-5f);
        Assert.InRange(Vector3.Distance(new Vector3(0.75f, 0, 0), contact.WorldPoint), 0, 1e-5f);
        Assert.InRange(contact.Separation, -0.50001f, -0.49999f);
        Assert.InRange(world.Read<BodyPose>(second).Position.X, 1.9309f, 1.9312f);
        Assert.InRange(world.Read<BodyVelocity>(second).Linear.X, 1.723f, 1.725f);
        Assert.Equal(7, query.Number);
        Assert.Equal(2.25, query.Time);
        world.ReleaseQuery(results);
        world.ReleaseQuery(responsePoints);
        world.ReleaseQuery(responseBodies);
        world.ReleaseQuery(pointScope);
        world.ReleaseQuery(poses);
        world.ReleaseQuery(members);
    }

    [Fact]
    public void OneDeviceWorkItemReusesItsScratchAcrossPairsBeforeAResidentResultConsumerCompletes()
    {
        using var backend = D3D12GraphicsBackend.Create();
        using Device device = CreateDevice(backend);
        using var jobs = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using World world = WorldJobs.CreateWorld(scheduler: jobs);
        using var grid = new DeviceCollisionGrid(backend, device, CompileGrid(),
            new DeviceGridSettings(1, 3), new DeviceGridCapacity(3, 256, 257, 8, 1));
        using var geometry = new DeviceCollisionGeometry(backend, device, new DeviceGeometryCapacity(3, 0, 0, 0));
        Entity first = CreateSphere(world, grid, geometry, 0, Vector3.Zero);
        Entity second = CreateSphere(world, grid, geometry, 1, new Vector3(1.5f, 0, 0));
        Entity third = CreateSphere(world, grid, geometry, 2, new Vector3(3, 0, 0));
        QueryDefinition bodies = world.QueryDefinition().All<SphereGeometry>().Build();
        QueryHandle members = world.Query(bodies);
        var pairs = world.Within(members);
        using var detection = new DeviceCollisionDetection(backend, device, grid, geometry,
            CompileDetection("DeviceConvexCollision<DeviceSphere, DeviceSphere>"), 0,
            new DeviceDetectionCapacity(Pairs: 8, FaceVertices: 4, WorkItems: 1),
            new ConvexCollisionSettings(1e-5f, 32, 64), 0, DeviceInputTransfer.Upload, DeviceCollisionReadback.None);
        using SomeEngine.Graphics.Buffer result = backend.CreateBuffer(device,
            new(8 + detection.Distances.Info.Size + detection.ContactCounts.Info.Size, BufferUsages.CopyDestination), MemoryType.Readback);
        using CommandContext commands = backend.CreateCommandContext(device, new(QueueType.Compute, 0, 1));
        var stages = grid.BeginInputs()
            .Then(grid.WriteGeometryInputs<SphereGeometry, DeviceSphereReader>(pairs, default, 0, 64))
            .Then(grid.Build(pairs, DevicePairRules.None, DeviceInputTransfer.Upload, DevicePairReadback.None))
            .Then(detection.Detect())
            .Then(new CopyDeviceCollisionResults(backend, device, grid, detection, commands, result));

        PhysicsSimulation.Create(world, bodies, stages, 1).Schedule(3, 1, 0.25).Complete();

        Assert.Empty(detection.GetReadbackAccesses(jobs));
        using MappedBuffer mapping = backend.Map(result, MapType.Read, BufferRange.Whole);
        mapping.Invalidate(mapping.Range);
        ReadOnlySpan<int> bounds = MemoryMarshal.Cast<byte, int>(mapping.Bytes[..8]);
        Assert.Equal(2, bounds[1] - bounds[0]);
        DeviceCollisionDistance[] distances = MemoryMarshal.Cast<byte, DeviceCollisionDistance>(mapping.Bytes.Slice(8, 128)).ToArray();
        Assert.Equal([first, second], distances.Select(value => value.First));
        Assert.Equal([second, third], distances.Select(value => value.Second));
        Assert.All(distances, value => Assert.Equal(-0.5f, value.Distance.Separation));
        Assert.Equal([1, 1], MemoryMarshal.Cast<byte, int>(mapping.Bytes.Slice(8 + (int)detection.Distances.Info.Size, 8)).ToArray());
        Assert.Equal(3, detection.Number);
        Assert.Equal(1.25, detection.Time);
        world.ReleaseQuery(members);
    }

    [Fact]
    public void DevicePlaneProductKeepsItsFirstPositionAndReversesTheConvexContactDirection()
    {
        using var backend = D3D12GraphicsBackend.Create();
        using Device device = CreateDevice(backend);
        using var jobs = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using World world = WorldJobs.CreateWorld(scheduler: jobs);
        using var grid = new DeviceCollisionGrid(backend, device, CompileGrid(),
            new DeviceGridSettings(1, 4), new DeviceGridCapacity(2, 256, 257, 4, 3));
        using var geometry = new DeviceCollisionGeometry(backend, device, new DeviceGeometryCapacity(2, 0, 0, 0));
        Entity box = world.CreateEntity(new BoxGeometry(Vector3.One));
        world.Add(box, new BodyPose { Position = new Vector3(0, 0.75f, 0), Rotation = Quaternion.Identity });
        world.Add(box, new DeviceCollisionBody(grid, 0));
        world.Add(box, new DeviceGeometryBinding(geometry, 0));
        Entity plane = world.CreateEntity(new PlaneGeometry(Vector3.UnitY, 0));
        world.Add(plane, new BodyPose { Rotation = Quaternion.Identity });
        world.Add(plane, new DeviceCollisionBody(grid, 1));
        world.Add(plane, new DeviceGeometryBinding(geometry, 1));
        QueryDefinition bodies = world.QueryDefinition().All<DeviceGeometryBinding>().Build();
        QueryHandle boxes = world.Query(world.QueryDefinition().All<BoxGeometry>());
        QueryHandle planes = world.Query(world.QueryDefinition().All<PlaneGeometry>());
        var pairs = world.Product(planes, boxes);
        using var detection = new DeviceCollisionDetection(backend, device, grid, geometry,
            CompileDetection("DeviceReverseCollision<DeviceConvexPlaneCollision<DeviceBox>>"), 1,
            new DeviceDetectionCapacity(4, 4, 1), new ConvexCollisionSettings(1e-5f, 32, 64), 0,
            DeviceInputTransfer.Upload, DeviceCollisionReadback.Distances | DeviceCollisionReadback.Contacts);
        var stages = grid.BeginInputs()
            .Then(grid.WriteGeometryInputs<BoxGeometry, DeviceBoxReader>(pairs, default, 0, 64))
            .Then(grid.WriteGeometryInputs<PlaneGeometry, DevicePlaneReader>(pairs,
                new(new CollisionBounds(new Vector3(-4), new Vector3(4))), 1, 64))
            .Then(grid.Build(pairs, DevicePairRules.None, DeviceInputTransfer.Upload, DevicePairReadback.None))
            .Then(detection.Detect());

        PhysicsSimulation.Create(world, bodies, stages, 1).Schedule(1, 0, 0.25).Complete();

        DeviceCollisionDistance distance = Assert.Single(detection.ReadDistances().ToArray());
        Assert.Equal(plane, distance.First);
        Assert.Equal(box, distance.Second);
        Assert.Equal(-0.25f, distance.Distance.Separation);
        CollisionContact[] contacts = detection.ReadContacts(0).ToArray();
        Assert.Equal(4, contacts.Length);
        Assert.All(contacts, contact =>
        {
            Assert.Equal(plane, contact.First);
            Assert.Equal(box, contact.Second);
            Assert.Equal(0, contact.FirstFeature);
            Assert.Equal(2, contact.SecondFeature);
            Assert.Equal(Vector3.UnitY, contact.Normal);
            Assert.Equal(0, contact.FirstLocalPoint.Y);
            Assert.Equal(-1, contact.SecondLocalPoint.Y);
            Assert.Equal(-0.125f, contact.WorldPoint.Y);
        });
        world.ReleaseQuery(planes);
        world.ReleaseQuery(boxes);
    }

    [Fact]
    public void DeviceCapsuleAndCylinderProduceTheTwoSideContactsFromTheirCurrentDimensions()
    {
        using var backend = D3D12GraphicsBackend.Create();
        using Device device = CreateDevice(backend);
        using var jobs = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using World world = WorldJobs.CreateWorld(scheduler: jobs);
        using var grid = new DeviceCollisionGrid(backend, device, CompileGrid(),
            new DeviceGridSettings(1, 4), new DeviceGridCapacity(2, 256, 257, 4, 3));
        using var geometry = new DeviceCollisionGeometry(backend, device, new DeviceGeometryCapacity(2, 0, 0, 0));
        Entity capsule = world.CreateEntity(new CapsuleGeometry(0.5f, 1));
        world.Add(capsule, new BodyPose { Rotation = Quaternion.Identity });
        world.Add(capsule, new DeviceCollisionBody(grid, 0));
        world.Add(capsule, new DeviceGeometryBinding(geometry, 0));
        Entity cylinder = world.CreateEntity(new CylinderGeometry(1, 1));
        world.Add(cylinder, new BodyPose { Position = new Vector3(1.25f, 0, 0), Rotation = Quaternion.Identity });
        world.Add(cylinder, new DeviceCollisionBody(grid, 1));
        world.Add(cylinder, new DeviceGeometryBinding(geometry, 1));
        QueryDefinition bodies = world.QueryDefinition().All<DeviceGeometryBinding>().Build();
        QueryHandle members = world.Query(bodies);
        var pairs = world.Within(members);
        using var detection = new DeviceCollisionDetection(backend, device, grid, geometry,
            CompileDetection("DeviceConvexCollision<DeviceCapsule, DeviceCylinder>"), 1,
            new DeviceDetectionCapacity(4, 4, 1), new ConvexCollisionSettings(1e-5f, 32, 64), 0,
            DeviceInputTransfer.Upload, DeviceCollisionReadback.Contacts);
        var stages = grid.BeginInputs()
            .Then(grid.WriteGeometryInputs<CapsuleGeometry, DeviceCapsuleReader>(pairs, default, 0, 64))
            .Then(grid.WriteGeometryInputs<CylinderGeometry, DeviceCylinderReader>(pairs, default, 1, 64))
            .Then(grid.Build(pairs, DevicePairRules.None, DeviceInputTransfer.Upload, DevicePairReadback.None))
            .Then(detection.Detect());

        PhysicsSimulation.Create(world, bodies, stages, 1).Schedule(1, 0, 0.25).Complete();

        Assert.Equal(1, detection.Count);
        CollisionContact[] contacts = detection.ReadContacts(0).ToArray();
        Assert.Equal(2, contacts.Length);
        Assert.All(contacts, contact =>
        {
            Assert.Equal(capsule, contact.First);
            Assert.Equal(cylinder, contact.Second);
            Assert.Equal(2, contact.FirstFeature);
            Assert.Equal(0, contact.SecondFeature);
            Assert.Equal(Vector3.UnitX, contact.Normal);
            Assert.Equal(-0.25f, contact.Separation);
            Assert.Equal(0.5f, contact.FirstLocalPoint.X);
            Assert.Equal(-1, contact.SecondLocalPoint.X);
            Assert.Equal(0.375f, contact.WorldPoint.X);
        });
        Assert.Equal([-1f, 1], contacts.Select(contact => contact.WorldPoint.Y).Order());
        world.ReleaseQuery(members);
    }

    [Fact]
    public void DeviceHullContactsReadSharedArraysAfterTheInputReferenceReplacementCompletes()
    {
        using var backend = D3D12GraphicsBackend.Create();
        using Device device = CreateDevice(backend);
        using var jobs = new Scheduler(new JobRuntimeConfig { WorkerCount = 0 });
        using World world = WorldJobs.CreateWorld(scheduler: jobs);
        using var grid = new DeviceCollisionGrid(backend, device, CompileGrid(),
            new DeviceGridSettings(1, 3), new DeviceGridCapacity(2, 256, 257, 4, 1));
        using var geometry = new DeviceCollisionGeometry(backend, device, new DeviceGeometryCapacity(2, 8, 6, 24));
        var original = new ConvexHullGeometry(Cube(0.5f), 0);
        Entity first = world.CreateEntity(original);
        world.Add(first, new BodyPose { Rotation = Quaternion.Identity });
        world.Add(first, new DeviceCollisionBody(grid, 0));
        world.Add(first, new DeviceGeometryBinding(geometry, 0));
        Entity second = world.CreateEntity(original);
        world.Add(second, new BodyPose { Position = new Vector3(1.75f, 0, 0), Rotation = Quaternion.Identity });
        world.Add(second, new DeviceCollisionBody(grid, 1));
        world.Add(second, new DeviceGeometryBinding(geometry, 1));
        QueryDefinition bodies = world.QueryDefinition().All<ConvexHullGeometry>().Build();
        QueryHandle members = world.Query(bodies);
        var pairs = world.Within(members);
        using var detection = new DeviceCollisionDetection(backend, device, grid, geometry,
            CompileDetection("DeviceConvexCollision<DeviceHull, DeviceHull>", "#include \"physics_collision_hull.slang\""), 0,
            new DeviceDetectionCapacity(4, 4, 1), new ConvexCollisionSettings(1e-5f, 32, 64), 0,
            DeviceInputTransfer.Upload, DeviceCollisionReadback.Distances | DeviceCollisionReadback.Contacts);
        var stages = grid.BeginInputs()
            .Then(grid.WriteGeometryInputs<ConvexHullGeometry, DeviceHullReader>(pairs, default, 0, 64))
            .Then(grid.Build(pairs, DevicePairRules.None, DeviceInputTransfer.Upload, DevicePairReadback.None))
            .Then(detection.Detect());
        var inputReady = new TaskCompletionSource();
        JobHandle input = jobs.CreateExternalFenceHandle(inputReady.Task);
        JobHandle replaced = new ReplaceDeviceHullSource(new(Cube(1), 0)).Schedule(world, input);
        PhysicsStepResult step = PhysicsSimulation.Create(world, bodies, stages, 1).Schedule(9, 2, 0.25, replaced);

        inputReady.SetResult();
        step.Complete();

        DeviceCollisionDistance distance = Assert.Single(detection.ReadDistances().ToArray());
        Assert.Equal(-0.25f, distance.Distance.Separation);
        CollisionContact[] contacts = detection.ReadContacts(0).ToArray();
        Assert.Equal(4, contacts.Length);
        Assert.All(contacts, contact =>
        {
            Assert.Equal(first, contact.First);
            Assert.Equal(second, contact.Second);
            Assert.Equal(1, contact.FirstFeature);
            Assert.Equal(0, contact.SecondFeature);
            Assert.Equal(Vector3.UnitX, contact.Normal);
            Assert.Equal(1, contact.FirstLocalPoint.X);
            Assert.Equal(-1, contact.SecondLocalPoint.X);
            Assert.Equal(0.875f, contact.WorldPoint.X);
        });
        Assert.Equal(9, detection.Number);
        Assert.Equal(2.25, detection.Time);
        world.ReleaseQuery(members);
    }

    [Fact]
    public void DeviceBoxPenetrationProducesTheFourFaceContactsWithCommonFeaturesAndAnchors()
    {
        using var backend = D3D12GraphicsBackend.Create();
        using Device device = CreateDevice(backend);
        using var jobs = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using World world = WorldJobs.CreateWorld(scheduler: jobs);
        using var grid = new DeviceCollisionGrid(backend, device, CompileGrid(),
            new DeviceGridSettings(1, 3), new DeviceGridCapacity(2, 256, 257, 4, 1));
        using var geometry = new DeviceCollisionGeometry(backend, device, new DeviceGeometryCapacity(2, 0, 0, 0));
        Entity first = world.CreateEntity(new BoxGeometry(Vector3.One));
        world.Add(first, new BodyPose { Rotation = Quaternion.Identity });
        world.Add(first, new DeviceCollisionBody(grid, 0));
        world.Add(first, new DeviceGeometryBinding(geometry, 0));
        Entity second = world.CreateEntity(new BoxGeometry(Vector3.One));
        world.Add(second, new BodyPose { Position = new Vector3(1.75f, 0, 0), Rotation = Quaternion.Identity });
        world.Add(second, new DeviceCollisionBody(grid, 1));
        world.Add(second, new DeviceGeometryBinding(geometry, 1));
        QueryDefinition bodies = world.QueryDefinition().All<BoxGeometry>().Build();
        QueryHandle members = world.Query(bodies);
        var pairs = world.Within(members);
        using var detection = new DeviceCollisionDetection(backend, device, grid, geometry,
            CompileDetection("DeviceConvexCollision<DeviceBox, DeviceBox>"), 0,
            new DeviceDetectionCapacity(4, 4, 1), new ConvexCollisionSettings(1e-5f, 32, 64), 0,
            DeviceInputTransfer.Upload, DeviceCollisionReadback.Distances | DeviceCollisionReadback.Contacts);
        var stages = grid.BeginInputs()
            .Then(grid.WriteGeometryInputs<BoxGeometry, DeviceBoxReader>(pairs, default, 0, 64))
            .Then(grid.Build(pairs, DevicePairRules.None, DeviceInputTransfer.Upload, DevicePairReadback.None))
            .Then(detection.Detect());

        PhysicsSimulation.Create(world, bodies, stages, 1).Schedule(1, 0, 0.25).Complete();

        DeviceCollisionDistance distance = Assert.Single(detection.ReadDistances().ToArray());
        Assert.InRange(distance.Distance.Separation, -0.25001f, -0.24999f);
        CollisionContact[] points = detection.ReadContacts(0).ToArray();
        Assert.Equal(4, points.Length);
        Assert.All(points, point =>
        {
            Assert.Equal(first, point.First);
            Assert.Equal(second, point.Second);
            Assert.Equal(1, point.FirstFeature);
            Assert.Equal(0, point.SecondFeature);
            Assert.Equal(1, point.FirstBodyCount);
            Assert.Equal(Vector3.UnitX, point.Normal);
            Assert.Equal(-0.25f, point.Separation);
            Assert.Equal(1, point.FirstLocalPoint.X);
            Assert.Equal(-1, point.SecondLocalPoint.X);
            Assert.Equal(0.875f, point.WorldPoint.X);
            Assert.False(point.Prediction.Enabled);
            Assert.Equal(0, point.Prediction.SampleTime);
        });
        Assert.Equal([new Vector3(1, -1, -1), new Vector3(1, -1, 1), new Vector3(1, 1, -1), new Vector3(1, 1, 1)],
            points.Select(point => point.FirstLocalPoint).OrderBy(point => point.Y).ThenBy(point => point.Z));
        world.ReleaseQuery(members);
    }

    [Fact]
    public void MixedDeviceTypeBinKeepsTheOriginalPairOrderAndTheNearestRotatedBoxWitnesses()
    {
        using var backend = D3D12GraphicsBackend.Create();
        using Device device = CreateDevice(backend);
        using var jobs = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using World world = WorldJobs.CreateWorld(scheduler: jobs);
        using var grid = new DeviceCollisionGrid(backend, device, CompileGrid(),
            new DeviceGridSettings(1, 3), new DeviceGridCapacity(2, 256, 257, 4, 3));
        using var geometry = new DeviceCollisionGeometry(backend, device, new DeviceGeometryCapacity(2, 0, 0, 0));
        Entity sphere = CreateSphere(world, grid, geometry, 1, new Vector3(1.2f, 1.2f, 0));
        world.Replace(sphere, new SphereGeometry(0.5f));
        Entity box = world.CreateEntity(new BoxGeometry(Vector3.One));
        world.Add(box, new BodyPose { Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 4) });
        world.Add(box, new DeviceCollisionBody(grid, 0));
        world.Add(box, new DeviceGeometryBinding(geometry, 0));
        QueryDefinition bodies = world.QueryDefinition().All<DeviceGeometryBinding>().Build();
        QueryHandle members = world.Query(bodies);
        var pairs = world.Within(members);
        using var detection = new DeviceCollisionDetection(backend, device, grid, geometry,
            CompileDetection("DeviceTypePairCollision<DeviceConvexCollision<DeviceBox, DeviceSphere>>", initialize: "kernel.firstType = 1;"), bin: 1,
            new DeviceDetectionCapacity(4, 4, 1), new ConvexCollisionSettings(1e-5f, 32, 64), 0,
            DeviceInputTransfer.Upload, DeviceCollisionReadback.Distances | DeviceCollisionReadback.Contacts);
        var stages = grid.BeginInputs()
            .Then(grid.WriteGeometryInputs<BoxGeometry, DeviceBoxReader>(pairs, default, 1, 64))
            .Then(grid.WriteGeometryInputs<SphereGeometry, DeviceSphereReader>(pairs, default, 0, 64))
            .Then(grid.Build(pairs, DevicePairRules.None, DeviceInputTransfer.Upload, DevicePairReadback.None))
            .Then(detection.Detect());

        var simulation = PhysicsSimulation.Create(world, bodies, stages, 1);
        simulation.Schedule(1, 0, 0.25).Complete();

        DeviceCollisionDistance result = Assert.Single(detection.ReadDistances().ToArray());
        Assert.Equal(sphere, result.First);
        Assert.Equal(box, result.Second);
        Assert.InRange(result.Distance.Separation, 0.19704f, 0.19708f);
        Assert.InRange(Vector3.Distance(new Vector3(0.8464466f, 0.8464466f, 0), result.Distance.FirstPoint), 0, 1e-4f);
        Assert.InRange(Vector3.Distance(new Vector3(0.7071068f, 0.7071068f, 0), result.Distance.SecondPoint), 0, 1e-4f);
        Assert.InRange(Vector3.Distance(new Vector3(-0.7071068f, -0.7071068f, 0), result.Distance.Normal), 0, 1e-4f);
        Assert.Empty(detection.ReadContacts(0).ToArray());

        world.Replace(sphere, new BodyPose { Position = new Vector3(0.9f, 0.9f, 0), Rotation = Quaternion.Identity });
        simulation.Schedule(2, 0.25, 0.25).Complete();

        CollisionContact contact = Assert.Single(detection.ReadContacts(0).ToArray());
        Assert.Equal(sphere, contact.First);
        Assert.Equal(box, contact.Second);
        Assert.Equal(0, contact.FirstFeature);
        Assert.Equal(1, contact.SecondFeature);
        Assert.InRange(contact.Separation, -0.22723f, -0.22718f);
        Assert.InRange(Vector3.Distance(new Vector3(-0.3535534f, -0.3535534f, 0), contact.FirstLocalPoint), 0, 1e-4f);
        Assert.InRange(Vector3.Distance(Vector3.UnitX, contact.SecondLocalPoint), 0, 1e-4f);
        Assert.InRange(Vector3.Distance(new Vector3(-0.7071068f, -0.7071068f, 0), contact.Normal), 0, 1e-4f);
        world.ReleaseQuery(members);
    }

    [Fact]
    public void DeviceSphereQueryReturnsEntityWitnessesAndSignedDistanceFromTheCurrentComponents()
    {
        using var backend = D3D12GraphicsBackend.Create();
        using Device device = CreateDevice(backend);
        using var jobs = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using World world = WorldJobs.CreateWorld(scheduler: jobs);
        using var grid = new DeviceCollisionGrid(backend, device, CompileGrid(),
            new DeviceGridSettings(1, 3), new DeviceGridCapacity(2, 256, 257, 4, 1));
        using var geometry = new DeviceCollisionGeometry(backend, device, new DeviceGeometryCapacity(2, 0, 0, 0));
        Entity first = CreateSphere(world, grid, geometry, 0, Vector3.Zero);
        Entity second = CreateSphere(world, grid, geometry, 1, new Vector3(5, 0, 0));
        QueryDefinition bodies = world.QueryDefinition().All<SphereGeometry>().Build();
        QueryHandle members = world.Query(bodies);
        var pairs = world.Within(members);
        using var detection = new DeviceCollisionDetection(backend, device, grid, geometry,
            CompileDetection("DeviceConvexCollision<DeviceSphere, DeviceSphere>"), bin: 0,
            new DeviceDetectionCapacity(4, 4, 1), new ConvexCollisionSettings(1e-5f, 32, 64), contactDistance: 0,
            DeviceInputTransfer.Upload, DeviceCollisionReadback.Distances);
        var stages = grid.BeginInputs()
            .Then(grid.WriteGeometryInputs<SphereGeometry, DeviceSphereReader>(pairs, default, geometryType: 0, rowsPerPacket: 64))
            .Then(grid.Build(pairs, DevicePairRules.None, DeviceInputTransfer.Upload, DevicePairReadback.None))
            .Then(detection.Detect());
        var simulation = PhysicsSimulation.Create(world, bodies, stages, 1);
        simulation.Schedule(6, 1.75, 0.25).Complete();
        Assert.Empty(detection.ReadDistances().ToArray());
        Assert.Equal(6, detection.Number); Assert.Equal(2, detection.Time);
        world.Replace(second, new BodyPose { Position = new(1.5f, 0, 0), Rotation = Quaternion.Identity });
        PhysicsStepResult step = simulation.Schedule(7, 2, 0.25);

        step.Complete();

        DeviceCollisionDistance result = Assert.Single(detection.ReadDistances().ToArray());
        Assert.Equal(first, result.First);
        Assert.Equal(second, result.Second);
        Assert.InRange(Vector3.Distance(Vector3.UnitX, result.Distance.Normal), 0, 1e-5f);
        Assert.InRange(Vector3.Distance(Vector3.UnitX, result.Distance.FirstPoint), 0, 1e-5f);
        Assert.InRange(Vector3.Distance(new Vector3(0.5f, 0, 0), result.Distance.SecondPoint), 0, 1e-5f);
        Assert.InRange(result.Distance.Separation, -0.50001f, -0.49999f);
        Assert.Equal(0, result.Distance.FirstFeature);
        Assert.Equal(0, result.Distance.SecondFeature);
        Assert.Equal(7, detection.Number);
        Assert.Equal(2.25, detection.Time);
        Assert.True(backend.IsComplete(detection.Completion));
        world.Replace(second, new BodyPose { Position = new(5, 0, 0), Rotation = Quaternion.Identity });
        simulation.Schedule(8, 2.25, 0.25).Complete();
        Assert.Empty(detection.ReadDistances().ToArray());
        Assert.Equal(8, detection.Number); Assert.Equal(2.5, detection.Time);
        Assert.True(backend.IsComplete(detection.Completion));
        world.ReleaseQuery(members);
    }

    private static Entity CreateSphere(World world, DeviceCollisionGrid grid, DeviceCollisionGeometry geometry,
        int index, Vector3 position)
    {
        Entity entity = world.CreateEntity(new SphereGeometry(1));
        world.Add(entity, new BodyPose { Position = position, Rotation = Quaternion.Identity });
        world.Add(entity, new DeviceCollisionBody(grid, index));
        world.Add(entity, new DeviceGeometryBinding(geometry, index));
        return entity;
    }

    private DeviceGridKernels CompileGrid()
    {
        D3D12TestEntry[] entries = [new("ClearDeviceGrid", SlangStage.Compute), new("BuildDeviceGrid", SlangStage.Compute),
            new("CountDevicePairs", SlangStage.Compute), new("ScanDevicePairCounts", SlangStage.Compute),
            new("OffsetDevicePairBlocks", SlangStage.Compute), new("OffsetDevicePairBins", SlangStage.Compute),
            new("WriteDevicePairs", SlangStage.Compute)];
        D3D12TestShader shader = shaderCompiler.CompileWithSearchPaths("device_detection_grid",
            "#include \"physics_grid.slang\"", entries, "sm_6_0", ShaderDirectory());
        ComputePipelineDesc Entry(int index) => new(shader.SlangCode, shader.Reflection,
            shader.GetEntryPoint(index), shader.GetEntryCode(index));
        return new(Entry(0), Entry(1), Entry(2), Entry(3), Entry(4), Entry(5), Entry(6));
    }

    private ComputePipelineDesc CompileDetection(string kernel, string declarations = "", string initialize = "")
    {
        string source = $$"""
            #include "physics_collision.slang"
            {{declarations}}
            [shader("compute")]
            [numthreads(64, 1, 1)]
            void Detect(uint3 id : SV_DispatchThreadID)
            {
                {{kernel}} kernel;
                {{initialize}}
                detectDeviceCollision(id.x, kernel);
            }
            """;
        D3D12TestShader shader = shaderCompiler.CompileWithSearchPaths("device_detection", source,
            [new("Detect", SlangStage.Compute)], "sm_6_0", ShaderDirectory());
        return new(shader.SlangCode, shader.Reflection, shader.GetEntryPoint(0), shader.GetEntryCode(0));
    }

    private static string ShaderDirectory()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (Directory.Exists(Path.Combine(directory.FullName, "assets", "Shaders")))
                return Path.Combine(directory.FullName, "assets", "Shaders");
        throw new DirectoryNotFoundException();
    }

    private static Device CreateDevice(IGraphicsBackend backend)
    {
        var options = new AdapterEnumerationOptions(AdapterPreference.HighPerformance, IncludeSoftware: false);
        backend.TryEnumerateAdapters(options, [], out int count);
        var adapters = new AdapterInfo[count];
        backend.TryEnumerateAdapters(options, adapters, out _);
        AdapterInfo adapter = adapters.First(value => value.HardwareAccelerated);
        return backend.CreateDevice(new DeviceDesc(adapter.Id, [new(QueueType.Compute)], label: "Device geometry behavior"));
    }

    private static ConvexHullData Cube(float half)
    {
        Vector3[] vertices = [new(-half, -half, -half), new(half, -half, -half), new(half, half, -half), new(-half, half, -half),
            new(-half, -half, half), new(half, -half, half), new(half, half, half), new(-half, half, half)];
        HullFace[] faces = [new(-Vector3.UnitX, 0, 4), new(Vector3.UnitX, 4, 4), new(-Vector3.UnitY, 8, 4),
            new(Vector3.UnitY, 12, 4), new(-Vector3.UnitZ, 16, 4), new(Vector3.UnitZ, 20, 4)];
        int[] indices = [0, 4, 7, 3, 1, 2, 6, 5, 0, 1, 5, 4, 3, 7, 6, 2, 0, 3, 2, 1, 4, 5, 6, 7];
        return new(vertices, faces, indices);
    }

    private readonly struct CopyDeviceCollisionResults(IGraphicsBackend backend, Device device, DeviceCollisionGrid grid,
        DeviceCollisionDetection detection, CommandContext commands, SomeEngine.Graphics.Buffer result) : IPhysicsStage
    {
        public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency)
        {
            WorldJobs.TryGetScheduler(context.World, out Scheduler? scheduler);
            var accesses = new List<JobResourceAccess>();
            var resources = new JobResourceAccessBuilder(scheduler!, accesses);
            resources.Read(grid.BinOffsets, 0, 8);
            resources.Read(detection.Distances, 0, (long)detection.Distances.Info.Size);
            resources.Read(detection.ContactCounts, 0, (long)detection.ContactCounts.Info.Size);
            resources.Write(result, 0, (long)result.Info.Size);
            resources.Write(commands);
            return scheduler!.Schedule(new CopyResults(backend, device, grid, detection, commands, result),
                CollectionsMarshal.AsSpan(accesses), dependency);
        }
    }

    private readonly struct CopyResults(IGraphicsBackend backend, Device device, DeviceCollisionGrid grid,
        DeviceCollisionDetection detection, CommandContext commands, SomeEngine.Graphics.Buffer result) : IJob
    {
        public void Execute()
        {
            backend.Begin(commands, new(0, 0, 16));
            Copy(grid.BinOffsets, 0, 8);
            Copy(detection.Distances, 8, detection.Distances.Info.Size);
            Copy(detection.ContactCounts, 8 + detection.Distances.Info.Size, detection.ContactCounts.Info.Size);
            using RecordedCommands recorded = backend.End(commands);
            QueueCompletion completion = backend.Submit(backend.GetQueue(device, QueueType.Compute), new([], [], [recorded], [], []));
            Assert.Equal(WaitStatus.Completed, backend.WaitCpu(completion, TimeSpan.FromSeconds(60)));
            backend.CollectCompleted(device);
        }

        private void Copy(SomeEngine.Graphics.Buffer source, ulong offset, ulong bytes)
        {
            backend.Barrier(commands, new BufferBarrier(source, PipelineSync.ComputeShading, PipelineSync.Copy,
                ResourceAccess.UnorderedAccess, ResourceAccess.CopySource));
            backend.CopyBuffer(commands, new(source, 0, result, offset, bytes));
            backend.Barrier(commands, new BufferBarrier(source, PipelineSync.Copy, PipelineSync.ComputeShading,
                ResourceAccess.CopySource, ResourceAccess.UnorderedAccess));
        }
    }
}

public readonly struct DeviceHullReferenceResources : IJobComponentResources<ConvexHullGeometry>
{
    public static void Collect(in ConvexHullGeometry component, ref JobResourceAccessBuilder resources) { }
}

public readonly partial struct ReplaceDeviceHullSource(ConvexHullGeometry source) : IJobEntity
{
    public void Execute([JobResources<DeviceHullReferenceResources>] ref ConvexHullGeometry geometry) => geometry = source;
}
