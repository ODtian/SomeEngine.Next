using System.Numerics;
using SlangShaderSharp;
using SomeEngine.ECS;
using SomeEngine.ECS.Commands;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;
using SomeEngine.Physics;
using Xunit;

namespace SomeEngine.Graphics.Direct3D12.Tests;

public sealed partial class DeviceXpbdTests
{
    private struct RefreshedSleepingContactNotifications;

    [Fact]
    public void RefreshingResidentDeviceContactGeometryPreservesItsSleepingBodyAndCompletedLoad()
    {
        using var backend = D3D12GraphicsBackend.Create();
        using Device device = CreateDevice(backend);
        using var jobs = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using World world = WorldJobs.CreateWorld(scheduler: jobs);
        using var bodies = new DeviceBodies(backend, device, 2);
        using var equations = new DeviceAvbdBodyEquations(backend, device, bodies);
        using var grid = new DeviceCollisionGrid(backend, device, CompileSleepingRefreshGrid(), new(1, 3), new(2, 256, 257, 4, 1));
        using var geometry = new DeviceCollisionGeometry(backend, device, new(2, 0, 0, 0), bodies);
        Entity floor = AddBody(world, bodies, 0, Vector3.Zero, BodyMotion.Static);
        Entity moving = AddBody(world, bodies, 1, new(0, 2, 0), BodyMotion.Dynamic);
        world.Add(moving, new DeviceAvbdEquationBinding(equations, 1)); world.Add(moving, new BodyActivity { Radius = 1 });
        foreach (var (entity, index) in new[] { (floor, 0), (moving, 1) })
        {
            world.Add(entity, new SphereGeometry(1));
            world.Add(entity, new ContactMaterial { NormalStiffness = float.PositiveInfinity });
            world.Add(entity, new DeviceCollisionBody(grid, index)); world.Add(entity, new DeviceGeometryBinding(geometry, index));
        }
        QueryHandle members = world.Query(world.QueryDefinition().Read<DeviceBodyBinding>());
        QueryHandle spheres = world.Query(world.QueryDefinition().Read<SphereGeometry>());
        QueryHandle poses = world.Query(world.QueryDefinition().Read<BodyPose>());
        QueryHandle points = world.Query(world.QueryDefinition().Read<ContactPoint>());
        QueryHandle states = world.Query(world.QueryDefinition().Read<AvbdContactState>()
            .Read<DeviceConstraintBinding<DeviceContactConstraint>>().Read<DeviceAvbdState<DeviceAvbdContactState>>());
        QueryHandle events = world.Query(world.QueryDefinition().Read<PhysicsStepEvents>().ReadBuffer<PhysicsEvent>());
        var pairs = world.Within(spheres);
        using var detection = new DeviceCollisionDetection(backend, device, grid, geometry, CompileSleepingRefreshDetection(), 0,
            new(4, 4, 1), new(1e-5f, 32, 64), .02f, DeviceInputTransfer.Resident, DeviceCollisionReadback.Contacts);
        using var detected = new ContactPointBuffer(jobs, 4, 8);
        using var parameters = new DeviceConstraintData<DeviceContactConstraint>(backend, device, 4, "DeviceContacts");
        using var anchors = new DeviceContactBodies(backend, device, 4, 2);
        using var histories = new DeviceConstraintState<DeviceAvbdContactState>(backend, device, 4, "DeviceAvbdContactStates");
        using var responses = new DeviceConstraintState<DeviceContactResponseState>(backend, device, 4, "DeviceContactResponses");
        using var matching = new DeviceContactMatches(backend, device, world, points, parameters, detection,
            CompileSleepingRefreshMatching(), new(.25f, .99f, true), bucketCount: 17, threadsPerGroup: 64);
        using var currentGeometry = matching.CreateGeometryUpdate(bodies, anchors, CompileSleepingRefreshContacts());
        using var refresh = ContactPoints.Create(world, poses, points, detected, new AverageContactMaterials(),
            matching.Components(new SleepingRefreshComponents(histories, responses)), new(.25f, .99f, true));
        using var input = new DeviceContactInputs(world, points, parameters, anchors);
        using var stateInput = new DeviceAvbdStateInputs<AvbdContactState, DeviceContactConstraint, DeviceAvbdContactState, DeviceAvbdContactStateSource>(
            backend, device, world, states, parameters, histories, default);
        using var selected = new DeviceConstraintSelection<ContactPoint, DeviceContactConstraint>(backend, device, world,
            points, parameters, "DeviceContactsSelection");
        using var selectedStates = new DeviceConstraintSelection<ContactPoint, DeviceContactConstraint, DeviceAvbdStateRows<DeviceAvbdContactState>>(
            backend, device, world, points, parameters, new(histories), "DeviceContactAvbdSelection");
        using var selectedResponses = new DeviceConstraintSelection<ContactPoint, DeviceContactConstraint, DeviceContactResponseRows>(
            backend, device, world, points, parameters, new(responses), "DeviceContactResponsesSelection");
        using var activities = new DeviceBodyActivities(backend, device, world, members, bodies);
        world.Add(moving, new DeviceBodyActivityBinding(activities));
        var bindings = new Dictionary<string, ResourceBinding[]>(); activities.AddBindings(bindings);
        parameters.AddBindings(bindings); anchors.AddBindings(bindings); histories.AddBindings(bindings); responses.AddBindings(bindings);
        using var dynamics = new DeviceBodyDynamics(backend, device, world, members, bodies, CompileActiveBodyDynamics(), 64,
            bindings, activities.GetWriteAccesses(jobs));
        using var bodyInputs = new DeviceBodyInputs(world, members, bodies);
        using var bodyResults = new DeviceBodyResults(backend, device, bodies);
        using var sphereInputs = grid.WriteGeometryReferences<SphereGeometry, DeviceComponentSource<SphereGeometry, DeviceSphereReader>>(pairs, default, 0, 64);
        using var update = new UpdateDeviceGeometry(backend, device, grid, geometry, CompileSleepingRefreshGeometry(), 0, DeviceInputTransfer.Upload);
        JobResourceAccess[] reads = [.. parameters.GetReadAccesses(jobs), anchors.GetReadAccess(jobs), .. activities.GetReadAccesses(jobs)];
        using var graph = new DeviceConstraintGraph(backend, device, bodies, dynamics, CompileSleepingContactGraph(), new(4, 2),
            bindings, reads, 64, new[] { selected });
        using var solve = new DeviceAvbdSolver(backend, device, bodies, equations, graph, CompileSleepingContactAvbd(), new(16, 1, .5f, 0),
            bindings, reads.Concat(new[] { histories.GetWriteAccess(jobs), responses.GetWriteAccess(jobs) }).ToArray(), 64,
            new IDeviceConstraintSelection[] { selectedStates, selectedResponses });
        using var actions = new DeviceContactActions(backend, device, bodies, graph, responses, CompileSleepingContactActions(), bindings,
            reads, 64, new[] { selectedResponses });
        using var activity = new DevicePhysicsActivity(backend, device, activities, graph, CompileSleepingContactActivity(),
            new(.05f, .2f, .01f, .01f), bindings, reads, 64);
        using var samples = new DeviceContactSamples(backend, device, bodies, parameters, CompileSleepingContactSamples(), bindings,
            reads.Append(responses.GetReadAccess(jobs)).ToArray(), 64, new IDeviceConstraintSelection[] { selected, selectedResponses });
        using var output = new PhysicsEventBuffer(jobs, 16);
        using var observation = new ContactEvents<RefreshedSleepingContactNotifications, DeviceContactEventSource>(world, members, points, output, .001f, new(samples));
        var received = new List<(long Number, PhysicsEvent Event)>();
        using var consumer = new ConsumePhysicsEvents<RecordDeviceActivityEvents>(world, events, new(received),
            [JobResourceAccess.Write(jobs.GetResourceToken(received))]);
        var stages = bodyInputs.Upload().Then(grid.BeginInputs()).Then(sphereInputs).Then(update.Update())
            .Then(grid.Build(pairs, DevicePairRules.None, DeviceInputTransfer.Resident, DevicePairReadback.None)).Then(detection.Detect())
            .Then(detection.WriteContacts(new ContactPointOutput(detected), new[] { detected.WriteAccess }))
            .Then(matching.Match()).Then(refresh.Refresh(currentGeometry.Results)).Then(input.Upload()).Then(stateInput.Upload())
            .Then(currentGeometry.Update()).Then(activity.UploadInputs()).Then(dynamics.UpdateSelection()).Then(activity.WakeConnected())
            .Then(dynamics.Predict(new(0, -10, 0))).Then(actions.Initialize()).Then(solve.Solve()).Then(dynamics.UpdateVelocities())
            .Then(activity.Finish()).Then(activity.Readback(output)).Then(samples.Readback()).Then(observation).Then(output.Emit());
        using var frame = PhysicsSimulation.Create(world, QueryDefinition.Empty, stages, 1).CreateFrame(consumer, .1, 0, 0, 0);
        var initialize = new PhysicsStepContext(world, QueryDefinition.Empty, 0, 0, 1, 0, .1);
        parameters.BeginInputs().Then(parameters.UploadInputs()).Schedule(in initialize, default).Complete();

        frame.AdvanceTime(.1); frame.CompleteForRead(); detected.Clear();
        frame.AdvanceTime(.1); frame.CompleteForRead(); detected.Clear();
        Assert.True(world.Read<BodyActivity>(moving).Sleeping);
        frame.AdvanceTime(.1); frame.CompleteForRead();
        var completed = new PhysicsStepContext(world, QueryDefinition.Empty, 2, 0, 1, .2, .1);
        bodyResults.Readback(64).Schedule(in completed, default).Complete();

        Assert.True(world.Read<BodyActivity>(moving).Sleeping);
        Near(new(0, 2, 0), world.Read<BodyPose>(moving).Position); Near(Vector3.Zero, world.Read<BodyVelocity>(moving).Linear);
        var contacts = received.Where(item => item.Event.Kind == PhysicsEventKind.Contact).ToArray();
        Assert.Equal(new[] { PhysicsEventPhase.Begin, PhysicsEventPhase.Stay, PhysicsEventPhase.Stay }, contacts.Select(item => item.Event.Phase));
        Assert.Equal(new long[] { 0, 1, 2 }, contacts.Select(item => item.Number));
        Assert.All(contacts, item => { Assert.Equal(contacts[0].Event.Subject, item.Event.Subject); Assert.InRange(item.Event.Force.Y, 9.98f, 10.02f); });
        Assert.Equal(1, Assert.Single(received, item => item.Event.Kind == PhysicsEventKind.Sleep).Number);
        world.ReleaseQuery(events); world.ReleaseQuery(states); world.ReleaseQuery(points); world.ReleaseQuery(poses);
        world.ReleaseQuery(spheres); world.ReleaseQuery(members);
    }

