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
public sealed partial class DeviceReferencedGeometryTests(D3D12TestCompiler shaderCompiler)
{
    [Fact]
    public void DeviceFaceDeformationFeedsCurrentBoundsAndThreeOriginalBodyWeights()
    {
        using var backend = D3D12GraphicsBackend.Create();
        using Device device = CreateDevice(backend);
        using var jobs = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using World world = WorldJobs.CreateWorld(scheduler: jobs);
        using var bodies = new DeviceBodies(backend, device, 4);
        using var grid = new DeviceCollisionGrid(backend, device, CompileGrid(), new(1, 4), new(2, 256, 257, 4, 3));
        using var geometry = new DeviceCollisionGeometry(backend, device, new(2, 0, 0, 0), bodies);
        Entity first = AddBody(world, bodies, 0, Vector3.Zero);
        Entity second = AddBody(world, bodies, 1, new Vector3(2, 0, 0));
        Entity third = AddBody(world, bodies, 2, new Vector3(0, 0, 2));
        Entity face = world.CreateEntity(new DynamicFace(new(first, Vector3.Zero), new(second, Vector3.Zero), new(third, Vector3.Zero), 0));
        world.Add(face, new DeviceCollisionBody(grid, 0));
        world.Add(face, new DeviceGeometryBinding(geometry, 0));
        Entity sphere = AddBody(world, bodies, 3, new Vector3(.5f, 1.9f, .5f));
        world.Add(sphere, new SphereGeometry(.25f));
        world.Add(sphere, new DeviceCollisionBody(grid, 1));
        world.Add(sphere, new DeviceGeometryBinding(geometry, 1));
        QueryHandle faces = world.Query(world.QueryDefinition().All<DynamicFace>());
        QueryHandle spheres = world.Query(world.QueryDefinition().All<SphereGeometry>());
        var pairs = world.Between(faces, spheres);
        using var faceInputs = grid.WriteGeometryReferences<DynamicFace, DeviceDynamicFaceSource>(pairs, default, 0, 64);
        using var sphereInputs = grid.WriteGeometryReferences<SphereGeometry, DeviceComponentSource<SphereGeometry, DeviceSphereReader>>(pairs, default, 1, 64);
        using var move = new MoveDeviceBodies(backend, device, bodies, Compile("move_face", """
            #include "physics_bodies.slang"
            [shader("compute")][numthreads(64,1,1)]
            void Run(uint3 id : SV_DispatchThreadID) {
                if (id.x < 3) { DeviceBodyState body = DeviceBodyStates[id.x]; body.position.y = 2; DeviceBodyStates[id.x] = body; }
            }
            """));
        using var update = new UpdateDeviceGeometry(backend, device, grid, geometry,
            CompileUpdate("DeviceDynamicFaceUpdate"), 0, DeviceInputTransfer.Upload);
        using var sphereUpdate = new UpdateDeviceGeometry(backend, device, grid, geometry,
            CompileUpdate("DeviceSphereUpdate"), 1, DeviceInputTransfer.Resident);
        using var detection = new DeviceCollisionDetection(backend, device, grid, geometry,
            CompileDetection("DeviceConvexCollision<DeviceTriangle, DeviceSphere>"), 1, new(4, 4, 1),
            new(1e-5f, 32, 64), 0, DeviceInputTransfer.Resident, DeviceCollisionReadback.Contacts);
        var captured = new List<Captured>();
        var stages = bodies.WriteInputs(64).Then(bodies.UploadInputs()).Then(move)
            .Then(grid.BeginInputs()).Then(faceInputs).Then(sphereInputs).Then(update.Update()).Then(sphereUpdate.Update())
            .Then(grid.Build(pairs, DevicePairRules.None, DeviceInputTransfer.Resident, DevicePairReadback.None))
            .Then(detection.Detect()).Then(detection.WriteContacts(new Capture(captured),
                new[] { JobResourceAccess.Write(jobs.GetResourceToken(captured)) }));

        PhysicsSimulation.Create(world, QueryDefinition.Empty, stages, 1).Schedule(2, 1, .25).Complete();

        Captured contact = Assert.Single(captured);
        Assert.Equal(face, contact.Contact.First);
        Assert.Equal(sphere, contact.Contact.Second);
        Near(-.15f, contact.Contact.Separation);
        Near(-Vector3.UnitY, contact.Contact.Normal);
        Near(new Vector3(.5f, 0, .5f), contact.Contact.FirstLocalPoint);
        Assert.Equal(new ContactBody(first, Vector3.Zero, .5f), contact.First[0]);
        Assert.Equal(new ContactBody(second, Vector3.Zero, .25f), contact.First[1]);
        Assert.Equal(new ContactBody(third, Vector3.Zero, .25f), contact.First[2]);
        world.ReleaseQuery(faces);
        world.ReleaseQuery(spheres);
    }

