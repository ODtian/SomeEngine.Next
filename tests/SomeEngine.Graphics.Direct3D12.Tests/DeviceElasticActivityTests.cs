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
    [Fact]
    public void ADeviceElasticConnectionWithDistanceErrorKeepsItsSlowMovingBodyAwake()
    {
        using var backend = D3D12GraphicsBackend.Create();
        using Device device = CreateDevice(backend);
        using var jobs = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using World world = WorldJobs.CreateWorld(scheduler: jobs);
        using var bodies = new DeviceBodies(backend, device, 3);
        using var equations = new DeviceAvbdBodyEquations(backend, device, bodies);
        Entity anchor = AddBody(world, bodies, 0, Vector3.Zero, BodyMotion.Static);
        Entity moving = AddBody(world, bodies, 1, Vector3.UnitX, BodyMotion.Dynamic);
        Entity other = AddBody(world, bodies, 2, new(10, 0, 0), BodyMotion.Dynamic);
        world.Add(moving, new DeviceAvbdEquationBinding(equations, 1));
        world.Add(other, new DeviceAvbdEquationBinding(equations, 2));
        using var parameters = new DeviceConstraintData<DeviceElasticConstraint>(backend, device, 1, "DeviceElasticConstraints");
        using var histories = new DeviceConstraintState<DeviceAvbdElasticState>(backend, device, 1, "DeviceAvbdElasticStates");
        Entity connection = world.CreateRelation(anchor, moving, new ElasticConnection
            { RestRotation = Quaternion.Identity, LinearStiffness = .0001f }).Entity;
        world.Add(connection, new AvbdElasticState { LinearPenalty = 1 });
        world.Add(connection, new DeviceConstraintBinding<DeviceElasticConstraint>(parameters, 0));
        world.Add(connection, new DeviceAvbdState<DeviceAvbdElasticState>(histories));
        QueryHandle members = world.Query(world.QueryDefinition().Read<DeviceBodyBinding>());
        QueryHandle rows = world.Query(world.QueryDefinition().Read<ElasticConnection>().Read<UndirectedRelationEndpoints<ElasticConnection>>());
        QueryHandle states = world.Query(world.QueryDefinition().Read<AvbdElasticState>()
            .Read<DeviceConstraintBinding<DeviceElasticConstraint>>().Read<DeviceAvbdState<DeviceAvbdElasticState>>());
        using var activities = new DeviceBodyActivities(backend, device, world, members, bodies);
        foreach (Entity body in new[] { moving, other })
        { world.Add(body, new BodyActivity { Radius = 1 }); world.Add(body, new DeviceBodyActivityBinding(activities)); }
        var bindings = new Dictionary<string, ResourceBinding[]>(); activities.AddBindings(bindings);
        using var dynamics = new DeviceBodyDynamics(backend, device, world, members, bodies, CompileActiveBodyDynamics(), 64,
            bindings, activities.GetWriteAccesses(jobs));
        using var bodyInputs = new DeviceBodyInputs(world, members, bodies);
        using var results = new DeviceBodyResults(backend, device, bodies);
        using var write = parameters.WriteInputs<ElasticConnection, DeviceElasticSource>(world, rows, default, 64);
        using var stateInput = new DeviceAvbdStateInputs<AvbdElasticState, DeviceElasticConstraint, DeviceAvbdElasticState, DeviceAvbdElasticStateSource>(
            backend, device, world, states, parameters, histories, default);
        using var selected = new DeviceConstraintSelection<ElasticConnection, DeviceElasticConstraint>(
            backend, device, world, rows, parameters, "DeviceElasticConstraintsSelection");
        using var selectedStates = new DeviceConstraintSelection<ElasticConnection, DeviceElasticConstraint, DeviceAvbdStateRows<DeviceAvbdElasticState>>(
            backend, device, world, rows, parameters, new(histories), "DeviceElasticAvbdSelection");
        parameters.AddBindings(bindings); histories.AddBindings(bindings);
        JobResourceAccess[] reads = [.. parameters.GetReadAccesses(jobs), .. activities.GetReadAccesses(jobs)];
        using var graph = new DeviceConstraintGraph(backend, device, bodies, dynamics, CompileElasticActivityGraph(), new(1, 2),
            bindings, reads, 64, new[] { selected });
        using var solve = new DeviceAvbdSolver(backend, device, bodies, equations, graph, CompileElasticActivityAvbd(), new(8, 1, 0, 0),
            bindings, reads.Append(histories.GetWriteAccess(jobs)).ToArray(), 64, new[] { selectedStates });
        using var activity = new DevicePhysicsActivity(backend, device, activities, graph, CompileElasticActivity(),
            new(.05f, .2f, .01f, .01f), bindings, reads, 64);
        using var output = new PhysicsEventBuffer(jobs, 8);
        var stages = bodyInputs.Upload().Then(parameters.BeginInputs()).Then(write).Then(parameters.UploadInputs()).Then(stateInput.Upload())
            .Then(activity.UploadInputs()).Then(dynamics.UpdateSelection()).Then(activity.WakeConnected())
            .Then(dynamics.Predict(Vector3.Zero)).Then(solve.Solve()).Then(dynamics.UpdateVelocities())
            .Then(activity.Finish()).Then(activity.Readback(output)).Then(results.Readback(64));
        var simulation = PhysicsSimulation.Create(world, QueryDefinition.Empty, stages, 1);

        simulation.Schedule(0, 0, .1).Complete();
        simulation.Schedule(1, .1, .1).Complete();
        simulation.Schedule(2, .2, .1).Complete();

        Assert.InRange(world.Read<BodyPose>(moving).Position.X, .99998f, .999999f);
        Assert.InRange(world.Read<BodyVelocity>(moving).Linear.X, -.0001f, -.000001f);
        Assert.False(world.Read<BodyActivity>(moving).Sleeping);
        Assert.True(world.Read<BodyActivity>(other).Sleeping);
        world.ReleaseQuery(states); world.ReleaseQuery(rows); world.ReleaseQuery(members);
    }

    private DeviceGraphKernels CompileElasticActivityGraph() => DeviceGraphTestKernels.CompileParallel(shaderCompiler, "elastic_activity_graph", """
        #include "physics_body_activity_types.slang"
        #define DEVICE_GRAPH_BODY_ACTIVE(body) deviceBodyActivityAwake(body)
        #include "physics_elastic_data.slang"
        [shader("compute")][numthreads(64,1,1)]
        void WriteRows(uint3 id:SV_DispatchThreadID)
        { DeviceElasticBodies rows; writeDeviceConstraintGraphRows(id.x,rows); }
        """, ShaderDirectory());

    private DeviceAvbdKernels CompileElasticActivityAvbd()
    {
        D3D12TestShader shader = shaderCompiler.CompileWithSearchPaths("elastic_activity_avbd", """
            #include "physics_avbd_elastic.slang"
            #include "physics_awake_avbd.slang"
            [shader("compute")][numthreads(64,1,1)]
            void Initialize(uint3 id:SV_DispatchThreadID)
            { DeviceAwakeAvbdConstraints<DeviceElasticAvbdConstraints> rows; DeviceAwakeBodies bodies; initializeDeviceAvbd(id.x,rows,bodies); }
            [shader("compute")][numthreads(64,1,1)]
            void Solve(uint3 id:SV_DispatchThreadID)
            { DeviceElasticAvbdConstraints rows; updateDeviceAvbdBody(id.x,rows); }
            [shader("compute")][numthreads(64,1,1)]
            void Duals(uint3 id:SV_DispatchThreadID)
            { DeviceElasticAvbdConstraints rows; updateDeviceAvbdDuals(id.x,rows); }
            """, [new("Initialize", SlangStage.Compute), new("Solve", SlangStage.Compute), new("Duals", SlangStage.Compute)], "sm_6_0", ShaderDirectory());
        ComputePipelineDesc Entry(int index) => new(shader.SlangCode, shader.Reflection, shader.GetEntryPoint(index), shader.GetEntryCode(index));
        return new(Entry(0), Entry(1), Entry(2));
    }

    private DeviceActivityKernels CompileElasticActivity()
    {
        D3D12TestShader shader = shaderCompiler.CompileWithSearchPaths("elastic_activity", """
            #include "physics_elastic_activity.slang"
            [shader("compute")][numthreads(64,1,1)]
            void ReadRows(uint3 id:SV_DispatchThreadID)
            { DeviceElasticActivity rows; readDeviceActivityRows(id.x,rows); }
            """, [new("ApplyDeviceActivityInputs", SlangStage.Compute), new("ClearDeviceActivitySignals", SlangStage.Compute),
                new("CollectDeviceActivityBodies", SlangStage.Compute), new("ReadRows", SlangStage.Compute),
                new("WakeDeviceBodies", SlangStage.Compute), new("AdvanceDeviceBodyQuietTime", SlangStage.Compute),
                new("FinishDeviceBodyActivity", SlangStage.Compute)], "sm_6_0", ShaderDirectory());
        ComputePipelineDesc Entry(int index) => new(shader.SlangCode, shader.Reflection, shader.GetEntryPoint(index), shader.GetEntryCode(index));
        return new(Entry(0), Entry(1), Entry(2), Entry(3), Entry(4), Entry(5), Entry(6));
    }
}
