using System.Numerics;
using SlangShaderSharp;
using SomeEngine.ECS;
using SomeEngine.ECS.Commands;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;
using SomeEngine.Physics;
using Xunit;

namespace SomeEngine.Graphics.Direct3D12.Tests;

public sealed partial class DeviceXpbdTests
{
    [Fact]
    public void RefreshingResidentDeviceContactGeometryPreservesItsSleepingBodyAndCompletedLoad()
    {
        using var backend = D3D12GraphicsBackend.Create();
        using Device device = CreateDevice(backend);
        using var jobs = new Scheduler(new JobConfiguration { WorkerCount = 2 });
        using var world = new World(execution: new WorldJobs(jobs));
        using var bodies = new DeviceBodies(backend, device, 2);
        using var equations = new DeviceAvbdBodyEquations(backend, device, bodies);
        using var grid = new DeviceCollisionGrid(backend, device, CompileSleepingRefreshGrid(), new(1, 3), new(2, 256, 257, 4, 1));
        using var geometry = new DeviceCollisionGeometry(backend, device, new(2, 0, 0, 0), bodies);
        Entity floor = AddBody(world, bodies, 0, Vector3.Zero, BodyMotion.Static);
        Entity moving = AddBody(world, bodies, 1, new(0, 2, 0), BodyMotion.Dynamic);
        world.Add(moving, new DeviceAvbdEquationBinding(equations, 1)); world.Add(moving, new BodyActivity { Radius = 1 });
        world.Add(moving, new ReportContacts());
        using (world.OpenStructureWrite())
            foreach (var (entity, index) in new[] { (floor, 0), (moving, 1) })
            {
                world.Add(entity, new SphereGeometry(1));
                world.Add(entity, new ContactMaterial { NormalStiffness = float.PositiveInfinity });
                world.Add(entity, new DeviceCollisionBody(grid, index)); world.Add(entity, new DeviceGeometryBinding(geometry, index));
            }
        Query members = world.Query(new QueryDescription().With<DeviceBodyBinding>());
        Query spheres = world.Query(new QueryDescription().With<SphereGeometry>());
        var pairs = world.Within(spheres);
        using var detection = new DeviceCollisionDetection(backend, device, grid, geometry, CompileSleepingRefreshDetection(), 0,
            new(4, 4, 1) { Contacts = 4, BodiesPerContact = 2 }, new(1e-5f, 32, 64), .02f,
            DeviceInputTransfer.Resident, DeviceCollisionReadback.None);
        using var contacts = new DeviceContactManifolds<DeviceAvbdContactState>(backend, device, new(4, 4, 8), new() { NormalPenalty = 1e4f });
        using var matching = new DeviceContactMatches<DeviceAvbdContactState>(backend, device, contacts, detection, bodies,
            CompileSleepingRefreshMatching(), new(.25f, .99f, true), new() { NormalStiffness = float.PositiveInfinity }, 17);
        using var initialize = new ContactStateInitialization<DeviceAvbdContactState>(backend, device, matching,
            CompileSleepingRefreshEntry("sleeping_refresh_state", "#include \"physics_contact_state_initialization.slang\"", "InitializeContactStateRows"));
        using var activities = new DeviceBodyActivities(backend, device, world, members, bodies);
        world.Add(moving, new DeviceBodyActivityBinding(activities));
        var bindings = new Dictionary<string, ResourceBinding[]>(); activities.AddBindings(bindings); contacts.Current.AddBindings(bindings);
        using var dynamics = new BodyDynamicsCommands(backend, device, world, members, bodies, CompileActiveBodyDynamics(), 64,
            bindings, activities.GetWriteAccesses(jobs));
        using var bodyInputs = new DeviceBodyInputs(world, members, bodies);
        using var bodyResults = new DeviceBodyResults(backend, device, bodies);
        using var sphereInputs = grid.WriteGeometryReferences<SphereGeometry, DeviceComponentSource<SphereGeometry, DeviceSphereReader>>(pairs, default, 0, 64);
        using var update = new UpdateDeviceGeometry(backend, device, grid, geometry, CompileSleepingRefreshGeometry(), 0,
            DeviceInputTransfer.Upload, contactDistance: .02f);
        JobResourceAccess[] reads = activities.GetReadAccesses(jobs);
        using var graph = new DeviceConstraintGraph(backend, device, bodies, dynamics, CompileSleepingContactGraph(), new(4, 2),
            bindings, reads, 64, contacts: contacts);
        using var solve = new AvbdGraphCommands(backend, device, bodies, equations, graph, CompileSleepingContactAvbd(),
            new(16, 1, .5f, 0), bindings, reads, 64);
        using var actions = new DeviceContactActions(backend, device, bodies, graph, contacts, CompileSleepingContactActions(),
            bindings, reads, 64, Array.Empty<IDeviceConstraintSelection>());
        using var activity = new DevicePhysicsActivity(backend, device, activities, graph, CompileSleepingContactActivity(),
            new(.05f, .2f, .01f, .01f), bindings, reads, 64);
        using var samples = new DeviceContactSamples(backend, device, bodies, contacts, CompileSleepingContactSamples());
        using var contactResults = new DeviceContactResults(backend, device, jobs);
        using var output = new PhysicsEventBuffer(world, 16);
        using var observation = new DeviceContactEvents(world, samples, contactResults.Contacts, output, .001f);
        var received = new List<(long Number, PhysicsEvent Event)>();
        using var consumer = new RecordPhysicsEvents(jobs, output.Watch(), received);
        var stages = bodyInputs.Upload().Then(grid.BeginInputs()).Then(sphereInputs).Then(update.Update())
            .Then(grid.Build(pairs, DevicePairRules.None, DeviceInputTransfer.Resident, DevicePairReadback.None))
            .Then(detection.Detect()).Then(matching.Match()).Then(initialize.Initialize())
            .Then(activity.UploadInputs()).Then(dynamics.UpdateSelection()).Then(activity.WakeConnected())
            .Then(dynamics.Predict(new(0, -10, 0))).Then(actions.Initialize()).Then(solve.Solve()).Then(dynamics.UpdateVelocities())
            .Then(activity.Finish()).Then(activity.Readback(output)).Then(samples.Readback()).Then(contactResults.Readback(contacts))
            .Then(observation).Then(output.Emit());
        using var frame = PhysicsSimulation.Create(world, members, stages, 1).CreateFrame(consumer, .1, 0, 0, 0);

        frame.AdvanceTime(.1); frame.CompleteForRead();
        frame.AdvanceTime(.1); frame.CompleteForRead();
        Assert.True(world.ReadComponentData<BodyActivity>(moving).Sleeping);
        frame.AdvanceTime(.1); frame.CompleteForRead();
        var completed = new PhysicsStepContext(world, members, 2, 0, 1, .2, .1);
        bodyResults.Readback(64).Schedule(in completed, default).Complete();

        Assert.True(world.ReadComponentData<BodyActivity>(moving).Sleeping);
        Near(new(0, 2, 0), world.ReadComponentData<BodyPose>(moving).Position); Near(Vector3.Zero, world.ReadComponentData<BodyVelocity>(moving).Linear);
        var events = received.Where(item => item.Event.Kind == PhysicsEventKind.Contact).ToArray();
        Assert.Equal(new[] { PhysicsEventPhase.Begin, PhysicsEventPhase.Stay, PhysicsEventPhase.Stay }, events.Select(item => item.Event.Phase));
        Assert.Equal(new long[] { 0, 1, 2 }, events.Select(item => item.Number));
        Assert.All(events, item => { Assert.Equal(floor, item.Event.First); Assert.Equal(moving, item.Event.Second);
            Assert.InRange(item.Event.Force.Y, 9.98f, 10.02f); });
        Assert.Equal(1, Assert.Single(received, item => item.Event.Kind == PhysicsEventKind.Sleep).Number);
    }