    [Fact]
    public void DeviceCapsuleBoundsAndContactsReadTheReferencedBodyStatesOutsideGeometryMembers()
    {
        using var backend = D3D12GraphicsBackend.Create();
        using Device device = CreateDevice(backend);
        using var jobs = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using World world = WorldJobs.CreateWorld(scheduler: jobs);
        using var bodies = new DeviceBodies(backend, device, capacity: 3);
        using var grid = new DeviceCollisionGrid(backend, device, CompileGrid(), new(1, 4), new(2, 256, 257, 4, 3));
        using var geometry = new DeviceCollisionGeometry(backend, device, new(2, 0, 0, 0), bodies);
        Entity first = AddBody(world, bodies, 0, new Vector3(0, -1, 0));
        Entity second = AddBody(world, bodies, 1, new Vector3(0, 1, 0));
        Entity capsule = world.CreateEntity(new DynamicCapsule(new(first, Vector3.Zero), new(second, Vector3.Zero), .25f));
        world.Add(capsule, new DeviceCollisionBody(grid, 0));
        world.Add(capsule, new DeviceGeometryBinding(geometry, 0));
        Entity sphere = AddBody(world, bodies, 2, new Vector3(.4f, .5f, 0));
        world.Add(sphere, new SphereGeometry(.25f));
        world.Add(sphere, new DeviceCollisionBody(grid, 1));
        world.Add(sphere, new DeviceGeometryBinding(geometry, 1));
        QueryHandle capsules = world.Query(world.QueryDefinition().All<DynamicCapsule>());
        QueryHandle spheres = world.Query(world.QueryDefinition().All<SphereGeometry>());
        var pairs = world.Between(capsules, spheres);
        using var capsuleInputs = grid.WriteGeometryReferences<DynamicCapsule, DeviceDynamicCapsuleSource>(pairs, default, 0, 64);
        using var sphereInputs = grid.WriteGeometryReferences<SphereGeometry, DeviceComponentSource<SphereGeometry, DeviceSphereReader>>(
            pairs, default, 1, 64);
        using var update = new UpdateDeviceGeometry(backend, device, grid, geometry,
            CompileUpdate("DeviceDynamicCapsuleUpdate"), 0, DeviceInputTransfer.Upload);
        using var sphereUpdate = new UpdateDeviceGeometry(backend, device, grid, geometry,
            CompileUpdate("DeviceSphereUpdate"), 1, DeviceInputTransfer.Resident);
        using var detection = new DeviceCollisionDetection(backend, device, grid, geometry,
            CompileDetection("DeviceConvexCollision<DeviceCapsule, DeviceSphere>"), 1, new(4, 4, 1),
            new(1e-5f, 32, 64), 0, DeviceInputTransfer.Resident, DeviceCollisionReadback.Contacts | DeviceCollisionReadback.Distances);
        var captured = new List<Captured>();
        JobResourceAccess output = JobResourceAccess.Write(jobs.GetResourceToken(captured));
        var stages = bodies.WriteInputs(64).Then(bodies.UploadInputs())
            .Then(grid.BeginInputs()).Then(capsuleInputs).Then(sphereInputs).Then(update.Update()).Then(sphereUpdate.Update())
            .Then(grid.Build(pairs, DevicePairRules.None, DeviceInputTransfer.Resident, DevicePairReadback.None))
            .Then(detection.Detect()).Then(detection.WriteContacts(new Capture(captured), new[] { output }));

        PhysicsSimulation.Create(world, QueryDefinition.Empty, stages, 1).Schedule(4, 1, .25).Complete();

        Captured contact = Assert.Single(captured);
        Assert.Equal(capsule, contact.Contact.First);
        Assert.Equal(sphere, contact.Contact.Second);
        Near(-.1f, contact.Contact.Separation);
        Near(Vector3.UnitX, contact.Contact.Normal);
        Assert.Equal(new ContactBody(first, Vector3.Zero, .25f), contact.First[0]);
        Assert.Equal(new ContactBody(second, Vector3.Zero, .75f), contact.First[1]);
        Assert.Equal(sphere, Assert.Single(contact.Second).Body);
        Near(-Vector3.UnitX * .25f, contact.Second[0].LocalPoint);
        Assert.Equal(4, detection.Number);
        Assert.Equal(1.25, detection.Time);
        world.ReleaseQuery(capsules);
        world.ReleaseQuery(spheres);
    }

    private static Entity AddBody(World world, DeviceBodies bodies, int index, Vector3 position)
    {
        Entity entity = world.CreateEntity(new BodyPose { Position = position, Rotation = Quaternion.Identity });
        world.Add(entity, new BodyMass { Motion = BodyMotion.Dynamic, InverseMass = 1, InverseInertia = Matrix4x4.Identity });
        world.Add(entity, new BodyVelocity());
        world.Add(entity, new BodyStepState());
        world.Add(entity, new DeviceBodyBinding(bodies, index));
        return entity;
    }

