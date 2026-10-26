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
    public void ATimeSelectedDeviceWakeOnTheSharedReferenceWakesBothConnectedIslands()
    {
        using var backend = D3D12GraphicsBackend.Create();
        using Device device = CreateDevice(backend);
        using var jobs = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using World world = WorldJobs.CreateWorld(scheduler: jobs);
        using var bodies = new DeviceBodies(backend, device, 4);
        Entity anchor = AddBody(world, bodies, 0, Vector3.Zero, BodyMotion.Static);
        Entity first = AddBody(world, bodies, 1, Vector3.UnitX, BodyMotion.Dynamic);
        Entity second = AddBody(world, bodies, 2, -Vector3.UnitX, BodyMotion.Dynamic);
        Entity other = AddBody(world, bodies, 3, new(10, 0, 0), BodyMotion.Dynamic);
        world.AddBuffer<BodyWake>(anchor);
        using var lengths = new DeviceConstraintData<DeviceLengthConstraint>(backend, device, 2, "DeviceLengths");
        using var multipliers = new DeviceConstraintState<float>(backend, device, 2, "DeviceLengthMultipliers");
        for (int index = 0; index < 2; index++)
        {
            Entity edge = world.CreateRelation(anchor, index == 0 ? first : second, new XpbdLength { RestLength = 1 }).Entity;
            world.Add(edge, new DeviceConstraintBinding<DeviceLengthConstraint>(lengths, index));
            world.Add(edge, new DeviceXpbdState<float>(multipliers));
        }
        QueryHandle members = world.Query(world.QueryDefinition().Read<DeviceBodyBinding>());
        QueryHandle rows = world.Query(world.QueryDefinition().Read<XpbdLength>().Read<UndirectedRelationEndpoints<XpbdLength>>()
            .Read<DeviceConstraintBinding<DeviceLengthConstraint>>().All<DeviceXpbdState<float>>());
        using var activities = new DeviceBodyActivities(backend, device, world, members, bodies);
        foreach (Entity body in new[] { first, second, other })
        { world.Add(body, new BodyActivity { Radius = 1 }); world.Add(body, new DeviceBodyActivityBinding(activities)); }
        var activityBindings = new Dictionary<string, ResourceBinding[]>(); activities.AddBindings(activityBindings);
        using var dynamics = new DeviceBodyDynamics(backend, device, world, members, bodies, CompileActiveBodyDynamics(), 64,
            activityBindings, activities.GetWriteAccesses(jobs));
        using var bodyInputs = new DeviceBodyInputs(world, members, bodies);
        using var output = new PhysicsEventBuffer(jobs, 8);
        using var inputs = lengths.WriteInputs<XpbdLength, DeviceLengthSource>(world, rows, default, 64);
        using var selected = new DeviceConstraintSelection<XpbdLength, DeviceLengthConstraint, DeviceXpbdStateRows<float>>(
            backend, device, world, rows, lengths, new(multipliers), "DeviceLengthsSelection");
        var bindings = new Dictionary<string, ResourceBinding[]>(activityBindings);
        lengths.AddBindings(bindings); multipliers.AddBindings(bindings);
        JobResourceAccess[] reads = [.. lengths.GetReadAccesses(jobs), .. activities.GetReadAccesses(jobs)];
        using var graph = new DeviceConstraintGraph(backend, device, bodies, dynamics, CompileActiveLengthGraph(),
            new(2, 2), bindings, reads, 64, new[] { selected });
        using var solve = new DeviceXpbdSolver(backend, device, bodies, graph, CompileGraphLengthProjection(), new(4, 2),
            bindings, reads, new[] { multipliers.States }, 64);
        using var activity = new DevicePhysicsActivity(backend, device, activities, graph, CompileLengthActivityKernels(),
            new(.05f, .25f, .01f, .01f), bindings, reads, 64);
        var stages = bodyInputs.Upload().Then(lengths.BeginInputs()).Then(inputs).Then(lengths.UploadInputs())
            .Then(activity.UploadInputs()).Then(dynamics.UpdateSelection()).Then(activity.WakeConnected())
            .Then(dynamics.Predict(Vector3.Zero)).Then(solve.Solve()).Then(dynamics.UpdateVelocities())
            .Then(activity.Finish()).Then(activity.Readback(output));
        var physics = PhysicsSimulation.Create(world, QueryDefinition.Empty, stages, 1);
        physics.Schedule(0, 0, .125).Complete(); physics.Schedule(1, .125, .125).Complete();
        Assert.True(world.Read<BodyActivity>(first).Sleeping); Assert.True(world.Read<BodyActivity>(second).Sleeping);
        world.ExecuteBufferWrite<BodyWake>(anchor, static requests => requests.Add(new(.375)));
        physics.Schedule(2, .25, .125).Complete();
        Assert.True(world.Read<BodyActivity>(first).Sleeping); Assert.True(world.Read<BodyActivity>(second).Sleeping);

        physics.Schedule(3, .375, .125).Complete();

        Assert.False(world.Read<BodyActivity>(first).Sleeping); Assert.False(world.Read<BodyActivity>(second).Sleeping);
        Assert.True(world.Read<BodyActivity>(other).Sleeping);
        world.ReleaseQuery(rows); world.ReleaseQuery(members);
    }
    private DeviceGraphKernels CompileActiveLengthGraph() => DeviceGraphTestKernels.CompileParallel(shaderCompiler, "active_length_graph", """
        #include "physics_body_activity_types.slang"
        #define DEVICE_GRAPH_BODY_ACTIVE(body) deviceBodyActivityAwake(body)
        #include "physics_length_graph.slang"
        [shader("compute")][numthreads(64,1,1)]
        void WriteRows(uint3 id:SV_DispatchThreadID) { DeviceLengthBodies rows; writeDeviceConstraintGraphRows(id.x,rows); }
        """, ShaderDirectory());
    private DeviceActivityKernels CompileLengthActivityKernels()
    {
        D3D12TestShader shader = shaderCompiler.CompileWithSearchPaths("length_activity", """
            #include "physics_length_activity.slang"
            [shader("compute")][numthreads(64,1,1)]
            void ReadRows(uint3 id:SV_DispatchThreadID) { DeviceLengthActivity rows; readDeviceActivityRows(id.x,rows); }
            """, [new("ApplyDeviceActivityInputs", SlangStage.Compute), new("ClearDeviceActivitySignals", SlangStage.Compute),
                new("CollectDeviceActivityBodies", SlangStage.Compute), new("ReadRows", SlangStage.Compute),
                new("WakeDeviceBodies", SlangStage.Compute), new("AdvanceDeviceBodyQuietTime", SlangStage.Compute),
                new("FinishDeviceBodyActivity", SlangStage.Compute)], "sm_6_0", ShaderDirectory());
        ComputePipelineDesc Entry(int index) => new(shader.SlangCode, shader.Reflection, shader.GetEntryPoint(index), shader.GetEntryCode(index));
        return new(Entry(0), Entry(1), Entry(2), Entry(3), Entry(4), Entry(5), Entry(6));
    }
}
