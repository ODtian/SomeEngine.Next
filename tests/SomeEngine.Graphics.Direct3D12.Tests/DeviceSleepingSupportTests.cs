using System.Numerics;
using SlangShaderSharp;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;
using SomeEngine.Physics;
using Xunit;

namespace SomeEngine.Graphics.Direct3D12.Tests;

public sealed partial class DeviceXpbdTests
{
    private struct SleepingSupportNotifications;

    [Fact]
    public void ASleepingDeviceSupportStackKeepsItsLoadInCompletedContactEvents()
    {
        using var backend = D3D12GraphicsBackend.Create();
        using Device device = CreateDevice(backend);
        using var jobs = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using World world = WorldJobs.CreateWorld(scheduler: jobs);
        using var bodies = new DeviceBodies(backend, device, 4);
        using var equations = new DeviceAvbdBodyEquations(backend, device, bodies);
        Entity floor = AddBody(world, bodies, 0, Vector3.Zero, BodyMotion.Static);
        Entity lower = AddBody(world, bodies, 1, Vector3.UnitY, BodyMotion.Dynamic);
        Entity upper = AddBody(world, bodies, 2, new(0, 3, 0), BodyMotion.Dynamic);
        Entity falling = AddBody(world, bodies, 3, new(5, 10, 0), BodyMotion.Dynamic);
        Entity[] dynamicBodies = [lower, upper, falling];
        for (int index = 0; index < dynamicBodies.Length; index++)
        {
            world.Add(dynamicBodies[index], new DeviceAvbdEquationBinding(equations, index + 1));
            world.Add(dynamicBodies[index], new BodyActivity { Radius = 1 });
        }
        foreach (Entity body in new[] { lower, upper })
        {
            world.Replace(body, new BodyMass { Motion = BodyMotion.Dynamic, InverseMass = 1,
                CenterOfMass = new(.25f, 0, .2f), InverseInertia = Matrix4x4.Identity });
            world.Add(body, new LongRangeBodySupport());
            world.AddBuffer<LongRangeSupportLoad>(body); world.AddBuffer<LongRangeSupportEquation>(body);
        }
        using var parameters = new DeviceConstraintData<DeviceContactConstraint>(backend, device, 8, "DeviceContacts");
        using var links = new DeviceContactBodies(backend, device, 8, 2);
        using var histories = new DeviceConstraintState<DeviceAvbdContactState>(backend, device, 8, "DeviceAvbdContactStates");
        using var responses = new DeviceConstraintState<DeviceContactResponseState>(backend, device, 8, "DeviceContactResponses");
        QueryHandle supportBodies = world.Query(world.QueryDefinition().Read<LongRangeBodySupport>()
            .ReadBuffer<LongRangeSupportLoad>().ReadBuffer<LongRangeSupportEquation>());
        using var supports = new DeviceContactSupports(backend, device, world, supportBodies, bodies, parameters,
            new(Vector3.UnitY, 1e-5f, 1e-4f, 1e-4f, 1e-4f, .5f, .99f, 1e-6f), 8);
        world.Add(lower, new DeviceSupportBodyBinding(supports)); world.Add(upper, new DeviceSupportBodyBinding(supports));
        var floorPoints = new HashSet<Entity>();
        int row = 0;
        for (int layer = 0; layer < 2; layer++)
            foreach (Vector2 corner in new[] { new Vector2(-1, -1), new Vector2(-1, 1), new Vector2(1, -1), new Vector2(1, 1) })
            {
                Entity firstBody = layer == 0 ? floor : lower, secondBody = layer == 0 ? lower : upper;
                Entity point = world.CreateRelation(firstBody, secondBody, new ContactPoint
                    { Normal = Vector3.UnitY, FirstBodyCount = 1, WarmStart = true, WorldPoint = new(corner.X, layer * 2, corner.Y) }).Entity;
                var first = world.CreateRelation(point, firstBody, new ContactParticipant());
                var second = world.CreateRelation(point, secondBody, new ContactParticipant());
                world.AddBuffer<ContactBodyLink>(point);
                world.ExecuteBufferWrite<ContactBodyLink>(point, buffer =>
                {
                    buffer.Add(new(first, new(corner.X, layer == 0 ? 0 : 1, corner.Y), 1));
                    buffer.Add(new(second, new(corner.X, -1, corner.Y), 1));
                });
                world.Add(point, new ContactMaterial { NormalStiffness = float.PositiveInfinity, StaticFriction = 1, DynamicFriction = .5f });
                world.Add(point, new ContactResponse());
                world.Add(point, new AvbdContactState { NormalPenalty = 100, TangentPenalty = 100, FrictionRegularizationSpeed = .01f });
                world.Add(point, new LongRangeContactSupport()); world.AddBuffer<LongRangeSupportLoad>(point);
                world.Add(point, new ContactEventState<SleepingSupportNotifications>());
                world.Add(point, new DeviceConstraintBinding<DeviceContactConstraint>(parameters, row++));
                world.Add(point, new DeviceAvbdState<DeviceAvbdContactState>(histories));
                world.Add(point, new DeviceAvbdState<DeviceContactSupportState>(supports.ContactStates));
                world.Add(point, new DeviceContactResponseBinding(responses));
                if (layer == 0) floorPoints.Add(point);
            }
        QueryHandle members = world.Query(world.QueryDefinition().Read<DeviceBodyBinding>());
        QueryHandle points = world.Query(world.QueryDefinition().Read<ContactPoint>());
        QueryHandle states = world.Query(world.QueryDefinition().Read<AvbdContactState>()
            .Read<DeviceConstraintBinding<DeviceContactConstraint>>().Read<DeviceAvbdState<DeviceAvbdContactState>>());
        QueryHandle supportRows = world.Query(world.QueryDefinition().Read<LongRangeContactSupport>()
            .Read<DeviceConstraintBinding<DeviceContactConstraint>>().Read<DeviceAvbdState<DeviceContactSupportState>>());
        QueryHandle events = world.Query(world.QueryDefinition().Read<PhysicsStepEvents>().ReadBuffer<PhysicsEvent>());
        using var activities = new DeviceBodyActivities(backend, device, world, members, bodies);
        foreach (Entity body in dynamicBodies) world.Add(body, new DeviceBodyActivityBinding(activities));
        var bindings = new Dictionary<string, ResourceBinding[]>(); activities.AddBindings(bindings);
        using var dynamics = new DeviceBodyDynamics(backend, device, world, members, bodies, CompileActiveBodyDynamics(), 64,
            bindings, activities.GetWriteAccesses(jobs));
        using var bodyInputs = new DeviceBodyInputs(world, members, bodies);
        using var input = new DeviceContactInputs(world, points, parameters, links);
        using var stateInput = new DeviceAvbdStateInputs<AvbdContactState, DeviceContactConstraint, DeviceAvbdContactState, DeviceAvbdContactStateSource>(
            backend, device, world, states, parameters, histories, default);
        using var supportInput = new DeviceAvbdStateInputs<LongRangeContactSupport, DeviceContactConstraint, DeviceContactSupportState, DeviceContactSupportStateSource>(
            backend, device, world, supportRows, parameters, supports.ContactStates, default);
        using var selected = new DeviceConstraintSelection<ContactPoint, DeviceContactConstraint>(backend, device, world, points, parameters, "DeviceContactsSelection");
        using var selectedStates = new DeviceConstraintSelection<ContactPoint, DeviceContactConstraint, DeviceAvbdStateRows<DeviceAvbdContactState>>(
            backend, device, world, points, parameters, new(histories), "DeviceContactAvbdSelection");
        using var selectedSupport = new DeviceConstraintSelection<ContactPoint, DeviceContactConstraint, DeviceAvbdStateRows<DeviceContactSupportState>>(
            backend, device, world, points, parameters, new(supports.ContactStates), "DeviceContactSupportSelection");
        using var selectedResponses = new DeviceConstraintSelection<ContactPoint, DeviceContactConstraint, DeviceContactResponseRows>(
            backend, device, world, points, parameters, new(responses), "DeviceContactResponsesSelection");
        parameters.AddBindings(bindings); links.AddBindings(bindings); histories.AddBindings(bindings);
        responses.AddBindings(bindings); supports.AddBindings(bindings);
        JobResourceAccess[] reads = [.. parameters.GetReadAccesses(jobs), links.GetReadAccess(jobs),
            .. supports.GetReadAccesses(jobs), .. activities.GetReadAccesses(jobs)];
        using var graph = new DeviceConstraintGraph(backend, device, bodies, dynamics, CompileSleepingSupportGraph(), new(12, 2),
            bindings, reads, 64, new IDeviceConstraintSelection[] { selected, selectedSupport, supports });
        using var solve = new DeviceAvbdSolver(backend, device, bodies, equations, graph, CompileSleepingSupportAvbd(), new(1, 1, .5f, 0),
            bindings, reads.Concat(new[] { histories.GetWriteAccess(jobs), responses.GetWriteAccess(jobs) }).Concat(supports.GetWriteAccesses(jobs)).ToArray(),
            64, new IDeviceConstraintSelection[] { selectedStates, selectedResponses });
        using var actions = new DeviceContactActions(backend, device, bodies, graph, responses, CompileSleepingContactActions(), bindings,
            reads, 64, new[] { selectedResponses });
        using var activity = new DevicePhysicsActivity(backend, device, activities, graph, CompileSleepingSupportActivity(),
            new(.05f, .2f, .01f, .01f), bindings, reads, 64);
        using var samples = new DeviceContactSamples(backend, device, bodies, parameters, CompileSleepingContactSamples(), bindings,
            reads.Append(responses.GetReadAccess(jobs)).ToArray(), 64, new IDeviceConstraintSelection[] { selected, selectedResponses });
        using var output = new PhysicsEventBuffer(jobs, 64);
        using var observation = new ContactEvents<SleepingSupportNotifications, DeviceContactEventSource>(world, members, points, output, .001f, new(samples));
        var received = new List<(long Number, PhysicsEvent Event)>();
        using var consumer = new ConsumePhysicsEvents<RecordDeviceActivityEvents>(world, events, new(received),
            [JobResourceAccess.Write(jobs.GetResourceToken(received))]);
        var stages = bodyInputs.Upload().Then(input.Upload()).Then(stateInput.Upload()).Then(supportInput.Upload())
            .Then(supports.InitializeBuffers()).Then(activity.UploadInputs()).Then(dynamics.UpdateSelection()).Then(activity.WakeConnected())
            .Then(dynamics.Predict(new(0, -10, 0))).Then(actions.Initialize()).Then(solve.Solve()).Then(dynamics.UpdateVelocities())
            .Then(activity.Finish()).Then(activity.Readback(output)).Then(samples.Readback()).Then(observation).Then(output.Emit());
        using var frame = PhysicsSimulation.Create(world, QueryDefinition.Empty, stages, 1).CreateFrame(consumer, .1, 0, 0, 0);

        frame.AdvanceTime(.1); frame.CompleteForRead();
        frame.AdvanceTime(.1); frame.CompleteForRead();
        Assert.True(world.Read<BodyActivity>(lower).Sleeping); Assert.True(world.Read<BodyActivity>(upper).Sleeping);
        frame.AdvanceTime(.1); frame.CompleteForRead();

        Assert.True(world.Read<BodyActivity>(lower).Sleeping); Assert.True(world.Read<BodyActivity>(upper).Sleeping);
        Assert.False(world.Read<BodyActivity>(falling).Sleeping);
        for (long number = 0; number < 3; number++)
        {
            var contacts = received.Where(item => item.Number == number && item.Event.Kind == PhysicsEventKind.Contact && floorPoints.Contains(item.Event.Subject)).ToArray();
            Assert.Equal(4, contacts.Length);
            Assert.InRange(contacts.Sum(item => item.Event.Force.Y), 19.98f, 20.02f);
            Assert.All(contacts, item => Assert.Equal(number == 0 ? PhysicsEventPhase.Begin : PhysicsEventPhase.Stay, item.Event.Phase));
        }
        world.ReleaseQuery(events); world.ReleaseQuery(supportRows); world.ReleaseQuery(states); world.ReleaseQuery(points);
        world.ReleaseQuery(members); world.ReleaseQuery(supportBodies);
    }

