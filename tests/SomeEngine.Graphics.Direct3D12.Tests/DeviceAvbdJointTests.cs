using System.Numerics;
using SlangShaderSharp;
using SomeEngine.ECS;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;
using SomeEngine.Physics;
using Xunit;

namespace SomeEngine.Graphics.Direct3D12.Tests;

public sealed partial class DeviceAvbdTests
{
    [Fact]
    public void DeviceJointUsesItsMaterialAndResidentDualsInTheSharedBodyEquation()
    {
        using var backend = D3D12GraphicsBackend.Create();
        using Device device = CreateDevice(backend);
        using var jobs = new Scheduler(new JobRuntimeConfig { WorkerCount = 0 });
        using World world = WorldJobs.CreateWorld(scheduler: jobs);
        using var bodies = new DeviceBodies(backend, device, 2);
        using var equations = new DeviceAvbdBodyEquations(backend, device, bodies);
        var source = CreateBody(world, bodies, equations, 0, Vector3.Zero, BodyMotion.Static);
        var target = CreateBody(world, bodies, equations, 1, Vector3.UnitX, BodyMotion.Dynamic);
        using var parameters = new DeviceConstraintData<DeviceJointConstraint>(backend, device, 4, "DeviceJoints");
        using var histories = new DeviceConstraintState<DeviceAvbdJointState>(backend, device, 4, "DeviceAvbdJointStates");
        var joint = world.CreateRelation(source, target, JointConstraints.CreateBall(JointFrame.Identity, JointFrame.Identity, 100));
        world.Add(joint.Entity, new AvbdJointState { LinearPenalty = new(900), AngularPenalty = new(900) });
        world.Add(joint.Entity, new DeviceConstraintBinding<DeviceJointConstraint>(parameters, 3));
        world.Add(joint.Entity, new DeviceAvbdState<DeviceAvbdJointState>(histories));
        QueryHandle members = world.Query(new QueryDefinitionBuilder().Read<DeviceBodyBinding>().All<DeviceAvbdEquationBinding>());
        QueryHandle joints = world.Query(new QueryDefinitionBuilder().Read<JointConstraint>().Read<DirectedRelationEndpoints<JointConstraint>>());
        QueryHandle states = world.Query(new QueryDefinitionBuilder().Read<AvbdJointState>()
            .Read<DeviceConstraintBinding<DeviceJointConstraint>>().Read<DeviceAvbdState<DeviceAvbdJointState>>());
        using var input = parameters.WriteInputs<JointConstraint, DeviceJointSource>(world, joints, default, 64);
        using var stateInput = new DeviceAvbdStateInputs<AvbdJointState, DeviceJointConstraint, DeviceAvbdJointState, DeviceAvbdJointStateSource>(
            backend, device, world, states, parameters, histories, default);
        using var selected = new DeviceConstraintSelection<JointConstraint, DeviceJointConstraint>(backend, device, world, joints, parameters, "DeviceJointsSelection");
        using var selectedStates = new DeviceConstraintSelection<JointConstraint, DeviceJointConstraint, DeviceAvbdStateRows<DeviceAvbdJointState>>(
            backend, device, world, joints, parameters, new(histories), "DeviceJointAvbdSelection");
        using var dynamics = new DeviceBodyDynamics(backend, device, world, members, bodies, CompileDynamics(), 64);
        using var results = new DeviceBodyResults(backend, device, bodies);
        var bindings = new Dictionary<string, ResourceBinding[]>();
        parameters.AddBindings(bindings); histories.AddBindings(bindings);
        using var graph = new DeviceConstraintGraph(backend, device, bodies, dynamics, CompileJointGraph(), new(4, 2), bindings,
            parameters.GetReadAccesses(jobs), 64, new[] { selected });
        using var solver = new DeviceAvbdSolver(backend, device, bodies, equations, graph, CompileJointAvbd(), new(32, 1, 0, 0), bindings,
            parameters.GetReadAccesses(jobs).Append(histories.GetWriteAccess(jobs)).ToArray(), 64, new[] { selectedStates });
        var step = bodies.WriteInputs(64).Then(bodies.UploadInputs())
            .Then(parameters.BeginInputs()).Then(input).Then(parameters.UploadInputs()).Then(stateInput.Upload())
            .Then(dynamics.Predict(Vector3.Zero)).Then(solver.Solve()).Then(dynamics.UpdateVelocities()).Then(results.Readback(64));

        PhysicsSimulation.Create(world, QueryDefinition.Empty, step, 1).Schedule(9, 3, .1).Complete();

        Assert.InRange(world.Read<BodyPose>(target).Position.X, .49999f, .50001f);
        Assert.InRange(world.Read<BodyVelocity>(target).Linear.X, -5.0001f, -4.9999f);
        Assert.Equal(Vector3.Zero, world.Read<BodyPose>(source).Position);
        Assert.Equal(3.1, world.Read<BodyStepState>(target).Time);
        world.ReleaseQuery(states); world.ReleaseQuery(joints); world.ReleaseQuery(members);
    }

