using System.Numerics;
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

public sealed partial class DeviceXpbdTests
{
    private struct SleepingContactNotifications;

    [Fact]
    public void ASleepingDeviceContactKeepsItsSupportForceInCompletedEvents()
    {
        using var backend = D3D12GraphicsBackend.Create();
        using Device device = CreateDevice(backend);
        using var jobs = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using World world = WorldJobs.CreateWorld(scheduler: jobs);
        using var bodies = new DeviceBodies(backend, device, 2);
        using var equations = new DeviceAvbdBodyEquations(backend, device, bodies);
        Entity floor = AddBody(world, bodies, 0, Vector3.Zero, BodyMotion.Static);
        Entity moving = AddBody(world, bodies, 1, Vector3.UnitY, BodyMotion.Dynamic);
        world.Add(moving, new DeviceAvbdEquationBinding(equations, 1));
        world.Add(moving, new BodyActivity { Radius = 1 });
        Entity point = world.CreateRelation(floor, moving, new ContactPoint
            { Normal = Vector3.UnitY, BodyAnchorDelta = Vector3.UnitY, FirstBodyCount = 1, WarmStart = true }).Entity;
        var first = world.CreateRelation(point, floor, new ContactParticipant());
        var second = world.CreateRelation(point, moving, new ContactParticipant());
        world.AddBuffer<ContactBodyLink>(point);
        world.ExecuteBufferWrite<ContactBodyLink>(point, links =>
        { links.Add(new(first, Vector3.Zero, 1)); links.Add(new(second, Vector3.Zero, 1)); });
        world.Add(point, new ContactMaterial { NormalStiffness = float.PositiveInfinity });
        world.Add(point, new AvbdContactState { NormalPenalty = 1e4f }); world.Add(point, new ContactResponse());
        world.Add(point, new ContactEventState<SleepingContactNotifications>());
        using var parameters = new DeviceConstraintData<DeviceContactConstraint>(backend, device, 1, "DeviceContacts");
        using var anchors = new DeviceContactBodies(backend, device, 1, 2);
        using var histories = new DeviceConstraintState<DeviceAvbdContactState>(backend, device, 1, "DeviceAvbdContactStates");
        using var responses = new DeviceConstraintState<DeviceContactResponseState>(backend, device, 1, "DeviceContactResponses");
        world.Add(point, new DeviceConstraintBinding<DeviceContactConstraint>(parameters, 0));
        world.Add(point, new DeviceAvbdState<DeviceAvbdContactState>(histories)); world.Add(point, new DeviceContactResponseBinding(responses));
        QueryHandle members = world.Query(world.QueryDefinition().Read<DeviceBodyBinding>());
        QueryHandle contacts = world.Query(world.QueryDefinition().Read<ContactPoint>());
        QueryHandle states = world.Query(world.QueryDefinition().Read<AvbdContactState>()
            .Read<DeviceConstraintBinding<DeviceContactConstraint>>().Read<DeviceAvbdState<DeviceAvbdContactState>>());
        QueryHandle events = world.Query(world.QueryDefinition().Read<PhysicsStepEvents>().ReadBuffer<PhysicsEvent>());
        using var activities = new DeviceBodyActivities(backend, device, world, members, bodies);
        world.Add(moving, new DeviceBodyActivityBinding(activities));
        var bindings = new Dictionary<string, ResourceBinding[]>(); activities.AddBindings(bindings);
        using var dynamics = new DeviceBodyDynamics(backend, device, world, members, bodies, CompileActiveBodyDynamics(), 64,
            bindings, activities.GetWriteAccesses(jobs));
        using var bodyInputs = new DeviceBodyInputs(world, members, bodies);
        using var inputs = new DeviceContactInputs(world, contacts, parameters, anchors);
        using var stateInputs = new DeviceAvbdStateInputs<AvbdContactState, DeviceContactConstraint, DeviceAvbdContactState, DeviceAvbdContactStateSource>(
            backend, device, world, states, parameters, histories, default);
        using var selected = new DeviceConstraintSelection<ContactPoint, DeviceContactConstraint>(backend, device, world,
            contacts, parameters, "DeviceContactsSelection");
        using var selectedStates = new DeviceConstraintSelection<ContactPoint, DeviceContactConstraint, DeviceAvbdStateRows<DeviceAvbdContactState>>(
            backend, device, world, contacts, parameters, new(histories), "DeviceContactAvbdSelection");
        using var selectedResponses = new DeviceConstraintSelection<ContactPoint, DeviceContactConstraint, DeviceContactResponseRows>(
            backend, device, world, contacts, parameters, new(responses), "DeviceContactResponsesSelection");
        parameters.AddBindings(bindings); anchors.AddBindings(bindings); histories.AddBindings(bindings); responses.AddBindings(bindings);
        JobResourceAccess[] reads = [.. parameters.GetReadAccesses(jobs), anchors.GetReadAccess(jobs), .. activities.GetReadAccesses(jobs)];
        using var graph = new DeviceConstraintGraph(backend, device, bodies, dynamics, CompileSleepingContactGraph(), new(1, 2),
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
        using var output = new PhysicsEventBuffer(jobs, 8);
        using var observation = new ContactEvents<SleepingContactNotifications, DeviceContactEventSource>(world, members, contacts, output, .001f, new(samples));
        var received = new List<(long Number, PhysicsEvent Event)>();
        using var consumer = new ConsumePhysicsEvents<RecordDeviceActivityEvents>(world, events, new(received),
            [JobResourceAccess.Write(jobs.GetResourceToken(received))]);
        var stages = bodyInputs.Upload().Then(inputs.Upload()).Then(stateInputs.Upload()).Then(activity.UploadInputs())
            .Then(dynamics.UpdateSelection()).Then(activity.WakeConnected()).Then(dynamics.Predict(new(0, -10, 0)))
            .Then(actions.Initialize()).Then(solve.Solve()).Then(dynamics.UpdateVelocities()).Then(activity.Finish())
            .Then(activity.Readback(output)).Then(samples.Readback()).Then(observation).Then(output.Emit());
        using var frame = PhysicsSimulation.Create(world, QueryDefinition.Empty, stages, 1).CreateFrame(consumer, .1, 0, 0, 0);

        frame.AdvanceTime(.1); frame.CompleteForRead(); frame.AdvanceTime(.1); frame.CompleteForRead();
        Assert.True(world.Read<BodyActivity>(moving).Sleeping);
        frame.AdvanceTime(.1); frame.CompleteForRead();

        Assert.True(world.Read<BodyActivity>(moving).Sleeping);
        var contactsReceived = received.Where(item => item.Event.Kind == PhysicsEventKind.Contact).ToArray();
        Assert.Equal(new[] { PhysicsEventPhase.Begin, PhysicsEventPhase.Stay, PhysicsEventPhase.Stay }, contactsReceived.Select(item => item.Event.Phase));
        Assert.Equal(new long[] { 0, 1, 2 }, contactsReceived.Select(item => item.Number));
        Assert.All(contactsReceived, item => Assert.InRange(item.Event.Force.Y, 9.98f, 10.02f));
        var sleep = Assert.Single(received, item => item.Event.Kind == PhysicsEventKind.Sleep);
        Assert.Equal(moving, sleep.Event.Subject); Assert.Equal(1, sleep.Number);
        world.ReleaseQuery(events); world.ReleaseQuery(states); world.ReleaseQuery(contacts); world.ReleaseQuery(members);
    }

    private DeviceGraphKernels CompileSleepingContactGraph() => DeviceGraphTestKernels.CompileParallel(shaderCompiler, "sleeping_contact_graph", """
        #include "physics_body_activity_types.slang"
        #define DEVICE_GRAPH_BODY_ACTIVE(body) deviceBodyActivityAwake(body)
        #include "physics_contact_data.slang"
        [shader("compute")][numthreads(64,1,1)]
        void WriteRows(uint3 id:SV_DispatchThreadID) { DeviceContactBodies rows; writeDeviceConstraintGraphRows(id.x,rows); }
        """, ShaderDirectory());

    private DeviceAvbdKernels CompileSleepingContactAvbd()
    {
        D3D12TestShader shader = shaderCompiler.CompileWithSearchPaths("sleeping_contact_avbd", """
            #include "physics_avbd_contact.slang"
            #include "physics_awake_avbd.slang"
            [shader("compute")][numthreads(64,1,1)]
            void Initialize(uint3 id:SV_DispatchThreadID)
            { DeviceAwakeAvbdConstraints<DeviceContactAvbdConstraints> rows; DeviceAwakeBodies bodies; initializeDeviceAvbd(id.x,rows,bodies); }
            [shader("compute")][numthreads(64,1,1)]
            void UpdateBody(uint3 id:SV_DispatchThreadID)
            { DeviceAwakeAvbdConstraints<DeviceContactAvbdConstraints> rows; updateDeviceAvbdBody(id.x,rows); }
            [shader("compute")][numthreads(64,1,1)]
            void UpdateDuals(uint3 id:SV_DispatchThreadID)
            { DeviceAwakeAvbdConstraints<DeviceContactAvbdConstraints> rows; updateDeviceAvbdDuals(id.x,rows); }
            """, [new("Initialize", SlangStage.Compute), new("UpdateBody", SlangStage.Compute), new("UpdateDuals", SlangStage.Compute)],
            "sm_6_0", ShaderDirectory());
        ComputePipelineDesc Entry(int index) => new(shader.SlangCode, shader.Reflection, shader.GetEntryPoint(index), shader.GetEntryCode(index));
        return new(Entry(0), Entry(1), Entry(2));
    }
    private DeviceContactKernels CompileSleepingContactActions()
    {
        D3D12TestShader shader = shaderCompiler.CompileWithSearchPaths("sleeping_contact_actions", """
            #include "physics_awake_contact_actions.slang"
            [shader("compute")][numthreads(64,1,1)]
            void Initialize(uint3 id:SV_DispatchThreadID)
            { DeviceAwakeContactResponseActions<DeviceGraphBodies> rows; initializeDeviceContactResponses(id.x,rows); }
            [shader("compute")][numthreads(64,1,1)]
            void Restore(uint3 id:SV_DispatchThreadID)
            { DeviceAwakeContactResponseActions<DeviceGraphBodies> rows; applyDeviceContactRestitution(id.x,rows); }
            """, [new("Initialize", SlangStage.Compute), new("Restore", SlangStage.Compute)], "sm_6_0", ShaderDirectory());
        ComputePipelineDesc Entry(int index) => new(shader.SlangCode, shader.Reflection, shader.GetEntryPoint(index), shader.GetEntryCode(index));
        return new(Entry(0), Entry(1));
    }
    private DeviceActivityKernels CompileSleepingContactActivity()
    {
        D3D12TestShader shader = shaderCompiler.CompileWithSearchPaths("sleeping_contact_activity", """
            #include "physics_contact_activity.slang"
            [shader("compute")][numthreads(64,1,1)]
            void ReadRows(uint3 id:SV_DispatchThreadID) { DeviceContactActivity rows; readDeviceActivityRows(id.x,rows); }
            """, [new("ApplyDeviceActivityInputs", SlangStage.Compute), new("ClearDeviceActivitySignals", SlangStage.Compute),
                new("CollectDeviceActivityBodies", SlangStage.Compute), new("ReadRows", SlangStage.Compute),
                new("WakeDeviceBodies", SlangStage.Compute), new("AdvanceDeviceBodyQuietTime", SlangStage.Compute),
                new("FinishDeviceBodyActivity", SlangStage.Compute)], "sm_6_0", ShaderDirectory());
        ComputePipelineDesc Entry(int index) => new(shader.SlangCode, shader.Reflection, shader.GetEntryPoint(index), shader.GetEntryCode(index));
        return new(Entry(0), Entry(1), Entry(2), Entry(3), Entry(4), Entry(5), Entry(6));
    }
    private ComputePipelineDesc CompileSleepingContactSamples()
    {
        D3D12TestShader shader = shaderCompiler.CompileWithSearchPaths("sleeping_contact_samples", """
            #include "physics_contact_response_samples.slang"
            [shader("compute")][numthreads(64,1,1)]
            void ReadSamples(uint3 id:SV_DispatchThreadID) { DeviceResponseEventForces forces; readDeviceContactSamples(id.x,forces); }
            """, [new("ReadSamples", SlangStage.Compute)], "sm_6_0", ShaderDirectory());
        return new(shader.SlangCode, shader.Reflection, shader.GetEntryPoint(0), shader.GetEntryCode(0));
    }
}
