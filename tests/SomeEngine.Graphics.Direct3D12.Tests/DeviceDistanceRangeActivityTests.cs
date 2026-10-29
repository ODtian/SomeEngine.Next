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
    public void ADeviceDistanceRangeWithLinearErrorKeepsItsSlowMovingBodyAwake()
    {
        using var backend = D3D12GraphicsBackend.Create();
        using Device device = CreateDevice(backend);
        using var jobs = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using World world = WorldJobs.CreateWorld(scheduler: jobs);
        using var bodies = new DeviceBodies(backend, device, 3);
        using var equations = new DeviceAvbdBodyEquations(backend, device, bodies);
        Entity anchor = AddBody(world, bodies, 0, Vector3.Zero, BodyMotion.Static);
        Entity moving = AddBody(world, bodies, 1, new(3, 0, 0), BodyMotion.Dynamic);
        Entity other = AddBody(world, bodies, 2, new(10, 0, 0), BodyMotion.Dynamic);
        world.Add(moving, new DeviceAvbdEquationBinding(equations, 1));
        world.Add(other, new DeviceAvbdEquationBinding(equations, 2));
        using var parameters = new DeviceConstraintData<DeviceDistanceRangeConstraint>(backend, device, 1, "DeviceDistanceRanges");
        using var histories = new DeviceConstraintState<DeviceAvbdDistanceRangeState>(backend, device, 1, "DeviceAvbdDistanceRangeStates");
        Entity range = world.CreateRelation(anchor, moving, new DistanceRange
            { Minimum = 1, Maximum = 2, Stiffness = .0001f }).Entity;
        world.Add(range, new AvbdDistanceRangeState { Penalty = 1 });
        world.Add(range, new DeviceConstraintBinding<DeviceDistanceRangeConstraint>(parameters, 0));
        world.Add(range, new DeviceAvbdState<DeviceAvbdDistanceRangeState>(histories));
        QueryHandle members = world.Query(world.QueryDefinition().Read<DeviceBodyBinding>());
        QueryHandle rows = world.Query(world.QueryDefinition().Read<DistanceRange>().Read<UndirectedRelationEndpoints<DistanceRange>>());
        QueryHandle states = world.Query(world.QueryDefinition().Read<AvbdDistanceRangeState>()
            .Read<DeviceConstraintBinding<DeviceDistanceRangeConstraint>>().Read<DeviceAvbdState<DeviceAvbdDistanceRangeState>>());
        using var activities = new DeviceBodyActivities(backend, device, world, members, bodies);
        foreach (Entity body in new[] { moving, other })
        { world.Add(body, new BodyActivity { Radius = 1 }); world.Add(body, new DeviceBodyActivityBinding(activities)); }
        var bindings = new Dictionary<string, ResourceBinding[]>(); activities.AddBindings(bindings);
        using var dynamics = new DeviceBodyDynamics(backend, device, world, members, bodies, CompileActiveBodyDynamics(), 64,
            bindings, activities.GetWriteAccesses(jobs));
        using var bodyInputs = new DeviceBodyInputs(world, members, bodies);
        using var results = new DeviceBodyResults(backend, device, bodies);
        using var write = parameters.WriteInputs<DistanceRange, DeviceDistanceRangeSource>(world, rows, default, 64);
        using var stateInput = new DeviceAvbdStateInputs<AvbdDistanceRangeState, DeviceDistanceRangeConstraint,
            DeviceAvbdDistanceRangeState, DeviceAvbdDistanceRangeStateSource>(backend, device, world, states, parameters, histories, default);
        using var selected = new DeviceConstraintSelection<DistanceRange, DeviceDistanceRangeConstraint>(
            backend, device, world, rows, parameters, "DeviceDistanceRangesSelection");
        using var selectedStates = new DeviceConstraintSelection<DistanceRange, DeviceDistanceRangeConstraint, DeviceAvbdStateRows<DeviceAvbdDistanceRangeState>>(
            backend, device, world, rows, parameters, new(histories), "DeviceDistanceRangeAvbdSelection");
        parameters.AddBindings(bindings); histories.AddBindings(bindings);
        JobResourceAccess[] reads = [.. parameters.GetReadAccesses(jobs), .. activities.GetReadAccesses(jobs)];
        using var graph = new DeviceConstraintGraph(backend, device, bodies, dynamics, CompileDistanceRangeActivityGraph(), new(1, 2),
            bindings, reads, 64, new[] { selected });
        using var solve = new DeviceAvbdSolver(backend, device, bodies, equations, graph, CompileDistanceRangeActivityAvbd(), new(8, 1, 0, 0),
            bindings, reads.Append(histories.GetWriteAccess(jobs)).ToArray(), 64, new[] { selectedStates });
        using var activity = new DevicePhysicsActivity(backend, device, activities, graph, CompileDistanceRangeActivity(),
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

        Assert.InRange(world.Read<BodyPose>(moving).Position.X, 2.99998f, 2.999999f);
        Assert.InRange(world.Read<BodyVelocity>(moving).Linear.X, -.0001f, -.000001f);
        Assert.False(world.Read<BodyActivity>(moving).Sleeping);
        Assert.True(world.Read<BodyActivity>(other).Sleeping);
        world.ReleaseQuery(states); world.ReleaseQuery(rows); world.ReleaseQuery(members);
    }

    private DeviceGraphKernels CompileDistanceRangeActivityGraph() => DeviceGraphTestKernels.CompileParallel(shaderCompiler, "distance_range_activity_graph", """
        #include "physics_body_activity_types.slang"
        #define DEVICE_GRAPH_BODY_ACTIVE(body) deviceBodyActivityAwake(body)
        #include "physics_distance_range_bodies.slang"
        [shader("compute")][numthreads(64,1,1)]
        void WriteRows(uint3 id:SV_DispatchThreadID)
        { DeviceDistanceRangeBodies rows; writeDeviceConstraintGraphRows(id.x,rows); }
        """, ShaderDirectory());

    private DeviceAvbdKernels CompileDistanceRangeActivityAvbd()
    {
        D3D12TestShader shader = shaderCompiler.CompileWithSearchPaths("distance_range_activity_avbd", """
            #include "physics_avbd_distance_range.slang"
            #include "physics_awake_avbd.slang"
            [shader("compute")][numthreads(64,1,1)]
            void Initialize(uint3 id:SV_DispatchThreadID)
            { DeviceAwakeAvbdConstraints<DeviceDistanceRangeAvbdConstraints> rows; DeviceAwakeBodies bodies; initializeDeviceAvbd(id.x,rows,bodies); }
            [shader("compute")][numthreads(64,1,1)]
            void Solve(uint3 id:SV_DispatchThreadID)
            { DeviceDistanceRangeAvbdConstraints rows; updateDeviceAvbdBody(id.x,rows); }
            [shader("compute")][numthreads(64,1,1)]
            void Duals(uint3 id:SV_DispatchThreadID)
            { DeviceDistanceRangeAvbdConstraints rows; updateDeviceAvbdDuals(id.x,rows); }
            """, [new("Initialize", SlangStage.Compute), new("Solve", SlangStage.Compute), new("Duals", SlangStage.Compute)], "sm_6_0", ShaderDirectory());
        ComputePipelineDesc Entry(int index) => new(shader.SlangCode, shader.Reflection, shader.GetEntryPoint(index), shader.GetEntryCode(index));
        return new(Entry(0), Entry(1), Entry(2));
    }

    private DeviceActivityKernels CompileDistanceRangeActivity()
    {
        D3D12TestShader shader = shaderCompiler.CompileWithSearchPaths("distance_range_activity", """
            #include "physics_distance_range_activity.slang"
            [shader("compute")][numthreads(64,1,1)]
            void ReadRows(uint3 id:SV_DispatchThreadID)
            { DeviceDistanceRangeActivity rows; readDeviceActivityRows(id.x,rows); }
            """, [new("ApplyDeviceActivityInputs", SlangStage.Compute), new("ClearDeviceActivitySignals", SlangStage.Compute),
                new("CollectDeviceActivityBodies", SlangStage.Compute), new("ReadRows", SlangStage.Compute),
                new("WakeDeviceBodies", SlangStage.Compute), new("AdvanceDeviceBodyQuietTime", SlangStage.Compute),
                new("FinishDeviceBodyActivity", SlangStage.Compute)], "sm_6_0", ShaderDirectory());
        ComputePipelineDesc Entry(int index) => new(shader.SlangCode, shader.Reflection, shader.GetEntryPoint(index), shader.GetEntryCode(index));
        return new(Entry(0), Entry(1), Entry(2), Entry(3), Entry(4), Entry(5), Entry(6));
    }
}