    [Fact]
    public void DeviceMotorCapsItsForceAndAppliesTheReactionToTheSource()
    {
        using var backend = D3D12GraphicsBackend.Create();
        using Device device = CreateDevice(backend);
        using var jobs = new Scheduler(new JobRuntimeConfig { WorkerCount = 0 });
        using World world = WorldJobs.CreateWorld(scheduler: jobs);
        using var bodies = new DeviceBodies(backend, device, 2);
        using var equations = new DeviceAvbdBodyEquations(backend, device, bodies);
        var source = CreateBody(world, bodies, equations, 0, Vector3.Zero, BodyMotion.Dynamic);
        var target = CreateBody(world, bodies, equations, 1, Vector3.Zero, BodyMotion.Dynamic);
        using var jointsData = new DeviceConstraintData<DeviceJointConstraint>(backend, device, 1, "DeviceJoints");
        using var motorsData = new DeviceConstraintData<DeviceMotorConstraint>(backend, device, 1, "DeviceMotors");
        using var histories = new DeviceConstraintState<DeviceAvbdMotorState>(backend, device, 1, "DeviceAvbdMotorStates");
        var joint = world.CreateRelation(source, target, new JointConstraint { SourceFrame = JointFrame.Identity, TargetFrame = JointFrame.Identity });
        world.Add(joint.Entity, new JointMotor { Space = JointMotorSpace.Source, Response = JointMotorResponse.Both,
            LinearDamping = new(10, 0, 0), MaximumForce = new(2) });
        world.Add(joint.Entity, new JointMotorTarget { Frame = JointFrame.Identity, LinearVelocity = new(10, 0, 0) });
        world.Add(joint.Entity, new AvbdMotorState { LinearPenalty = new(2000) });
        world.Add(joint.Entity, new DeviceConstraintBinding<DeviceJointConstraint>(jointsData, 0));
        world.Add(joint.Entity, new DeviceConstraintBinding<DeviceMotorConstraint>(motorsData, 0));
        world.Add(joint.Entity, new DeviceAvbdState<DeviceAvbdMotorState>(histories));
        QueryHandle members = world.Query(new QueryDefinitionBuilder().Read<DeviceBodyBinding>().All<DeviceAvbdEquationBinding>());
        QueryHandle joints = world.Query(new QueryDefinitionBuilder().Read<JointConstraint>().Read<DirectedRelationEndpoints<JointConstraint>>());
        QueryHandle motors = world.Query(new QueryDefinitionBuilder().Read<JointMotor>().Read<JointMotorTarget>());
        QueryHandle states = world.Query(new QueryDefinitionBuilder().Read<AvbdMotorState>()
            .Read<DeviceConstraintBinding<DeviceMotorConstraint>>().Read<DeviceAvbdState<DeviceAvbdMotorState>>());
        using var jointInputs = jointsData.WriteInputs<JointConstraint, DeviceJointSource>(world, joints, default, 64);
        using var motorInputs = motorsData.WriteInputs<JointMotor, DeviceMotorSource>(world, motors, default, 64);
        using var stateInputs = new DeviceAvbdStateInputs<AvbdMotorState, DeviceMotorConstraint, DeviceAvbdMotorState, DeviceAvbdMotorStateSource>(
            backend, device, world, states, motorsData, histories, default);
        using var selected = new DeviceConstraintSelection<JointMotor, DeviceMotorConstraint>(backend, device, world, motors, motorsData, "DeviceMotorsSelection");
        using var selectedStates = new DeviceConstraintSelection<JointMotor, DeviceMotorConstraint, DeviceAvbdStateRows<DeviceAvbdMotorState>>(
            backend, device, world, motors, motorsData, new(histories), "DeviceMotorAvbdSelection");
        using var dynamics = new DeviceBodyDynamics(backend, device, world, members, bodies, CompileDynamics(), 64);
        using var results = new DeviceBodyResults(backend, device, bodies);
        var bindings = new Dictionary<string, ResourceBinding[]>();
        jointsData.AddBindings(bindings); motorsData.AddBindings(bindings); histories.AddBindings(bindings);
        JobResourceAccess[] reads = [.. jointsData.GetReadAccesses(jobs), .. motorsData.GetReadAccesses(jobs)];
        using var graph = new DeviceConstraintGraph(backend, device, bodies, dynamics, CompileMotorGraph(), new(1, 2), bindings, reads, 64, new[] { selected });
        using var solver = new DeviceAvbdSolver(backend, device, bodies, equations, graph, CompileMotorAvbd(), new(32, 1, 0, 0), bindings,
            reads.Append(histories.GetWriteAccess(jobs)).ToArray(), 64, new[] { selectedStates });
        var step = bodies.WriteInputs(64).Then(bodies.UploadInputs())
            .Then(jointsData.BeginInputs()).Then(jointInputs).Then(jointsData.UploadInputs())
            .Then(motorsData.BeginInputs()).Then(motorInputs).Then(motorsData.UploadInputs()).Then(stateInputs.Upload())
            .Then(dynamics.Predict(Vector3.Zero)).Then(solver.Solve()).Then(dynamics.UpdateVelocities()).Then(results.Readback(64));

        PhysicsSimulation.Create(world, QueryDefinition.Empty, step, 1).Schedule(0, 0, .1).Complete();

        Assert.InRange(world.Read<BodyPose>(source).Position.X, -.020001f, -.019999f);
        Assert.InRange(world.Read<BodyPose>(target).Position.X, .019999f, .020001f);
        Assert.InRange(world.Read<BodyVelocity>(source).Linear.X, -.20001f, -.19999f);
        Assert.InRange(world.Read<BodyVelocity>(target).Linear.X, .19999f, .20001f);
        world.ReleaseQuery(states); world.ReleaseQuery(motors); world.ReleaseQuery(joints); world.ReleaseQuery(members);
    }

