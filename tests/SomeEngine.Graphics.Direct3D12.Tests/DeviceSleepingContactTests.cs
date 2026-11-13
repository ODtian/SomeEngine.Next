using System.Numerics;
using SlangShaderSharp;
using SomeEngine.ECS;
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
    public void ASleepingDeviceContactKeepsItsSupportForceInCompletedEvents()
    {
        using var scene = new SleepingContactScene(this);
        scene.Step(); scene.Step();
        Assert.True(scene.World.ReadComponentData<BodyActivity>(scene.Moving).Sleeping);

        scene.Step();

        Assert.True(scene.World.ReadComponentData<BodyActivity>(scene.Moving).Sleeping);
        var contacts = scene.Received.Where(item => item.Event.Kind == PhysicsEventKind.Contact).ToArray();
        Assert.Equal(new[] { PhysicsEventPhase.Begin, PhysicsEventPhase.Stay, PhysicsEventPhase.Stay }, contacts.Select(item => item.Event.Phase));
        Assert.Equal(new long[] { 0, 1, 2 }, contacts.Select(item => item.Number));
        Assert.All(contacts, item => Assert.InRange(item.Event.Force.Y, 9.98f, 10.02f));
        var sleep = Assert.Single(scene.Received, item => item.Event.Kind == PhysicsEventKind.Sleep);
        Assert.Equal(scene.Moving, sleep.Event.Subject); Assert.Equal(1, sleep.Number);
    }

    private sealed class SleepingContactScene : IDisposable
    {
        private readonly List<IDisposable> _resources = [];
        private readonly Scheduler _jobs;
        private readonly ContactManifolds<DeviceAvbdContactState> _cpu;
        private readonly DeviceContactInputs<DeviceAvbdContactState> _input;
        private readonly Query _members;
        private readonly IPhysicsFrame _frame;
        private long _next;
        internal World World { get; }
        internal Entity Floor { get; }
        internal Entity Moving { get; }
        internal readonly List<(long Number, PhysicsEvent Event)> Received = [];
        internal SleepingContactScene(DeviceXpbdTests test)
        {
            var backend = Keep(D3D12GraphicsBackend.Create()); Device device = Keep(CreateDevice(backend));
            _jobs = Keep(new Scheduler(new JobConfiguration { WorkerCount = 2 }));
            World = Keep(new World(execution: new WorldJobs(_jobs)));
            var bodies = Keep(new DeviceBodies(backend, device, 2));
            var equations = Keep(new DeviceAvbdBodyEquations(backend, device, bodies));
            Floor = AddBody(World, bodies, 0, Vector3.Zero, BodyMotion.Static);
            Moving = AddBody(World, bodies, 1, Vector3.UnitY, BodyMotion.Dynamic);
            World.Add(Moving, new DeviceAvbdEquationBinding(equations, 1)); World.Add(Moving, new BodyActivity { Radius = 1 });
            World.Add(Moving, new ReportContacts());
            _members = World.Query(new QueryDescription().With<DeviceBodyBinding>());
            _cpu = Keep(new ContactManifolds<DeviceAvbdContactState>(_jobs, new() { NormalPenalty = 1e4f }));
            _jobs.Schedule(new SeedContact(_cpu, Floor, Moving), [_cpu.WriteAccess]).Complete();
            var contacts = Keep(new DeviceContactManifolds<DeviceAvbdContactState>(backend, device, new(1, 1, 2), new() { NormalPenalty = 1e4f }));
            _input = Keep(new DeviceContactInputs<DeviceAvbdContactState>(backend, device, World, _cpu, contacts));
            var activities = Keep(new DeviceBodyActivities(backend, device, World, _members, bodies));
            World.Add(Moving, new DeviceBodyActivityBinding(activities));
            var bindings = new Dictionary<string, ResourceBinding[]>(); activities.AddBindings(bindings); contacts.Current.AddBindings(bindings);
            var dynamics = Keep(new BodyDynamicsCommands(backend, device, World, _members, bodies, test.CompileActiveBodyDynamics(), 64,
                bindings, activities.GetWriteAccesses(_jobs)));
            var bodyInputs = Keep(new DeviceBodyInputs(World, _members, bodies));
            var bodyResults = Keep(new DeviceBodyResults(backend, device, bodies));
            JobResourceAccess[] reads = activities.GetReadAccesses(_jobs);
            var graph = Keep(new DeviceConstraintGraph(backend, device, bodies, dynamics, test.CompileSleepingContactGraph(), new(1, 2),
                bindings, reads, 64, contacts: contacts));
            var solve = Keep(new AvbdGraphCommands(backend, device, bodies, equations, graph, test.CompileSleepingContactAvbd(),
                new(16, 1, .5f, 0), bindings, reads, 64));
            var actions = Keep(new DeviceContactActions(backend, device, bodies, graph, contacts, test.CompileSleepingContactActions(),
                bindings, reads, 64, Array.Empty<IDeviceConstraintSelection>()));
            var activity = Keep(new DevicePhysicsActivity(backend, device, activities, graph, test.CompileSleepingContactActivity(),
                new(.05f, .2f, .01f, .01f), bindings, reads, 64));
            var samples = Keep(new DeviceContactSamples(backend, device, bodies, contacts, test.CompileSleepingContactSamples()));
            var contactResults = Keep(new DeviceContactResults(backend, device, _jobs));
            var output = Keep(new PhysicsEventBuffer(World, 8));
            var observation = Keep(new DeviceContactEvents(World, samples, contactResults.Contacts, output, .001f));
            var consumer = Keep(new RecordPhysicsEvents(_jobs, output.Watch(), Received));
            var initialize = new PhysicsStepContext(World, _members, 0, 0, 1, 0, .1);
            bodyInputs.Upload().Then(_input.Upload()).Schedule(in initialize, default).Complete();
            var stages = bodyInputs.Upload().Then(activity.UploadInputs()).Then(dynamics.UpdateSelection()).Then(activity.WakeConnected())
                .Then(dynamics.Predict(new(0, -10, 0))).Then(actions.Initialize()).Then(solve.Solve()).Then(dynamics.UpdateVelocities())
                .Then(activity.Finish()).Then(activity.Readback(output)).Then(bodyResults.Readback(64))
                .Then(samples.Readback()).Then(contactResults.Readback(contacts)).Then(observation).Then(output.Emit());
            _frame = Keep(PhysicsSimulation.Create(World, _members, stages, 1).CreateFrame(consumer, .1, 0, 0, 0));
        }
        internal void Step() { _frame.AdvanceTime(.1); _frame.CompleteForRead(); _next++; }
        internal void Soften()
        {
            _jobs.Schedule(new ChangeMaterial(_cpu), [_cpu.WriteAccess]).Complete();
            var context = new PhysicsStepContext(World, _members, _next, 0, 1, _next * .1, .1);
            _input.Upload().Schedule(in context, _frame.Completion).Complete();
        }
        private readonly struct ChangeMaterial(ContactManifolds<DeviceAvbdContactState> contacts) : IJob
        { public void Execute() => contacts.Materials[0].NormalStiffness = 0; }
        private readonly struct SeedContact(ContactManifolds<DeviceAvbdContactState> contacts, Entity floor, Entity moving) : IJob
        {
            public void Execute()
            {
                contacts.Resize(1, 1, 2); contacts.InitializeState(0);
                ContactColumns columns = contacts.Columns;
                columns.Pairs[0] = new(floor, moving, 0, 0, 0, 1);
                columns.SetPoint(0, new ContactPoint { Normal = Vector3.UnitY, BodyAnchorDelta = Vector3.UnitY, FirstBodyCount = 1, WarmStart = true });
                columns.BodyCounts[0] = 2;
                columns.BodyLinks[0] = new(floor, Vector3.Zero, 1); columns.BodyLinks[1] = new(moving, Vector3.Zero, 1);
                columns.Materials[0] = new() { NormalStiffness = float.PositiveInfinity };
                columns.RebuildBodyPairs();
            }
        }
        private T Keep<T>(T value) where T : IDisposable { _resources.Add(value); return value; }
        public void Dispose() { for (int index = _resources.Count - 1; index >= 0; index--) _resources[index].Dispose(); }
    }

    private DeviceGraphKernels CompileSleepingContactGraph() => DeviceGraphTestKernels.CompileParallel(shaderCompiler, "sleeping_contact_graph", """
        #include "physics_body_activity_types.slang"
        #define DEVICE_GRAPH_BODY_ACTIVE(body) deviceBodyActivityAwake(body)
        #include "physics_contact_data.slang"
        [shader("compute")][numthreads(64,1,1)]
        void WriteRows(uint3 id:SV_DispatchThreadID) { DeviceContactBodies rows; writeDeviceConstraintGraphRows(id.x,rows); }
        """, ShaderDirectory());

    private AvbdGraphKernels CompileSleepingContactAvbd()
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
                new("FinishDeviceBodyActivity", SlangStage.Compute), new("ReadRemovedDeviceActivityContacts", SlangStage.Compute)], "sm_6_0", ShaderDirectory());
        ComputePipelineDesc Entry(int index) => new(shader.SlangCode, shader.Reflection, shader.GetEntryPoint(index), shader.GetEntryCode(index));
        return new(Entry(0), Entry(1), Entry(2), Entry(3), Entry(4), Entry(5), Entry(6), RemovedContacts: Entry(7));
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