    private DeviceGridKernels CompileSleepingRefreshGrid()
    {
        D3D12TestEntry[] entries = [new("ClearDeviceGrid", SlangStage.Compute), new("BuildDeviceGrid", SlangStage.Compute),
            new("CountDevicePairs", SlangStage.Compute), new("ScanDevicePairCounts", SlangStage.Compute),
            new("OffsetDevicePairBlocks", SlangStage.Compute), new("OffsetDevicePairBins", SlangStage.Compute), new("WriteDevicePairs", SlangStage.Compute)];
        D3D12TestShader shader = shaderCompiler.CompileWithSearchPaths("sleeping_refresh_grid", "#include \"physics_grid.slang\"", entries, "sm_6_0", ShaderDirectory());
        ComputePipelineDesc Entry(int index) => new(shader.SlangCode, shader.Reflection, shader.GetEntryPoint(index), shader.GetEntryCode(index));
        return new(Entry(0), Entry(1), Entry(2), Entry(3), Entry(4), Entry(5), Entry(6));
    }
    private ComputePipelineDesc CompileSleepingRefreshDetection() => CompileSleepingRefreshEntry("sleeping_refresh_detection", """
        #include "physics_geometry_references.slang"
        [shader("compute")][numthreads(64,1,1)]
        void Detect(uint3 id:SV_DispatchThreadID)
        { DeviceConvexCollision<DeviceSphere,DeviceSphere> kernel; detectDeviceReferencedCollision(id.x,kernel); }
        """, "Detect");
    private ComputePipelineDesc CompileSleepingRefreshGeometry() => CompileSleepingRefreshEntry("sleeping_refresh_geometry", """
        #include "physics_geometry_update.slang"
        [shader("compute")][numthreads(64,1,1)]
        void Update(uint3 id:SV_DispatchThreadID) { updateDeviceGeometry<DeviceSphereUpdate>(id.x); }
        """, "Update");
    private ComputePipelineDesc CompileSleepingRefreshContacts() => CompileSleepingRefreshEntry("sleeping_refresh_contacts",
        "#include \"physics_contact_geometry.slang\"", "WriteDeviceContactColumns");
    private ComputePipelineDesc CompileSleepingRefreshEntry(string name, string source, string entry)
    {
        D3D12TestShader shader = shaderCompiler.CompileWithSearchPaths(name, source, [new(entry, SlangStage.Compute)], "sm_6_0", ShaderDirectory());
        return new(shader.SlangCode, shader.Reflection, shader.GetEntryPoint(0), shader.GetEntryCode(0));
    }
    private DeviceContactMatchKernels CompileSleepingRefreshMatching()
    {
        D3D12TestShader shader = shaderCompiler.CompileWithSearchPaths("sleeping_refresh_matching", "#include \"physics_contact_matching.slang\"",
            [new("ClearDeviceContactMatches", SlangStage.Compute), new("IndexDeviceContactMatches", SlangStage.Compute),
                new("MatchDeviceContacts", SlangStage.Compute), new("ClaimContactInputs", SlangStage.Compute),
                new("CollectNewContactInputs", SlangStage.Compute), new("CollectRemovedContactRows", SlangStage.Compute)], "sm_6_0", ShaderDirectory());
        ComputePipelineDesc Entry(int index) => new(shader.SlangCode, shader.Reflection, shader.GetEntryPoint(index), shader.GetEntryCode(index));
        ComputePipelineDesc arguments = CompileSleepingRefreshEntry("sleeping_refresh_dispatch", "#include \"physics_contact_geometry.slang\"", "WriteDeviceContactDispatchArguments");
        return new(Entry(0), Entry(1), Entry(2), Entry(3), Entry(4), Entry(5), CompileSleepingRefreshContacts(), arguments);
    }
}