    private DeviceGraphKernels CompileSleepingSupportGraph() => DeviceGraphTestKernels.CompileParallel(shaderCompiler, "sleeping_support_graph", """
        #include "physics_body_activity_types.slang"
        #define DEVICE_GRAPH_BODY_ACTIVE(body) deviceBodyActivityAwake(body)
        #include "physics_contact_data.slang"
        #include "physics_contact_support_bodies.slang"
        [shader("compute")][numthreads(64,1,1)]
        void WriteRows(uint3 id:SV_DispatchThreadID)
        { DeviceConstraintBodyGroups<DeviceContactBodies,DeviceSupportBodies> rows; writeDeviceConstraintGraphRows(id.x,rows); }
        """, ShaderDirectory());

    private DeviceAvbdKernels CompileSleepingSupportAvbd()
    {
        D3D12TestShader shader = shaderCompiler.CompileWithSearchPaths("sleeping_support_avbd", """
            #include "physics_avbd_contact_support.slang"
            #include "physics_awake_avbd.slang"
            typedef DeviceAvbdConstraintGroups<DeviceSupportedContactAvbdConstraints,DeviceContactSupportAvbdConstraints> SupportRows;
            [shader("compute")][numthreads(64,1,1)]
            void Initialize(uint3 id:SV_DispatchThreadID)
            { DeviceAwakeAvbdConstraints<SupportRows> rows; DeviceAwakeBodies bodies; initializeDeviceAvbd(id.x,rows,bodies); }
            [shader("compute")][numthreads(64,1,1)]
            void UpdateBody(uint3 id:SV_DispatchThreadID)
            { SupportRows rows; updateDeviceAvbdBody(id.x,rows); }
            [shader("compute")][numthreads(64,1,1)]
            void Duals(uint3 id:SV_DispatchThreadID)
            { SupportRows rows; updateDeviceAvbdDuals(id.x,rows); }
            [shader("compute")][numthreads(1,1,1)]
            void InitializeGroups(uint3 id:SV_DispatchThreadID)
            { DeviceAwakeBodies bodies; initializeDeviceContactSupports(AvbdStep,bodies); }
            [shader("compute")][numthreads(1,1,1)]
            void UpdateGroups(uint3 id:SV_DispatchThreadID)
            { DeviceAwakeBodies bodies; updateDeviceContactSupports(AvbdStep,bodies); }
            """, [new("Initialize", SlangStage.Compute), new("UpdateBody", SlangStage.Compute), new("Duals", SlangStage.Compute),
                new("InitializeGroups", SlangStage.Compute), new("UpdateGroups", SlangStage.Compute)], "sm_6_0", ShaderDirectory());
        ComputePipelineDesc Entry(int index) => new(shader.SlangCode, shader.Reflection, shader.GetEntryPoint(index), shader.GetEntryCode(index));
        return new(Entry(0), Entry(1), Entry(2), Entry(3), Entry(4));
    }