    private DeviceGridKernels CompileGrid()
    {
        D3D12TestEntry[] entries = [new("ClearDeviceGrid", SlangStage.Compute), new("BuildDeviceGrid", SlangStage.Compute),
            new("CountDevicePairs", SlangStage.Compute), new("ScanDevicePairCounts", SlangStage.Compute),
            new("OffsetDevicePairBlocks", SlangStage.Compute), new("OffsetDevicePairBins", SlangStage.Compute),
            new("WriteDevicePairs", SlangStage.Compute)];
        D3D12TestShader shader = shaderCompiler.CompileWithSearchPaths("referenced_grid", "#include \"physics_grid.slang\"",
            entries, "sm_6_0", ShaderDirectory());
        ComputePipelineDesc Entry(int index) => new(shader.SlangCode, shader.Reflection, shader.GetEntryPoint(index), shader.GetEntryCode(index));
        return new(Entry(0), Entry(1), Entry(2), Entry(3), Entry(4), Entry(5), Entry(6));
    }

    private ComputePipelineDesc CompileUpdate(string type) => Compile("referenced_update", $$"""
        #include "physics_geometry_update.slang"
        [shader("compute")][numthreads(64,1,1)]
        void Run(uint3 id : SV_DispatchThreadID) { updateDeviceGeometry<{{type}}>(id.x); }
        """);

    private ComputePipelineDesc CompileDetection(string type) => Compile("referenced_detection", $$"""
        #include "physics_geometry_references.slang"
        [shader("compute")][numthreads(64,1,1)]
        void Run(uint3 id : SV_DispatchThreadID) {
            {{type}} kernel;
            detectDeviceReferencedCollision(id.x, kernel);
        }
        """);

    private ComputePipelineDesc Compile(string name, string source)
    {
        D3D12TestShader shader = shaderCompiler.CompileWithSearchPaths(name, source,
            [new("Run", SlangStage.Compute)], "sm_6_0", ShaderDirectory());
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
        return backend.CreateDevice(new(adapters.First(value => value.HardwareAccelerated).Id,
            [new(QueueType.Compute)], label: "Referenced geometry behavior"));
    }

    private static void Near(float expected, float actual) => Assert.InRange(actual, expected - 1e-5f, expected + 1e-5f);
    private static void Near(Vector3 expected, Vector3 actual) => Assert.InRange(Vector3.Distance(expected, actual), 0, 1e-5f);
    private sealed record Captured(CollisionContact Contact, ContactBody[] First, ContactBody[] Second);
    private readonly struct Capture(List<Captured> values) : ICollisionContactOutput
    {
        public void Add(in CollisionContact contact, ReadOnlySpan<ContactBody> first, ReadOnlySpan<ContactBody> second) =>
            values.Add(new(contact, first.ToArray(), second.ToArray()));
    }
    private readonly struct MoveDeviceBodies : IPhysicsStage, IDisposable
    {
        private readonly Movement _movement;
        public MoveDeviceBodies(IGraphicsBackend backend, Device device, DeviceBodies bodies, ComputePipelineDesc kernel) =>
            _movement = new(backend, device, bodies, kernel);
        public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency)
        {
            Scheduler scheduler = WorldJobs.GetScheduler(context.World);
            return scheduler.Schedule(new Move(_movement),
                new[] { _movement.Bodies.GetWriteAccess(scheduler), JobResourceAccess.Write(scheduler.GetResourceToken(_movement.Commands)) }, dependency);
        }
        public void Dispose() => _movement.Dispose();
        private readonly struct Move(Movement movement) : IJob { public void Execute() => movement.Execute(); }
    }

    private sealed class Movement : IJob, IDisposable
    {
        private readonly IGraphicsBackend _backend;
        private readonly Device _device;
        private readonly Pipeline _pipeline;
        private readonly VariableLayoutReflection _layout;
        private readonly BufferUav _view;
        public DeviceBodies Bodies { get; }
        public CommandContext Commands { get; }
        public Movement(IGraphicsBackend backend, Device device, DeviceBodies bodies, ComputePipelineDesc kernel)
        {
            _backend = backend; _device = device; Bodies = bodies;
            _pipeline = backend.CreateComputePipeline(device, kernel);
            _layout = kernel.Reflection.GetGlobalParamsVarLayout() ?? VariableLayoutReflection.Null;
            _view = backend.CreateBufferUav(device, new(bodies.States, new(0, bodies.States.Info.Size), StructureStride: DeviceBodies.StateStride));
            Commands = backend.CreateCommandContext(device, new(QueueType.Compute, 0, 1));
        }
        public void Execute()
        {
            _backend.Begin(Commands, new(8, 0, 8));
            _backend.SetPipeline(Commands, _pipeline);
            _backend.SetTransientParameterBindings(Commands, new(_layout, [ResourceBinding.WritableBuffer(_view)], []));
            _backend.Dispatch(Commands, new(1, 1, 1));
            _backend.Barrier(Commands, new MemoryBarrier(PipelineSync.ComputeShading, PipelineSync.ComputeShading,
                ResourceAccess.UnorderedAccess, ResourceAccess.UnorderedAccess));
            using RecordedCommands recorded = _backend.End(Commands);
            QueueCompletion ready = _backend.Submit(_backend.GetQueue(_device, QueueType.Compute), new([], [], [recorded], [], []));
            Assert.Equal(WaitStatus.Completed, _backend.WaitCpu(ready, TimeSpan.FromSeconds(60)));
            _backend.CollectCompleted(_device);
        }
        public void Dispose() { Commands.Dispose(); _view.Dispose(); _pipeline.Dispose(); }
    }
}
