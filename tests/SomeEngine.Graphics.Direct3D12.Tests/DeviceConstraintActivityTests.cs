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
    public void ADeviceMotorTargetEditWakesItsMixedConstraintIslandAndDrivesTheBody()
    {
        using var backend = D3D12GraphicsBackend.Create();
        using Device device = CreateDevice(backend);
        using var jobs = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using World world = WorldJobs.CreateWorld(scheduler: jobs);
        using var bodies = new DeviceBodies(backend, device, 4);
        Entity anchor = AddBody(world, bodies, 0, Vector3.Zero, BodyMotion.Static);
        Entity moving = AddBody(world, bodies, 1, Vector3.UnitX, BodyMotion.Dynamic);
        Entity other = AddBody(world, bodies, 3, new(10, 0, 0), BodyMotion.Dynamic);
        using var joints = new DeviceConstraintData<DeviceJointConstraint>(backend, device, 1, "DeviceJoints");
        using var motors = new DeviceConstraintData<DeviceMotorConstraint>(backend, device, 1, "DeviceMotors");
        using var jointMultipliers = new DeviceConstraintState<XpbdJointState>(backend, device, 1, "DeviceJointMultipliers");
        using var motorMultipliers = new DeviceConstraintState<XpbdMotorState>(backend, device, 1, "DeviceMotorMultipliers");
        Entity edge = world.CreateRelation(anchor, moving, new JointConstraint
            { SourceFrame = JointFrame.Identity, TargetFrame = JointFrame.Identity }).Entity;
        world.Add(edge, new JointMotor { Space = JointMotorSpace.World, Response = JointMotorResponse.Target,
            LinearStiffness = new(100, 0, 0), MaximumForce = new(float.PositiveInfinity) });
        world.Add(edge, new JointMotorTarget { Frame = new(Vector3.UnitX, Quaternion.Identity) });
        world.Add(edge, new DeviceConstraintBinding<DeviceJointConstraint>(joints, 0));
        world.Add(edge, new DeviceConstraintBinding<DeviceMotorConstraint>(motors, 0));
        world.Add(edge, new DeviceXpbdState<XpbdJointState>(jointMultipliers));
        world.Add(edge, new DeviceXpbdState<XpbdMotorState>(motorMultipliers));
        QueryHandle members = world.Query(world.QueryDefinition().Read<DeviceBodyBinding>());
        QueryHandle jointRows = world.Query(world.QueryDefinition().Read<JointConstraint>().Read<DirectedRelationEndpoints<JointConstraint>>());
        QueryHandle motorRows = world.Query(world.QueryDefinition().Read<JointMotor>().Read<JointMotorTarget>());
        QueryHandle events = world.Query(world.QueryDefinition().Read<PhysicsStepEvents>().ReadBuffer<PhysicsEvent>());
        using var activities = new DeviceBodyActivities(backend, device, world, members, bodies);
        foreach (Entity body in new[] { moving, other })
        { world.Add(body, new BodyActivity { Radius = 1 }); world.Add(body, new DeviceBodyActivityBinding(activities)); }
        var bindings = new Dictionary<string, ResourceBinding[]>(); activities.AddBindings(bindings);
        using var dynamics = new DeviceBodyDynamics(backend, device, world, members, bodies, CompileActiveBodyDynamics(), 64,
            bindings, activities.GetWriteAccesses(jobs));
        using var bodyInputs = new DeviceBodyInputs(world, members, bodies);
        using var results = new DeviceBodyResults(backend, device, bodies);
        using var writeJoints = joints.WriteInputs<JointConstraint, DeviceJointSource>(world, jointRows, default, 64);
        using var writeMotors = motors.WriteInputs<JointMotor, DeviceMotorSource>(world, motorRows, default, 64);
        using var selectedJoints = new DeviceConstraintSelection<JointConstraint, DeviceJointConstraint, DeviceXpbdStateRows<XpbdJointState>>(
            backend, device, world, jointRows, joints, new(jointMultipliers), "DeviceJointsSelection");
        using var selectedMotors = new DeviceConstraintSelection<JointMotor, DeviceMotorConstraint, DeviceXpbdStateRows<XpbdMotorState>>(
            backend, device, world, motorRows, motors, new(motorMultipliers), "DeviceMotorsSelection");
        joints.AddBindings(bindings); motors.AddBindings(bindings); jointMultipliers.AddBindings(bindings); motorMultipliers.AddBindings(bindings);
        JobResourceAccess[] reads = [.. joints.GetReadAccesses(jobs), .. motors.GetReadAccesses(jobs), .. activities.GetReadAccesses(jobs)];
        using var graph = new DeviceConstraintGraph(backend, device, bodies, dynamics, CompileMixedActivityGraph(), new(2, 2),
            bindings, reads, 64, new IDeviceConstraintSelection[] { selectedJoints, selectedMotors });
        using var solve = new DeviceXpbdSolver(backend, device, bodies, graph, CompileMixedActivityProjection(), new(4, 1),
            bindings, reads, new[] { jointMultipliers.States, motorMultipliers.States }, 64);
        using var activity = new DevicePhysicsActivity(backend, device, activities, graph, CompileMixedActivity(),
            new(.05f, .2f, .01f, .01f), bindings, reads, 64);
        using var output = new PhysicsEventBuffer(jobs, 8);
        var received = new List<(long Number, PhysicsEvent Event)>();
        using var consumer = new ConsumePhysicsEvents<RecordDeviceActivityEvents>(world, events, new(received),
            [JobResourceAccess.Write(jobs.GetResourceToken(received))]);
        var stages = bodyInputs.Upload().Then(joints.BeginInputs()).Then(writeJoints).Then(joints.UploadInputs())
            .Then(motors.BeginInputs()).Then(writeMotors).Then(motors.UploadInputs()).Then(activity.UploadInputs())
            .Then(dynamics.UpdateSelection()).Then(activity.WakeConnected()).Then(dynamics.Predict(Vector3.Zero))
            .Then(solve.Solve()).Then(dynamics.UpdateVelocities()).Then(activity.Finish()).Then(activity.Readback(output))
            .Then(results.Readback(64)).Then(output.Emit());
        using var frame = PhysicsSimulation.Create(world, QueryDefinition.Empty, stages, 1).CreateFrame(consumer, .1, 0, 0, 0);
        frame.AdvanceTime(.1); frame.CompleteForRead(); frame.AdvanceTime(.1); frame.CompleteForRead();
        Assert.True(world.Read<BodyActivity>(moving).Sleeping); Assert.True(world.Read<BodyActivity>(other).Sleeping);

        world.Replace(edge, new JointMotorTarget { Frame = new(Vector3.UnitX * 2, Quaternion.Identity) });
        frame.AdvanceTime(.1); frame.CompleteForRead();

        Near(new(1.5f, 0, 0), world.Read<BodyPose>(moving).Position);
        Near(new(5, 0, 0), world.Read<BodyVelocity>(moving).Linear);
        Assert.False(world.Read<BodyActivity>(moving).Sleeping); Assert.True(world.Read<BodyActivity>(other).Sleeping);
        var wake = Assert.Single(received, item => item.Event.Kind == PhysicsEventKind.Wake);
        Assert.Equal(moving, wake.Event.Subject); Assert.Equal(2, wake.Number); Assert.Equal(.2, wake.Event.Time);
        world.ReleaseQuery(events); world.ReleaseQuery(motorRows); world.ReleaseQuery(jointRows); world.ReleaseQuery(members);
    }

    private DeviceGraphKernels CompileMixedActivityGraph() => DeviceGraphTestKernels.CompileParallel(shaderCompiler, "mixed_activity_graph", """
        #include "physics_body_activity_types.slang"
        #define DEVICE_GRAPH_BODY_ACTIVE(body) deviceBodyActivityAwake(body)
        #include "physics_joint_bodies.slang"
        #include "physics_motor_bodies.slang"
        [shader("compute")][numthreads(64,1,1)]
        void WriteRows(uint3 id:SV_DispatchThreadID)
        { DeviceConstraintBodyGroups<DeviceJointBodies,DeviceMotorBodies> rows; writeDeviceConstraintGraphRows(id.x,rows); }
        """, ShaderDirectory());

    private DeviceXpbdKernels CompileMixedActivityProjection()
    {
        D3D12TestShader shader = shaderCompiler.CompileWithSearchPaths("mixed_activity_projection", """
            #include "physics_xpbd_graph.slang"
            #include "physics_xpbd_joint.slang"
            #include "physics_xpbd_motor.slang"
            #include "physics_joint_bodies.slang"
            #include "physics_motor_bodies.slang"
            typedef DeviceXpbdConstraintRows<DeviceJointBodies,DeviceXpbdJointProjection<DeviceGraphBodies>> JointRows;
            typedef DeviceXpbdConstraintRows<DeviceMotorBodies,DeviceXpbdMotorProjection<DeviceGraphBodies>> MotorRows;
            [shader("compute")][numthreads(64,1,1)]
            void Initialize(uint3 group:SV_GroupID,uint3 thread:SV_GroupThreadID)
            { DeviceXpbdConstraintGroups<JointRows,MotorRows> rows; initializeDeviceXpbdGraph(group.x,thread.x,rows); }
            [shader("compute")][numthreads(64,1,1)]
            void Project(uint3 group:SV_GroupID,uint3 thread:SV_GroupThreadID)
            { DeviceXpbdConstraintGroups<JointRows,MotorRows> rows; projectDeviceXpbdGraph(group.x,thread.x,rows); }
            """, [new("Initialize", SlangStage.Compute), new("Project", SlangStage.Compute)], "sm_6_0", ShaderDirectory());
        ComputePipelineDesc Entry(int index) => new(shader.SlangCode, shader.Reflection, shader.GetEntryPoint(index), shader.GetEntryCode(index));
        return new(Entry(0), Entry(1));
    }
    private DeviceActivityKernels CompileMixedActivity()
    {
        D3D12TestShader shader = shaderCompiler.CompileWithSearchPaths("mixed_constraint_activity", """
            #include "physics_joint_activity.slang"
            #include "physics_motor_activity.slang"
            [shader("compute")][numthreads(64,1,1)]
            void ReadRows(uint3 id:SV_DispatchThreadID)
            { DeviceConstraintActivityGroups<DeviceJointActivity,DeviceMotorActivity> rows; readDeviceActivityRows(id.x,rows); }
            """, [new("ApplyDeviceActivityInputs", SlangStage.Compute), new("ClearDeviceActivitySignals", SlangStage.Compute),
                new("CollectDeviceActivityBodies", SlangStage.Compute), new("ReadRows", SlangStage.Compute),
                new("WakeDeviceBodies", SlangStage.Compute), new("AdvanceDeviceBodyQuietTime", SlangStage.Compute),
                new("FinishDeviceBodyActivity", SlangStage.Compute)], "sm_6_0", ShaderDirectory());
        ComputePipelineDesc Entry(int index) => new(shader.SlangCode, shader.Reflection, shader.GetEntryPoint(index), shader.GetEntryCode(index));
        return new(Entry(0), Entry(1), Entry(2), Entry(3), Entry(4), Entry(5), Entry(6));
    }
}
