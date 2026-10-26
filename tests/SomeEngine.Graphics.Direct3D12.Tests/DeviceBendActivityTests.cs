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
    [Fact]
    public void ADeviceBendWithAngularErrorKeepsItsSlowMovingBodyAwake()
    {
        using var backend = D3D12GraphicsBackend.Create();
        using Device device = CreateDevice(backend);
        using var jobs = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using World world = WorldJobs.CreateWorld(scheduler: jobs);
        using var bodies = new DeviceBodies(backend, device, 4);
        Entity first = AddBody(world, bodies, 0, -Vector3.UnitX, BodyMotion.Static);
        Entity middle = AddBody(world, bodies, 1, Vector3.Zero, BodyMotion.Static);
        Entity last = AddBody(world, bodies, 2, Vector3.UnitY, BodyMotion.Dynamic);
        Entity other = AddBody(world, bodies, 3, new(10, 0, 0), BodyMotion.Dynamic);
        using var parameters = new DeviceConstraintData<DeviceBendConstraint>(backend, device, 1, "DeviceBends");
        using var multipliers = new DeviceConstraintState<float>(backend, device, 1, "DeviceBendMultipliers");
        Entity bend = ConstraintBodies.Create(world, new XpbdBend { MinimumCosine = .9f, Compliance = 1000 }, [first, middle, last]);
        world.Add(bend, new DeviceConstraintBinding<DeviceBendConstraint>(parameters, 0));
        world.Add(bend, new DeviceXpbdState<float>(multipliers));
        QueryHandle members = world.Query(world.QueryDefinition().Read<DeviceBodyBinding>());
        QueryHandle rows = world.Query(ConstraintBodies.AddReads(world.QueryDefinition().Read<XpbdBend>().Build()));
        using var activities = new DeviceBodyActivities(backend, device, world, members, bodies);
        foreach (Entity body in new[] { last, other })
        { world.Add(body, new BodyActivity { Radius = 1 }); world.Add(body, new DeviceBodyActivityBinding(activities)); }
        var bindings = new Dictionary<string, ResourceBinding[]>(); activities.AddBindings(bindings);
        using var dynamics = new DeviceBodyDynamics(backend, device, world, members, bodies, CompileActiveBodyDynamics(), 64,
            bindings, activities.GetWriteAccesses(jobs));
        using var bodyInputs = new DeviceBodyInputs(world, members, bodies);
        using var results = new DeviceBodyResults(backend, device, bodies);
        using var write = parameters.WriteInputs<XpbdBend, DeviceBendSource>(world, rows, default, 64);
        using var selected = new DeviceConstraintSelection<XpbdBend, DeviceBendConstraint, DeviceXpbdStateRows<float>>(
            backend, device, world, rows, parameters, new(multipliers), "DeviceBendsSelection");
        parameters.AddBindings(bindings); multipliers.AddBindings(bindings);
        JobResourceAccess[] reads = [.. parameters.GetReadAccesses(jobs), .. activities.GetReadAccesses(jobs)];
        using var graph = new DeviceConstraintGraph(backend, device, bodies, dynamics, CompileBendActivityGraph(), new(1, 3),
            bindings, reads, 64, new[] { selected });
        using var solve = new DeviceXpbdSolver(backend, device, bodies, graph, CompileBendActivityProjection(), new(1, 1),
            bindings, reads, new[] { multipliers.States }, 64);
        using var activity = new DevicePhysicsActivity(backend, device, activities, graph, CompileBendActivity(),
            new(.05f, .2f, .01f, 1), bindings, reads, 64);
        using var output = new PhysicsEventBuffer(jobs, 8);
        var stages = bodyInputs.Upload().Then(parameters.BeginInputs()).Then(write).Then(parameters.UploadInputs())
            .Then(activity.UploadInputs()).Then(dynamics.UpdateSelection()).Then(activity.WakeConnected())
            .Then(dynamics.Predict(Vector3.Zero)).Then(solve.Solve()).Then(dynamics.UpdateVelocities())
            .Then(activity.Finish()).Then(activity.Readback(output)).Then(results.Readback(64));
        var simulation = PhysicsSimulation.Create(world, QueryDefinition.Empty, stages, 1);

        simulation.Schedule(0, 0, .1).Complete();
        simulation.Schedule(1, .1, .1).Complete();
        simulation.Schedule(2, .2, .1).Complete();

        Vector3 position = world.Read<BodyPose>(last).Position;
        Assert.InRange(position.X, .00001f, .001f);
        Assert.InRange(world.Read<BodyVelocity>(last).Linear.Length(), .00001f, .01f);
        Assert.True(MathF.Acos(Vector3.Dot(Vector3.UnitX, Vector3.Normalize(position))) > 1.5f);
        Assert.False(world.Read<BodyActivity>(last).Sleeping);
        Assert.True(world.Read<BodyActivity>(other).Sleeping);
        world.ReleaseQuery(rows); world.ReleaseQuery(members);
    }

    private DeviceGraphKernels CompileBendActivityGraph() => DeviceGraphTestKernels.CompileParallel(shaderCompiler, "bend_activity_graph", """
        #include "physics_body_activity_types.slang"
        #define DEVICE_GRAPH_BODY_ACTIVE(body) deviceBodyActivityAwake(body)
        #include "physics_bend_graph.slang"
        [shader("compute")][numthreads(64,1,1)]
        void WriteRows(uint3 id:SV_DispatchThreadID)
        { DeviceBendBodies rows; writeDeviceConstraintGraphRows(id.x,rows); }
        """, ShaderDirectory());

    private DeviceXpbdKernels CompileBendActivityProjection()
    {
        D3D12TestShader shader = shaderCompiler.CompileWithSearchPaths("bend_activity_projection", """
            #include "physics_xpbd_graph.slang"
            #include "physics_xpbd_bend.slang"
            #include "physics_bend_graph.slang"
            [shader("compute")][numthreads(64,1,1)]
            void Initialize(uint3 group:SV_GroupID,uint3 thread:SV_GroupThreadID)
            { DeviceXpbdConstraintRows<DeviceBendBodies,DeviceXpbdBendProjection<DeviceGraphBodies>> rows; initializeDeviceXpbdGraph(group.x,thread.x,rows); }
            [shader("compute")][numthreads(64,1,1)]
            void Project(uint3 group:SV_GroupID,uint3 thread:SV_GroupThreadID)
            { DeviceXpbdConstraintRows<DeviceBendBodies,DeviceXpbdBendProjection<DeviceGraphBodies>> rows; projectDeviceXpbdGraph(group.x,thread.x,rows); }
            """, [new("Initialize", SlangStage.Compute), new("Project", SlangStage.Compute)], "sm_6_0", ShaderDirectory());
        ComputePipelineDesc Entry(int index) => new(shader.SlangCode, shader.Reflection, shader.GetEntryPoint(index), shader.GetEntryCode(index));
        return new(Entry(0), Entry(1));
    }

    private DeviceActivityKernels CompileBendActivity()
    {
        D3D12TestShader shader = shaderCompiler.CompileWithSearchPaths("bend_activity", """
            #include "physics_bend_activity.slang"
            [shader("compute")][numthreads(64,1,1)]
            void ReadRows(uint3 id:SV_DispatchThreadID)
            { DeviceBendActivity rows; readDeviceActivityRows(id.x,rows); }
            """, [new("ApplyDeviceActivityInputs", SlangStage.Compute), new("ClearDeviceActivitySignals", SlangStage.Compute),
                new("CollectDeviceActivityBodies", SlangStage.Compute), new("ReadRows", SlangStage.Compute),
                new("WakeDeviceBodies", SlangStage.Compute), new("AdvanceDeviceBodyQuietTime", SlangStage.Compute),
                new("FinishDeviceBodyActivity", SlangStage.Compute)], "sm_6_0", ShaderDirectory());
        ComputePipelineDesc Entry(int index) => new(shader.SlangCode, shader.Reflection, shader.GetEntryPoint(index), shader.GetEntryCode(index));
        return new(Entry(0), Entry(1), Entry(2), Entry(3), Entry(4), Entry(5), Entry(6));
    }
}