    private readonly struct SleepingRefreshComponents(DeviceConstraintState<DeviceAvbdContactState> histories,
        DeviceConstraintState<DeviceContactResponseState> responses) : IContactPointComponents
    {
        public void AddReads(World world, List<JobResourceAccess> accesses) { }
        public void Add(ref JobCommandWriter commands, Entity point, in ContactPoint geometry,
            ReadOnlySpan<ContactBody> first, ReadOnlySpan<ContactBody> second, in PhysicsStepContext context)
        {
            commands.Add(point, new AvbdContactState { NormalPenalty = 1e4f });
            commands.Add(point, new DeviceAvbdState<DeviceAvbdContactState>(histories));
            commands.Add(point, new DeviceContactResponseBinding(responses));
            commands.Add(point, new ContactEventState<RefreshedSleepingContactNotifications>());
        }
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
        "#include \"physics_contact_geometry.slang\"", "UpdateDeviceContactGeometry");
    private ComputePipelineDesc CompileSleepingRefreshEntry(string name, string source, string entry)
    {
        D3D12TestShader shader = shaderCompiler.CompileWithSearchPaths(name, source, [new(entry, SlangStage.Compute)], "sm_6_0", ShaderDirectory());
        return new(shader.SlangCode, shader.Reflection, shader.GetEntryPoint(0), shader.GetEntryCode(0));
    }
    private DeviceContactMatchKernels CompileSleepingRefreshMatching()
    {
        D3D12TestShader shader = shaderCompiler.CompileWithSearchPaths("sleeping_refresh_matching", "#include \"physics_contact_matching.slang\"",
            [new("ClearDeviceContactMatches", SlangStage.Compute), new("IndexDeviceContactMatches", SlangStage.Compute),
                new("MatchDeviceContacts", SlangStage.Compute)], "sm_6_0", ShaderDirectory());
        ComputePipelineDesc Entry(int index) => new(shader.SlangCode, shader.Reflection, shader.GetEntryPoint(index), shader.GetEntryCode(index));
        return new(Entry(0), Entry(1), Entry(2));
    }
}