    [Fact]
    public void DeviceWorldMotorSamplesTimedDampingAndCapsTorqueWhileItsReferenceKeepsMoving()
    {
        using var backend = D3D12GraphicsBackend.Create();
        using Device device = CreateDevice(backend);
        using var jobs = new Scheduler(new JobRuntimeConfig { WorkerCount = 0 });
        using World world = WorldJobs.CreateWorld(scheduler: jobs);
        using var bodies = new DeviceBodies(backend, device, 2);
        using var equations = new DeviceAvbdBodyEquations(backend, device, bodies);
        var source = CreateBody(world, bodies, equations, 0, new(3, 0, 0), BodyMotion.Dynamic);
        var target = CreateBody(world, bodies, equations, 1, Vector3.Zero, BodyMotion.Dynamic);
        world.Replace(source, new BodyVelocity { Linear = -Vector3.UnitX });
        world.Replace(target, new BodyMass { Motion = BodyMotion.Dynamic, InverseMass = 1, InverseInertia = Matrix4x4.Identity });
        using var jointsData = new DeviceConstraintData<DeviceJointConstraint>(backend, device, 1, "DeviceJoints");
        using var motorsData = new DeviceConstraintData<DeviceMotorConstraint>(backend, device, 1, "DeviceMotors");
        using var histories = new DeviceConstraintState<DeviceAvbdMotorState>(backend, device, 1, "DeviceAvbdMotorStates");
        var joint = world.CreateRelation(source, target, new JointConstraint { SourceFrame = JointFrame.Identity, TargetFrame = JointFrame.Identity });
        world.Add(joint.Entity, new JointMotor { Space = JointMotorSpace.World, Response = JointMotorResponse.Target,
            LinearStiffness = new(400, 0, 0), LinearDamping = new(20, 0, 0), MaximumForce = new(1000),
            AngularDamping = new(10, 0, 0), MaximumTorque = Vector3.One });
        world.Add(joint.Entity, new JointMotorTarget { Frame = new(Vector3.UnitX, Quaternion.Identity),
            LinearVelocity = new(2, 0, 0), AngularVelocity = new(2, 0, 0), Time = 1 });
        world.Add(joint.Entity, new AvbdMotorState { LinearPenalty = new(2000), AngularPenalty = new(2000) });
        world.Add(joint.Entity, new DeviceConstraintBinding<DeviceJointConstraint>(jointsData, 0));
        world.Add(joint.Entity, new DeviceConstraintBinding<DeviceMotorConstraint>(motorsData, 0));
        world.Add(joint.Entity, new DeviceAvbdState<DeviceAvbdMotorState>(histories));
        QueryHandle members = world.Query(new QueryDefinitionBuilder().Read<DeviceBodyBinding>().All<DeviceAvbdEquationBinding>());
        QueryHandle joints = world.Query(new QueryDefinitionBuilder().Read<JointConstraint>().Read<DirectedRelationEndpoints<JointConstraint>>());
        QueryHandle motors = world.Query(new QueryDefinitionBuilder().Read<JointMotor>().Read<JointMotorTarget>());
        QueryHandle states = world.Query(new QueryDefinitionBuilder().Read<AvbdMotorState>()
            .Read<DeviceConstraintBinding<DeviceMotorConstraint>>().Read<DeviceAvbdState<DeviceAvbdMotorState>>());
        using var jointInputs = jointsData.WriteInputs<JointConstraint, DeviceJointSource>(world, joints, default, 64);
        using var motorInputs = motorsData.WriteInputs<JointMotor, DeviceMotorSource>(world, motors, default, 64);
        using var stateInputs = new DeviceAvbdStateInputs<AvbdMotorState, DeviceMotorConstraint, DeviceAvbdMotorState, DeviceAvbdMotorStateSource>(
            backend, device, world, states, motorsData, histories, default);
        using var selected = new DeviceConstraintSelection<JointMotor, DeviceMotorConstraint>(backend, device, world, motors, motorsData, "DeviceMotorsSelection");
        using var selectedStates = new DeviceConstraintSelection<JointMotor, DeviceMotorConstraint, DeviceAvbdStateRows<DeviceAvbdMotorState>>(
            backend, device, world, motors, motorsData, new(histories), "DeviceMotorAvbdSelection");
        using var dynamics = new DeviceBodyDynamics(backend, device, world, members, bodies, CompileDynamics(), 64);
        using var results = new DeviceBodyResults(backend, device, bodies);
        var bindings = new Dictionary<string, ResourceBinding[]>();
        jointsData.AddBindings(bindings); motorsData.AddBindings(bindings); histories.AddBindings(bindings);
        JobResourceAccess[] reads = [.. jointsData.GetReadAccesses(jobs), .. motorsData.GetReadAccesses(jobs)];
        using var graph = new DeviceConstraintGraph(backend, device, bodies, dynamics, CompileMotorGraph(), new(1, 2), bindings, reads, 64, new[] { selected });
        using var solver = new DeviceAvbdSolver(backend, device, bodies, equations, graph, CompileMotorAvbd(), new(32, 1, 0, 0), bindings,
            reads.Append(histories.GetWriteAccess(jobs)).ToArray(), 64, new[] { selectedStates });
        var step = bodies.WriteInputs(64).Then(bodies.UploadInputs())
            .Then(jointsData.BeginInputs()).Then(jointInputs).Then(jointsData.UploadInputs())
            .Then(motorsData.BeginInputs()).Then(motorInputs).Then(motorsData.UploadInputs()).Then(stateInputs.Upload())
            .Then(dynamics.Predict(Vector3.Zero)).Then(solver.Solve()).Then(dynamics.UpdateVelocities()).Then(results.Readback(64));

        PhysicsSimulation.Create(world, QueryDefinition.Empty, step, 1).Schedule(10, 1, .1).Complete();

        Assert.InRange(world.Read<BodyPose>(source).Position.X, 2.89999f, 2.90001f);
        Assert.InRange(world.Read<BodyVelocity>(source).Linear.X, -1.00001f, -.99999f);
        Assert.InRange(world.Read<BodyPose>(target).Position.X, .742847f, .742867f);
        Assert.InRange(world.Read<BodyVelocity>(target).Linear.X, 7.42847f, 7.42867f);
        Assert.InRange(Vector3.Distance(new(.1f, 0, 0), world.Read<BodyVelocity>(target).Angular), 0, 1e-5f);
        Assert.Equal(Vector3.Zero, world.Read<BodyVelocity>(source).Angular);
        world.ReleaseQuery(states); world.ReleaseQuery(motors); world.ReleaseQuery(joints); world.ReleaseQuery(members);
    }

