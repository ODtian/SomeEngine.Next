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
    public void DeviceJointBoundsKeepTheirReferencedChainAwakeBelowTheSpeedThreshold()
    {
        using var backend = D3D12GraphicsBackend.Create();
        using Device device = CreateDevice(backend);
        using var jobs = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using World world = WorldJobs.CreateWorld(scheduler: jobs);
        using var bodies = new DeviceBodies(backend, device, 8);
        using var frames = new DeviceConstraintData<DeviceJointConstraint>(backend, device, 6, "DeviceJoints");
        using var sets = new DeviceConstraintData<DeviceJointBoundSet>(backend, device, 6, "DeviceJointBoundSets");
        Entity previous = AddBody(world, bodies, 0, Vector3.Zero, BodyMotion.Static), tip = default;
        var jointEntities = new List<Entity>();
        var dynamicBodies = new List<Entity>();
        for (int index = 0; index < 6; index++)
        {
            tip = AddBody(world, bodies, index + 1, new(0, -2 * index - 1, 0), BodyMotion.Dynamic);
            dynamicBodies.Add(tip);
            Entity joint = world.CreateRelation(previous, tip, JointConstraints.CreateBall(
                new(index == 0 ? Vector3.Zero : -Vector3.UnitY, Quaternion.Identity),
                new(Vector3.UnitY, Quaternion.Identity), float.PositiveInfinity)).Entity;
            world.AddBuffer<LongRangeJointBound>(joint);
            world.Add(joint, new DeviceConstraintBinding<DeviceJointConstraint>(frames, index));
            world.Add(joint, new DeviceConstraintBinding<DeviceJointBoundSet>(sets, index));
            jointEntities.Add(joint); previous = tip;
        }
        Entity other = AddBody(world, bodies, 7, new(10, 0, 0), BodyMotion.Dynamic);
        dynamicBodies.Add(other);
        world.Replace(tip, new BodyVelocity { Linear = new(0, -.001f, 0) });
        QueryHandle members = world.Query(world.QueryDefinition().Read<DeviceBodyBinding>());
        QueryHandle masses = world.Query(world.QueryDefinition().Read<BodyMass>());
        QueryHandle joints = world.Query(world.QueryDefinition().Read<JointConstraint>().Read<DirectedRelationEndpoints<JointConstraint>>());
        QueryHandle boundRows = world.Query(world.QueryDefinition().Read<JointConstraint>().Read<DirectedRelationEndpoints<JointConstraint>>()
            .Buffer<LongRangeJointBound>(QueryAccess.ReadWrite));
        using var bounds = new DeviceJointBounds(backend, device, world, masses, boundRows, frames, sets, 16, new(4, 32, 1, 0));
        foreach (Entity joint in jointEntities) world.Add(joint, new DeviceJointBoundsBinding(bounds));
        using var frameInputs = frames.WriteInputs<JointConstraint, DeviceJointSource>(world, joints, default, 64);
        using var boundInputs = bounds.WriteInputs(64);
        using var selected = new DeviceConstraintSelection<JointConstraint, DeviceJointBoundSet, DeviceJointBoundsRows>(
            backend, device, world, boundRows, sets, new(bounds), "DeviceJointBoundSetsSelection");
        using var activities = new DeviceBodyActivities(backend, device, world, members, bodies);
        foreach (Entity body in dynamicBodies)
        {
            world.Add(body, new BodyActivity { Radius = 1 }); world.Add(body, new DeviceBodyActivityBinding(activities));
        }
        var bindings = new Dictionary<string, ResourceBinding[]>(); activities.AddBindings(bindings);
        using var dynamics = new DeviceBodyDynamics(backend, device, world, members, bodies, CompileActiveBodyDynamics(), 64,
            bindings, activities.GetWriteAccesses(jobs));
        using var bodyInputs = new DeviceBodyInputs(world, members, bodies);
        using var results = new DeviceBodyResults(backend, device, bodies);
        frames.AddBindings(bindings); sets.AddBindings(bindings); bounds.AddBindings(bindings);
        JobResourceAccess[] reads = [.. frames.GetReadAccesses(jobs), .. sets.GetReadAccesses(jobs),
            .. bounds.GetReadAccesses(jobs), .. activities.GetReadAccesses(jobs)];
        using var graph = new DeviceConstraintGraph(backend, device, bodies, dynamics, CompileJointBoundActivityGraph(),
            new(bounds.Capacity, 2), bindings, reads, 64, new[] { selected });
        using var activity = new DevicePhysicsActivity(backend, device, activities, graph, CompileJointBoundActivity(),
            new(.01f, .2f, .00001f, .01f), bindings, reads, 64);
        using var output = new PhysicsEventBuffer(jobs, 16);
        var stages = bounds.BuildInputs().Then(frames.BeginInputs()).Then(frameInputs).Then(frames.UploadInputs())
            .Then(sets.BeginInputs()).Then(boundInputs).Then(sets.UploadInputs()).Then(bounds.UploadInputs())
            .Then(bodyInputs.Upload()).Then(activity.UploadInputs()).Then(dynamics.UpdateSelection()).Then(activity.WakeConnected())
            .Then(dynamics.Predict(Vector3.Zero)).Then(dynamics.UpdateVelocities()).Then(activity.Finish())
            .Then(activity.Readback(output)).Then(results.Readback(64));
        var simulation = PhysicsSimulation.Create(world, QueryDefinition.Empty, stages, 1);

        simulation.Schedule(0, 0, .1).Complete();
        simulation.Schedule(1, .1, .1).Complete();

        Assert.InRange(world.Read<BodyPose>(tip).Position.Y, -11.00021f, -11.00019f);
        Assert.InRange(world.Read<BodyVelocity>(tip).Linear.Y, -.0011f, -.0009f);
        Assert.False(world.Read<BodyActivity>(tip).Sleeping);
        Assert.True(world.Read<BodyActivity>(other).Sleeping);
        world.ReleaseQuery(boundRows); world.ReleaseQuery(joints); world.ReleaseQuery(masses); world.ReleaseQuery(members);
    }

    private DeviceGraphKernels CompileJointBoundActivityGraph() => DeviceGraphTestKernels.CompileParallel(shaderCompiler, "joint_bound_activity_graph", """
        #include "physics_body_activity_types.slang"
        #define DEVICE_GRAPH_BODY_ACTIVE(body) deviceBodyActivityAwake(body)
        #include "physics_joint_bound_bodies.slang"
        [shader("compute")][numthreads(64,1,1)]
        void WriteRows(uint3 id:SV_DispatchThreadID)
        { DeviceJointBoundBodies rows; writeDeviceConstraintGraphRows(id.x,rows); }
        """, ShaderDirectory());

    private DeviceActivityKernels CompileJointBoundActivity()
    {
        D3D12TestShader shader = shaderCompiler.CompileWithSearchPaths("joint_bound_activity", """
            #include "physics_joint_bound_activity.slang"
            [shader("compute")][numthreads(64,1,1)]
            void ReadRows(uint3 id:SV_DispatchThreadID)
            { DeviceJointBoundActivity rows; readDeviceActivityRows(id.x,rows); }
            """, [new("ApplyDeviceActivityInputs", SlangStage.Compute), new("ClearDeviceActivitySignals", SlangStage.Compute),
                new("CollectDeviceActivityBodies", SlangStage.Compute), new("ReadRows", SlangStage.Compute),
                new("WakeDeviceBodies", SlangStage.Compute), new("AdvanceDeviceBodyQuietTime", SlangStage.Compute),
                new("FinishDeviceBodyActivity", SlangStage.Compute)], "sm_6_0", ShaderDirectory());
        ComputePipelineDesc Entry(int index) => new(shader.SlangCode, shader.Reflection, shader.GetEntryPoint(index), shader.GetEntryCode(index));
        return new(Entry(0), Entry(1), Entry(2), Entry(3), Entry(4), Entry(5), Entry(6));
    }
}
