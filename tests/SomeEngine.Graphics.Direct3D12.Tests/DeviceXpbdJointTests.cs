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
    public void DeviceJointFramesLimitsAndFullInertiaMatchTheCoupledBodyResponse()
    {
        using var backend = D3D12GraphicsBackend.Create();
        using Device device = CreateDevice(backend);
        using var jobs = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using World world = WorldJobs.CreateWorld(scheduler: jobs);
        using World reference = WorldJobs.CreateWorld(scheduler: jobs);
        using var bodies = new DeviceBodies(backend, device, 2);
        using var joints = new DeviceConstraintData<DeviceJointConstraint>(backend, device, 1, "DeviceJoints");
        using var multipliers = new DeviceConstraintState<XpbdJointState>(backend, device, 1, "DeviceJointMultipliers");
        Entity source = AddBody(world, bodies, 0, Vector3.Zero, BodyMotion.Static);
        Entity target = AddBody(world, bodies, 1, new(.7f, -.3f, .4f), BodyMotion.Dynamic);
        var sourcePose = new BodyPose { Rotation = Quaternion.CreateFromYawPitchRoll(-.2f, .1f, .3f) };
        var targetPose = new BodyPose { Position = new(.7f, -.3f, .4f), Rotation = Quaternion.CreateFromYawPitchRoll(.4f, -.2f, .3f) };
        var mass = new BodyMass { Motion = BodyMotion.Dynamic, InverseMass = .7f, CenterOfMass = new(.1f, .2f, -.1f),
            InverseInertia = new(1, .1f, 0, 0, .1f, .7f, .05f, 0, 0, .05f, .4f, 0, 0, 0, 0, 1) };
        var velocity = new BodyVelocity { Linear = new(.2f, -.3f, .1f), Angular = new(-.1f, .3f, .2f) };
        world.Replace(source, sourcePose); world.Replace(target, targetPose); world.Replace(target, mass); world.Replace(target, velocity);
        Entity first = reference.CreateEntity(sourcePose);
        reference.Add(first, new BodyMass { Motion = BodyMotion.Static }); reference.Add(first, new BodyVelocity()); reference.Add(first, new BodyStepState());
        Entity second = reference.CreateEntity(targetPose);
        reference.Add(second, mass); reference.Add(second, velocity); reference.Add(second, new BodyStepState());
        JointConstraint definition = JointConstraints.CreateHinge(
            new(new(.2f, .1f, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, .3f)),
            new(new(-.1f, 0, .2f), Quaternion.CreateFromAxisAngle(Vector3.UnitY, -.2f)), 200, 100);
        definition.LinearX = new(JointAxisMotion.Limited, -.2f, .3f);
        definition.AngularY = new(JointAxisMotion.Limited, -.1f, .15f);
        RelationEdge<JointConstraint> joint = world.CreateRelation(source, target, definition);
        RelationEdge<JointConstraint> expectedJoint = reference.CreateRelation(first, second, definition);
        world.Add(joint.Entity, new DeviceConstraintBinding<DeviceJointConstraint>(joints, 0));
        world.Add(joint.Entity, new DeviceXpbdState<XpbdJointState>(multipliers));
        reference.Add(expectedJoint.Entity, new XpbdJointState());
        var referenceBodies = reference.Query(reference.QueryDefinition().ReadWrite<BodyPose>().Read<BodyMass>().Read<BodyStepState>());
        var referenceRows = reference.Query(reference.QueryDefinition().Read<JointConstraint>()
            .Read<DirectedRelationEndpoints<JointConstraint>>().ReadWrite<XpbdJointState>());
        var scalar = new PredictBodies(Vector3.Zero, 64).Then(SolveXpbd.Create(reference, referenceBodies,
            new XpbdConstraints<JointConstraint, XpbdJointProjection>(referenceRows), 8)).Then(new UpdateBodyVelocities(64));
        var members = world.Query(world.QueryDefinition().Read<DeviceBodyBinding>());
        var rows = world.Query(world.QueryDefinition().Read<JointConstraint>().Read<DirectedRelationEndpoints<JointConstraint>>()
            .Read<DeviceConstraintBinding<DeviceJointConstraint>>().All<DeviceXpbdState<XpbdJointState>>());
        using var write = joints.WriteInputs<JointConstraint, DeviceJointSource>(world, rows, new(), 64);
        using var dynamics = new DeviceBodyDynamics(backend, device, world, members, bodies, CompileBodyDynamics(), 64);
        using var results = new DeviceBodyResults(backend, device, bodies);
        using var project = new DeviceXpbdProjection<DeviceJointConstraint, XpbdJointState>(backend, device, world,
            rows, bodies, dynamics, joints, multipliers, CompileXpbdJoint(), 64);
        var deviceStages = joints.BeginInputs().Then(write).Then(joints.UploadInputs())
            .Then(bodies.WriteInputs(64)).Then(bodies.UploadInputs()).Then(dynamics.Predict(Vector3.Zero))
            .Then(project.Initialize()).Then(project.Iterate(8)).Then(dynamics.UpdateVelocities()).Then(results.Readback(64));

        PhysicsSimulation.Create(reference, SomeEngine.ECS.Queries.QueryDefinition.Empty, scalar, 1).Schedule(0, 0, .05).Complete();
        PhysicsSimulation.Create(world, world.QueryDefinition().All<DeviceBodyBinding>().Build(), deviceStages, 1).Schedule(0, 0, .05).Complete();

        Assert.InRange(Vector3.Distance(reference.Read<BodyPose>(second).Position, world.Read<BodyPose>(target).Position), 0, 1e-4f);
        Assert.InRange(MathF.Abs(Quaternion.Dot(reference.Read<BodyPose>(second).Rotation, world.Read<BodyPose>(target).Rotation)), .999999f, 1.000001f);
        Assert.InRange(Vector3.Distance(reference.Read<BodyVelocity>(second).Linear, world.Read<BodyVelocity>(target).Linear), 0, 1e-4f);
        Assert.InRange(Vector3.Distance(reference.Read<BodyVelocity>(second).Angular, world.Read<BodyVelocity>(target).Angular), 0, 1e-4f);
        world.ReleaseQuery(rows); world.ReleaseQuery(members);
        reference.ReleaseQuery(referenceRows); reference.ReleaseQuery(referenceBodies);
    }

    [Fact]
    public void DeviceMotorLimitsBothBodyReactionsByThePhysicalMaximumForce()
    {
        using var backend = D3D12GraphicsBackend.Create();
        using Device device = CreateDevice(backend);
        using var jobs = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using World world = WorldJobs.CreateWorld(scheduler: jobs);
        using var bodies = new DeviceBodies(backend, device, 2);
        using var joints = new DeviceConstraintData<DeviceJointConstraint>(backend, device, 1, "DeviceJoints");
        using var motors = new DeviceConstraintData<DeviceMotorConstraint>(backend, device, 2, "DeviceMotors");
        using var multipliers = new DeviceConstraintState<XpbdMotorState>(backend, device, 2, "DeviceMotorMultipliers");
        Entity source = AddBody(world, bodies, 0, Vector3.Zero, BodyMotion.Dynamic);
        Entity target = AddBody(world, bodies, 1, Vector3.Zero, BodyMotion.Dynamic);
        RelationEdge<JointConstraint> edge = world.CreateRelation(source, target,
            JointConstraints.CreateBall(JointFrame.Identity, JointFrame.Identity, 0));
        world.Add(edge.Entity, new JointMotor { Space = JointMotorSpace.Source, Response = JointMotorResponse.Both,
            LinearStiffness = new(100, 0, 0), LinearDamping = new(20, 0, 0), MaximumForce = new(2, 0, 0) });
        world.Add(edge.Entity, new JointMotorTarget { Frame = new(Vector3.UnitX, Quaternion.Identity), Time = 1 });
        world.Add(edge.Entity, new DeviceConstraintBinding<DeviceJointConstraint>(joints, 0));
        world.Add(edge.Entity, new DeviceConstraintBinding<DeviceMotorConstraint>(motors, 1));
        world.Add(edge.Entity, new DeviceXpbdState<XpbdMotorState>(multipliers));
        var members = world.Query(world.QueryDefinition().Read<DeviceBodyBinding>());
        var jointRows = world.Query(world.QueryDefinition().Read<JointConstraint>().Read<DirectedRelationEndpoints<JointConstraint>>()
            .Read<DeviceConstraintBinding<DeviceJointConstraint>>());
        var motorRows = world.Query(world.QueryDefinition().Read<JointMotor>().Read<JointMotorTarget>()
            .Read<DeviceConstraintBinding<DeviceMotorConstraint>>().All<DeviceXpbdState<XpbdMotorState>>());
        using var writeJoints = joints.WriteInputs<JointConstraint, DeviceJointSource>(world, jointRows, new(), 64);
        using var writeMotors = motors.WriteInputs<JointMotor, DeviceMotorSource>(world, motorRows, new(), 64);
        using var dynamics = new DeviceBodyDynamics(backend, device, world, members, bodies, CompileBodyDynamics(), 64);
        using var results = new DeviceBodyResults(backend, device, bodies);
        var bindings = new Dictionary<string, ResourceBinding[]>();
        joints.AddBindings(bindings);
        using var project = new DeviceXpbdProjection<DeviceMotorConstraint, XpbdMotorState>(backend, device, world,
            motorRows, bodies, dynamics, motors, multipliers, CompileXpbdMotor(), 64, bindings, joints.GetReadAccesses(jobs));
        var stages = joints.BeginInputs().Then(writeJoints).Then(joints.UploadInputs())
            .Then(motors.BeginInputs()).Then(writeMotors).Then(motors.UploadInputs())
            .Then(bodies.WriteInputs(64)).Then(bodies.UploadInputs()).Then(dynamics.Predict(Vector3.Zero))
            .Then(project.Initialize()).Then(project.Iterate(8)).Then(dynamics.UpdateVelocities()).Then(results.Readback(64));

        PhysicsSimulation.Create(world, world.QueryDefinition().All<DeviceBodyBinding>().Build(), stages, 1).Schedule(9, 1, .1).Complete();

        Near(new(-.02f, 0, 0), world.Read<BodyPose>(source).Position);
        Near(new(.02f, 0, 0), world.Read<BodyPose>(target).Position);
        Near(new(-.2f, 0, 0), world.Read<BodyVelocity>(source).Linear);
        Near(new(.2f, 0, 0), world.Read<BodyVelocity>(target).Linear);
        world.ReleaseQuery(motorRows);
        world.ReleaseQuery(jointRows);
        world.ReleaseQuery(members);
    }

    [Fact]
    public void DeviceWorldMotorSamplesTheTimedTargetAndDampingWhileTheSourceKeepsItsMotion()
    {
        using var backend = D3D12GraphicsBackend.Create();
        using Device device = CreateDevice(backend);
        using var jobs = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using World world = WorldJobs.CreateWorld(scheduler: jobs);
        using var bodies = new DeviceBodies(backend, device, 2);
        using var joints = new DeviceConstraintData<DeviceJointConstraint>(backend, device, 1, "DeviceJoints");
        using var motors = new DeviceConstraintData<DeviceMotorConstraint>(backend, device, 1, "DeviceMotors");
        using var multipliers = new DeviceConstraintState<XpbdMotorState>(backend, device, 1, "DeviceMotorMultipliers");
        Entity source = AddBody(world, bodies, 0, new(3, 0, 0), BodyMotion.Dynamic);
        Entity target = AddBody(world, bodies, 1, Vector3.Zero, BodyMotion.Dynamic);
        world.Replace(source, new BodyVelocity { Linear = -Vector3.UnitX });
        RelationEdge<JointConstraint> edge = world.CreateRelation(source, target,
            JointConstraints.CreateBall(JointFrame.Identity, JointFrame.Identity, 0));
        world.Add(edge.Entity, new JointMotor { Space = JointMotorSpace.World, Response = JointMotorResponse.Target,
            LinearStiffness = new(400, 0, 0), LinearDamping = new(20, 0, 0), MaximumForce = new(1000, 0, 0) });
        world.Add(edge.Entity, new JointMotorTarget { Frame = new(Vector3.UnitX, Quaternion.Identity),
            LinearVelocity = new(2, 0, 0), Time = 1 });
        world.Add(edge.Entity, new DeviceConstraintBinding<DeviceJointConstraint>(joints, 0));
        world.Add(edge.Entity, new DeviceConstraintBinding<DeviceMotorConstraint>(motors, 0));
        world.Add(edge.Entity, new DeviceXpbdState<XpbdMotorState>(multipliers));
        var members = world.Query(world.QueryDefinition().Read<DeviceBodyBinding>());
        var jointRows = world.Query(world.QueryDefinition().Read<JointConstraint>().Read<DirectedRelationEndpoints<JointConstraint>>()
            .Read<DeviceConstraintBinding<DeviceJointConstraint>>());
        var motorRows = world.Query(world.QueryDefinition().Read<JointMotor>().Read<JointMotorTarget>()
            .Read<DeviceConstraintBinding<DeviceMotorConstraint>>().All<DeviceXpbdState<XpbdMotorState>>());
        using var writeJoints = joints.WriteInputs<JointConstraint, DeviceJointSource>(world, jointRows, new(), 64);
        using var writeMotors = motors.WriteInputs<JointMotor, DeviceMotorSource>(world, motorRows, new(), 64);
        using var dynamics = new DeviceBodyDynamics(backend, device, world, members, bodies, CompileBodyDynamics(), 64);
        using var results = new DeviceBodyResults(backend, device, bodies);
        var bindings = new Dictionary<string, ResourceBinding[]>();
        joints.AddBindings(bindings);
        using var project = new DeviceXpbdProjection<DeviceMotorConstraint, XpbdMotorState>(backend, device, world,
            motorRows, bodies, dynamics, motors, multipliers, CompileXpbdMotor(), 64, bindings, joints.GetReadAccesses(jobs));
        var stages = joints.BeginInputs().Then(writeJoints).Then(joints.UploadInputs())
            .Then(motors.BeginInputs()).Then(writeMotors).Then(motors.UploadInputs())
            .Then(bodies.WriteInputs(64)).Then(bodies.UploadInputs()).Then(dynamics.Predict(Vector3.Zero))
            .Then(project.Initialize()).Then(project.Iterate(8)).Then(dynamics.UpdateVelocities()).Then(results.Readback(64));

        PhysicsSimulation.Create(world, world.QueryDefinition().All<DeviceBodyBinding>().Build(), stages, 1).Schedule(9, 1, .1).Complete();

        Near(new(2.9f, 0, 0), world.Read<BodyPose>(source).Position);
        Near(-Vector3.UnitX, world.Read<BodyVelocity>(source).Linear);
        Near(new(.74285716f, 0, 0), world.Read<BodyPose>(target).Position);
        Near(new(7.4285717f, 0, 0), world.Read<BodyVelocity>(target).Linear);
        world.ReleaseQuery(motorRows);
        world.ReleaseQuery(jointRows);
        world.ReleaseQuery(members);
    }

    [Fact]
    public void DeviceJointProjectsTheOriginalFramesWithFiniteStiffnessAndAccumulatedMultipliers()
    {
        using var backend = D3D12GraphicsBackend.Create();
        using Device device = CreateDevice(backend);
        using var jobs = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using World world = WorldJobs.CreateWorld(scheduler: jobs);
        using var bodies = new DeviceBodies(backend, device, 2);
        using var joints = new DeviceConstraintData<DeviceJointConstraint>(backend, device, 1, "DeviceJoints");
        using var multipliers = new DeviceConstraintState<XpbdJointState>(backend, device, 1, "DeviceJointMultipliers");
        Entity first = AddBody(world, bodies, 0, Vector3.Zero, BodyMotion.Static);
        Entity second = AddBody(world, bodies, 1, Vector3.UnitX, BodyMotion.Dynamic);
        RelationEdge<JointConstraint> joint = world.CreateRelation(first, second,
            JointConstraints.CreateBall(JointFrame.Identity, JointFrame.Identity, 100));
        world.Add(joint.Entity, new DeviceConstraintBinding<DeviceJointConstraint>(joints, 0));
        world.Add(joint.Entity, new DeviceXpbdState<XpbdJointState>(multipliers));
        var members = world.Query(world.QueryDefinition().Read<DeviceBodyBinding>());
        var rows = world.Query(world.QueryDefinition().Read<JointConstraint>()
            .Read<DirectedRelationEndpoints<JointConstraint>>().Read<DeviceConstraintBinding<DeviceJointConstraint>>()
            .All<DeviceXpbdState<XpbdJointState>>());
        using var write = joints.WriteInputs<JointConstraint, DeviceJointSource>(world, rows, new(), 64);
        using var dynamics = new DeviceBodyDynamics(backend, device, world, members, bodies, CompileBodyDynamics(), 64);
        using var results = new DeviceBodyResults(backend, device, bodies);
        using var project = new DeviceXpbdProjection<DeviceJointConstraint, XpbdJointState>(backend, device, world,
            rows, bodies, dynamics, joints, multipliers, CompileXpbdJoint(), 64);
        var stages = joints.BeginInputs().Then(write).Then(joints.UploadInputs())
            .Then(bodies.WriteInputs(64)).Then(bodies.UploadInputs()).Then(dynamics.Predict(Vector3.Zero))
            .Then(project.Initialize()).Then(project.Iterate(8)).Then(dynamics.UpdateVelocities()).Then(results.Readback(64));

        PhysicsSimulation.Create(world, world.QueryDefinition().All<DeviceBodyBinding>().Build(), stages, 1).Schedule(0, 0, .1).Complete();

        Near(new(.5f, 0, 0), world.Read<BodyPose>(second).Position);
        Near(new(-5, 0, 0), world.Read<BodyVelocity>(second).Linear);
        Assert.Equal(Quaternion.Identity, world.Read<BodyPose>(second).Rotation);
        world.ReleaseQuery(rows);
        world.ReleaseQuery(members);
    }

    private DeviceXpbdKernels CompileXpbdJoint()
    {
        D3D12TestShader shader = shaderCompiler.CompileWithSearchPaths("xpbd_joint", """
            #include "physics_xpbd_joint.slang"
            #include "physics_xpbd.slang"
            [shader("compute")][numthreads(64,1,1)]
            void Initialize(uint3 id : SV_DispatchThreadID) { initializeDeviceXpbd<DeviceXpbdJoint>(id.x); }
            [shader("compute")][numthreads(64,1,1)]
            void Project(uint3 id : SV_DispatchThreadID) { projectDeviceXpbd<DeviceXpbdJoint>(id.x); }
            """, [new("Initialize", SlangStage.Compute), new("Project", SlangStage.Compute)], "sm_6_0", ShaderDirectory());
        ComputePipelineDesc Entry(int index) => new(shader.SlangCode, shader.Reflection,
            shader.GetEntryPoint(index), shader.GetEntryCode(index));
        return new(Entry(0), Entry(1));
    }

    private DeviceXpbdKernels CompileXpbdMotor()
    {
        D3D12TestShader shader = shaderCompiler.CompileWithSearchPaths("xpbd_motor", """
            #include "physics_xpbd_motor.slang"
            #include "physics_xpbd.slang"
            [shader("compute")][numthreads(64,1,1)]
            void Initialize(uint3 id : SV_DispatchThreadID) { initializeDeviceXpbd<DeviceXpbdMotor>(id.x); }
            [shader("compute")][numthreads(64,1,1)]
            void Project(uint3 id : SV_DispatchThreadID) { projectDeviceXpbd<DeviceXpbdMotor>(id.x); }
            """, [new("Initialize", SlangStage.Compute), new("Project", SlangStage.Compute)], "sm_6_0", ShaderDirectory());
        ComputePipelineDesc Entry(int index) => new(shader.SlangCode, shader.Reflection,
            shader.GetEntryPoint(index), shader.GetEntryCode(index));
        return new(Entry(0), Entry(1));
    }
}