    private DeviceGraphKernels CompileJointGraph()
    {
        D3D12TestShader shader = shaderCompiler.CompileWithSearchPaths("avbd_joint_graph", """
            #include "physics_joint_bodies.slang"
            #include "physics_constraint_graph.slang"
            [shader("compute")][numthreads(64,1,1)]
            void WriteRows(uint3 id : SV_DispatchThreadID) { DeviceJointBodies rows; writeDeviceConstraintGraphRows(id.x, rows); }
            [shader("compute")][numthreads(1,1,1)]
            void BuildGraph(uint3 id : SV_DispatchThreadID) { buildDeviceConstraintGraph(); }
            """, [new("WriteRows", SlangStage.Compute), new("BuildGraph", SlangStage.Compute)], "sm_6_0", ShaderDirectory());
        return new(Entry(shader, 0), Entry(shader, 1));
    }
    private DeviceAvbdKernels CompileJointAvbd()
    {
        D3D12TestShader shader = shaderCompiler.CompileWithSearchPaths("avbd_joint_step", """
            #include "physics_avbd_joint.slang"
            [shader("compute")][numthreads(64,1,1)]
            void Initialize(uint3 id : SV_DispatchThreadID) { DeviceJointAvbdConstraints rows; initializeDeviceAvbd(id.x, rows); }
            [shader("compute")][numthreads(64,1,1)]
            void UpdateBody(uint3 id : SV_DispatchThreadID) { DeviceJointAvbdConstraints rows; updateDeviceAvbdBody(id.x, rows); }
            [shader("compute")][numthreads(64,1,1)]
            void UpdateDuals(uint3 id : SV_DispatchThreadID) { DeviceJointAvbdConstraints rows; updateDeviceAvbdDuals(id.x, rows); }
            """, [new("Initialize", SlangStage.Compute), new("UpdateBody", SlangStage.Compute), new("UpdateDuals", SlangStage.Compute)],
            "sm_6_0", ShaderDirectory());
        return new(Entry(shader, 0), Entry(shader, 1), Entry(shader, 2));
    }
    private DeviceGraphKernels CompileMotorGraph()
    {
        D3D12TestShader shader = shaderCompiler.CompileWithSearchPaths("avbd_motor_graph", """
            #include "physics_motor_bodies.slang"
            #include "physics_constraint_graph.slang"
            [shader("compute")][numthreads(64,1,1)]
            void WriteRows(uint3 id : SV_DispatchThreadID) { DeviceMotorBodies rows; writeDeviceConstraintGraphRows(id.x, rows); }
            [shader("compute")][numthreads(1,1,1)]
            void BuildGraph(uint3 id : SV_DispatchThreadID) { buildDeviceConstraintGraph(); }
            """, [new("WriteRows", SlangStage.Compute), new("BuildGraph", SlangStage.Compute)], "sm_6_0", ShaderDirectory());
        return new(Entry(shader, 0), Entry(shader, 1));
    }
    private DeviceAvbdKernels CompileMotorAvbd()
    {
        D3D12TestShader shader = shaderCompiler.CompileWithSearchPaths("avbd_motor_step", """
            #include "physics_avbd_motor.slang"
            [shader("compute")][numthreads(64,1,1)]
            void Initialize(uint3 id : SV_DispatchThreadID) { DeviceMotorAvbdConstraints rows; initializeDeviceAvbd(id.x, rows); }
            [shader("compute")][numthreads(64,1,1)]
            void UpdateBody(uint3 id : SV_DispatchThreadID) { DeviceMotorAvbdConstraints rows; updateDeviceAvbdBody(id.x, rows); }
            [shader("compute")][numthreads(64,1,1)]
            void UpdateDuals(uint3 id : SV_DispatchThreadID) { DeviceMotorAvbdConstraints rows; updateDeviceAvbdDuals(id.x, rows); }
            """, [new("Initialize", SlangStage.Compute), new("UpdateBody", SlangStage.Compute), new("UpdateDuals", SlangStage.Compute)],
            "sm_6_0", ShaderDirectory());
        return new(Entry(shader, 0), Entry(shader, 1), Entry(shader, 2));
    }
}
