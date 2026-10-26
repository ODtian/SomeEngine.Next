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
    public void TheFrameConsumesDeviceSleepAndLocalWakeWhileAnotherIslandRemainsAsleep()
    {
        using var backend = D3D12GraphicsBackend.Create();
        using Device device = CreateDevice(backend);
        using var jobs = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using World world = WorldJobs.CreateWorld(scheduler: jobs);
        using var bodies = new DeviceBodies(backend, device, 4);
        Entity first = AddBody(world, bodies, 1, Vector3.Zero, BodyMotion.Dynamic);
        Entity other = AddBody(world, bodies, 3, new(10, 0, 0), BodyMotion.Dynamic);
        world.Add(first, new BodyActivity { Radius = 1 }); world.Add(other, new BodyActivity { Radius = 1 });
        world.AddBuffer<BodyWake>(first);
        QueryHandle members = world.Query(world.QueryDefinition().Read<DeviceBodyBinding>());
        QueryHandle events = world.Query(new QueryDefinitionBuilder().Read<PhysicsStepEvents>().ReadBuffer<PhysicsEvent>());
        using var activities = new DeviceBodyActivities(backend, device, world, members, bodies);
        world.Add(first, new DeviceBodyActivityBinding(activities)); world.Add(other, new DeviceBodyActivityBinding(activities));
        var bindings = new Dictionary<string, ResourceBinding[]>(); activities.AddBindings(bindings);
        using var dynamics = new DeviceBodyDynamics(backend, device, world, members, bodies, CompileActiveBodyDynamics(), 64,
            bindings, activities.GetWriteAccesses(jobs));
        using var results = new DeviceBodyResults(backend, device, bodies);
        using var bodyInputs = new DeviceBodyInputs(world, members, bodies);
        using var output = new PhysicsEventBuffer(jobs, 8);
        using var graph = new DeviceConstraintGraph(backend, device, bodies, dynamics, CompileEmptyActivityGraph(),
            new(1, 1), bindings, activities.GetReadAccesses(jobs), 64);
        using var activity = new DevicePhysicsActivity(backend, device, activities, graph, CompileEmptyActivityKernels(),
            new(.05f, .1f, .01f, .01f), bindings, default, 64);
        var received = new List<(long Number, PhysicsEvent Event)>();
        using var consumer = new ConsumePhysicsEvents<RecordDeviceActivityEvents>(world, events, new(received),
            [JobResourceAccess.Write(jobs.GetResourceToken(received))]);
        var stages = bodyInputs.Upload().Then(activity.UploadInputs()).Then(dynamics.UpdateSelection()).Then(activity.WakeConnected())
            .Then(dynamics.Predict(Vector3.Zero)).Then(dynamics.UpdateVelocities()).Then(activity.Finish())
            .Then(activity.Readback(output)).Then(results.Readback(64)).Then(output.Emit());
        var physics = PhysicsSimulation.Create(world, QueryDefinition.Empty, stages, 2);
        using var frame = physics.CreateFrame(consumer, .1, 1, 0, 0);

        frame.AdvanceTime(.31); frame.CompleteForRead();

        Assert.True(world.Read<BodyActivity>(first).Sleeping);
        Assert.True(world.Read<BodyActivity>(other).Sleeping);
        Assert.Equal(2, received.Count);
        Assert.All(received, item => { Assert.Equal(0, item.Number); Assert.Equal(PhysicsEventKind.Sleep, item.Event.Kind); Assert.Equal(.1, item.Event.Time); });
        double wakeTime = .3;
        world.ExecuteBufferWrite<BodyWake>(first, requests => requests.Add(new(wakeTime)));
        world.Replace(first, new BodyVelocity { Linear = Vector3.UnitX });

        frame.AdvanceTime(.1); frame.CompleteForRead();

        Assert.False(world.Read<BodyActivity>(first).Sleeping);
        Assert.True(world.Read<BodyActivity>(other).Sleeping);
        Near(new(.1f, 0, 0), world.Read<BodyPose>(first).Position);
        Near(new(10, 0, 0), world.Read<BodyPose>(other).Position);
        Assert.Equal(3, world.Read<BodyStepState>(other).Number);
        Assert.Equal(.4, world.Read<BodyStepState>(other).Time, 10);
        var wake = received.Single(item => item.Event.Kind == PhysicsEventKind.Wake);
        Assert.Equal(first, wake.Event.Subject); Assert.Equal(3, wake.Number); Assert.Equal(wakeTime, wake.Event.Time, 10);
        world.ReleaseQuery(events); world.ReleaseQuery(members);
    }

    private DeviceBodyKernels CompileActiveBodyDynamics()
    {
        D3D12TestShader shader = shaderCompiler.CompileWithSearchPaths("active_body_dynamics", "#include \"physics_body_activity_dynamics.slang\"",
            [new("PredictDeviceActiveBodies", SlangStage.Compute), new("UpdateDeviceBodyVelocities", SlangStage.Compute),
                new("ResumeDeviceActiveBodies", SlangStage.Compute)], "sm_6_0", ShaderDirectory());
        ComputePipelineDesc Entry(int index) => new(shader.SlangCode, shader.Reflection, shader.GetEntryPoint(index), shader.GetEntryCode(index));
        return new(Entry(0), Entry(1), Resume: Entry(2));
    }
    private DeviceGraphKernels CompileEmptyActivityGraph() => DeviceGraphTestKernels.CompileParallel(shaderCompiler, "empty_activity_graph", """
        #include "physics_body_activity_types.slang"
        #define DEVICE_GRAPH_BODY_ACTIVE(body) deviceBodyActivityAwake(body)
        #include "physics_constraint_graph.slang"
        [shader("compute")][numthreads(64,1,1)]
        void WriteRows(uint3 id:SV_DispatchThreadID) { DeviceNoConstraintBodies rows; writeDeviceConstraintGraphRows(id.x,rows); }
        """, ShaderDirectory());

    private DeviceActivityKernels CompileEmptyActivityKernels()
    {
        D3D12TestShader shader = shaderCompiler.CompileWithSearchPaths("empty_activity", """
            #include "physics_body_activity.slang"
            [shader("compute")][numthreads(64,1,1)]
            void ReadRows(uint3 id:SV_DispatchThreadID) { DeviceNoConstraintActivity rows; readDeviceActivityRows(id.x,rows); }
            """, [new("ApplyDeviceActivityInputs", SlangStage.Compute), new("ClearDeviceActivitySignals", SlangStage.Compute),
                new("CollectDeviceActivityBodies", SlangStage.Compute), new("ReadRows", SlangStage.Compute),
                new("WakeDeviceBodies", SlangStage.Compute), new("AdvanceDeviceBodyQuietTime", SlangStage.Compute),
                new("FinishDeviceBodyActivity", SlangStage.Compute)], "sm_6_0", ShaderDirectory());
        ComputePipelineDesc Entry(int index) => new(shader.SlangCode, shader.Reflection, shader.GetEntryPoint(index), shader.GetEntryCode(index));
        return new(Entry(0), Entry(1), Entry(2), Entry(3), Entry(4), Entry(5), Entry(6));
    }
    private readonly struct RecordDeviceActivityEvents(List<(long Number, PhysicsEvent Event)> values) : IPhysicsEventConsumer
    {
        public void Consume(scoped in PhysicsStepEvents step, scoped ReadOnlySpan<PhysicsEvent> events, ref JobCommandWriter commands)
        { foreach (PhysicsEvent value in events) values.Add((step.Number, value)); }
    }
}