    private DeviceActivityKernels CompileSleepingSupportActivity()
    {
        D3D12TestShader shader = shaderCompiler.CompileWithSearchPaths("sleeping_support_activity", """
            #include "physics_contact_support_activity.slang"
            [shader("compute")][numthreads(64,1,1)]
            void ReadRows(uint3 id:SV_DispatchThreadID)
            { DeviceConstraintActivityGroups<DeviceSupportedContactActivity,DeviceSupportBodyActivity> rows; readDeviceActivityRows(id.x,rows); }
            """, [new("ApplyDeviceActivityInputs", SlangStage.Compute), new("ClearDeviceActivitySignals", SlangStage.Compute),
                new("CollectDeviceActivityBodies", SlangStage.Compute), new("ReadRows", SlangStage.Compute),
                new("WakeDeviceBodies", SlangStage.Compute), new("AdvanceDeviceBodyQuietTime", SlangStage.Compute),
                new("FinishDeviceBodyActivity", SlangStage.Compute)], "sm_6_0", ShaderDirectory());
        ComputePipelineDesc Entry(int index) => new(shader.SlangCode, shader.Reflection, shader.GetEntryPoint(index), shader.GetEntryCode(index));
        return new(Entry(0), Entry(1), Entry(2), Entry(3), Entry(4), Entry(5), Entry(6));
    }
}
