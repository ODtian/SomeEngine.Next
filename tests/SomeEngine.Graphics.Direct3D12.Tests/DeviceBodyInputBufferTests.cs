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
    public void TimedForcesAndImpulsesUseTheCurrentResidentVelocityMassAndRotatedInertia()
    {
        using var backend = D3D12GraphicsBackend.Create();
        using Device device = CreateDevice(backend);
        using var jobs = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using World world = WorldJobs.CreateWorld(scheduler: jobs);
        using var bodies = new DeviceBodies(backend, device, 3);
        Entity body = AddBody(world, bodies, 2, Vector3.Zero, BodyMotion.Dynamic);
        world.Replace(body, new BodyVelocity { Linear = Vector3.UnitX, Angular = Vector3.UnitZ * MathF.PI });
        QueryHandle members = world.Query(world.QueryDefinition().Read<DeviceBodyBinding>());
        using var inputs = new DeviceBodyInputs(world, members, bodies);
        using var dynamics = new DeviceBodyDynamics(backend, device, world, members, bodies, CompileBodyDynamics(), 64);
        using var results = new DeviceBodyResults(backend, device, bodies, world, members, capacity: 1);
        var first = inputs.Upload().Then(dynamics.Predict(new(10, 0, 0))).Then(dynamics.UpdateVelocities());
        PhysicsSimulation.Create(world, QueryDefinition.Empty, first, 1).Schedule(0, 0, .1).Complete();
        var mass = new BodyMass { Motion = BodyMotion.Dynamic, InverseMass = .5f,
            InverseInertia = Matrix4x4.CreateScale(.25f, .5f, 1) };
        world.Replace(body, mass);
        world.AddBuffer<BodyForce>(body);
        world.ExecuteBufferWrite<BodyForce>(body, values => values.Add(new()
            { Force = new(10, 0, 0), Torque = new(0, 0, 4), StartTime = .15, EndTime = .25 }));
        world.AddBuffer<BodyImpulse>(body);
        world.ExecuteBufferWrite<BodyImpulse>(body, values => values.Add(new()
            { Linear = new(2, 0, 0), Angular = new(2, 0, 0), Time = .15 }));
        using var timed = new DeviceBodyInputBuffers(backend, device, world, members, bodies, CompileBodyInputBuffers(), 64);
        var second = inputs.Upload().Then(timed.WriteInputs()).Then(timed.Apply())
            .Then(dynamics.Predict(Vector3.Zero)).Then(dynamics.UpdateVelocities()).Then(results.Readback(64));

        PhysicsSimulation.Create(world, QueryDefinition.Empty, second, 1).Schedule(1, .1, .1).Complete();

        Quaternion priorRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI * .1f);
        Vector3 angular = Vector3.UnitZ * MathF.PI + mass.ApplyInverseInertia(priorRotation, new(2, 0, .2f));
        Quaternion expectedRotation = Quaternion.CreateFromAxisAngle(Vector3.Normalize(angular), angular.Length() * .1f) * priorRotation;
        Near(new(.525f, 0, 0), world.Read<BodyPose>(body).Position);
        Near(new(3.25f, 0, 0), world.Read<BodyVelocity>(body).Linear);
        Near(angular, world.Read<BodyVelocity>(body).Angular);
        Assert.InRange(MathF.Abs(Quaternion.Dot(expectedRotation, world.Read<BodyPose>(body).Rotation)), .999999f, 1.000001f);

        PhysicsSimulation.Create(world, QueryDefinition.Empty, second, 1).Schedule(2, .2, .1).Complete();

        Near(new(.875f, 0, 0), world.Read<BodyPose>(body).Position);
        Near(new(3.5f, 0, 0), world.Read<BodyVelocity>(body).Linear);
        Near(angular + mass.ApplyInverseInertia(expectedRotation, new(0, 0, .2f)), world.Read<BodyVelocity>(body).Angular);
        world.ReleaseQuery(members);
    }

    [Fact]
    public void VelocityTargetsSelectTheLatestOriginalInputInEachDeviceSubstep()
    {
        using var backend = D3D12GraphicsBackend.Create();
        using Device device = CreateDevice(backend);
        using var jobs = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using World world = WorldJobs.CreateWorld(scheduler: jobs);
        using var bodies = new DeviceBodies(backend, device, 3);
        Entity body = AddBody(world, bodies, 2, Vector3.Zero, BodyMotion.Dynamic);
        world.Replace(body, new BodyVelocity { Linear = Vector3.UnitX });
        world.AddBuffer<BodyVelocityTarget>(body);
        world.ExecuteBufferWrite<BodyVelocityTarget>(body, values =>
        {
            values.Add(new() { Linear = new(3, 0, 0), Angular = new(0, 0, 2), Time = .15 });
            values.Add(new() { Linear = new(2, 0, 0), Angular = Vector3.UnitZ, Time = .12 });
            values.Add(new() { Linear = -Vector3.UnitX, Angular = -Vector3.UnitZ, Time = .22 });
        });
        QueryHandle members = world.Query(world.QueryDefinition().Read<DeviceBodyBinding>());
        using var inputs = new DeviceBodyInputs(world, members, bodies);
        using var timed = new DeviceBodyInputBuffers(backend, device, world, members, bodies, CompileBodyInputBuffers(), 64);
        using var dynamics = new DeviceBodyDynamics(backend, device, world, members, bodies, CompileBodyDynamics(), 64);
        using var results = new DeviceBodyResults(backend, device, bodies, world, members, capacity: 1);
        var stages = inputs.Upload().Then(timed.WriteInputs()).Then(timed.Apply())
            .Then(dynamics.Predict(Vector3.Zero)).Then(dynamics.UpdateVelocities());
        var simulation = PhysicsSimulation.Create(world, QueryDefinition.Empty, stages, 1);

        simulation.Schedule(0, 0, .1).Complete();
        simulation.Schedule(1, .1, .1).Complete();
        PhysicsStepResult completed = simulation.Schedule(2, .2, .1);
        var context = new PhysicsStepContext(world, QueryDefinition.Empty, 2, 0, 1, .2, .1);
        results.Readback(64).Schedule(in context, completed.Completion).Complete();

        Near(new(.3f, 0, 0), world.Read<BodyPose>(body).Position);
        Near(-Vector3.UnitX, world.Read<BodyVelocity>(body).Linear);
        Near(-Vector3.UnitZ, world.Read<BodyVelocity>(body).Angular);
        Assert.InRange(MathF.Abs(Quaternion.Dot(Quaternion.CreateFromAxisAngle(Vector3.UnitZ, .1f),
            world.Read<BodyPose>(body).Rotation)), .999999f, 1.000001f);
        world.ReleaseQuery(members);
    }

    [Fact]
    public void KinematicTargetsSampleTheOriginalTrajectoryAndHoldItsFinalDevicePose()
    {
        using var backend = D3D12GraphicsBackend.Create();
        using Device device = CreateDevice(backend);
        using var jobs = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using World world = WorldJobs.CreateWorld(scheduler: jobs);
        using var bodies = new DeviceBodies(backend, device, 2);
        Entity body = AddBody(world, bodies, 1, Vector3.Zero, BodyMotion.Kinematic);
        world.Replace(body, new BodyVelocity { Linear = new(9, 0, 0) });
        world.AddBuffer<BodyKinematicTarget>(body);
        world.ExecuteBufferWrite<BodyKinematicTarget>(body, values => values.Add(new()
        {
            StartPose = new() { Rotation = Quaternion.Identity },
            EndPose = new() { Position = new(2, 4, 0), Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2) },
            StartTime = 0, EndTime = .2,
        }));
        QueryHandle members = world.Query(world.QueryDefinition().Read<DeviceBodyBinding>());
        using var inputs = new DeviceBodyInputs(world, members, bodies);
        using var timed = new DeviceBodyInputBuffers(backend, device, world, members, bodies, CompileBodyInputBuffers(), 64);
        using var dynamics = new DeviceBodyDynamics(backend, device, world, members, bodies, CompileBodyDynamics(), 64);
        using var results = new DeviceBodyResults(backend, device, bodies, world, members, capacity: 1);
        var stages = inputs.Upload().Then(timed.WriteInputs()).Then(timed.Apply())
            .Then(dynamics.Predict(Vector3.Zero)).Then(timed.ApplyKinematicTargets())
            .Then(dynamics.UpdateVelocities()).Then(results.Readback(64));
        var simulation = PhysicsSimulation.Create(world, QueryDefinition.Empty, stages, 1);

        simulation.Schedule(0, 0, .1).Complete();

        Near(new(1, 2, 0), world.Read<BodyPose>(body).Position);
        Near(new(10, 20, 0), world.Read<BodyVelocity>(body).Linear);
        Near(new(0, 0, MathF.PI / .4f), world.Read<BodyVelocity>(body).Angular);

        simulation.Schedule(1, .1, .1).Complete();
        simulation.Schedule(2, .2, .1).Complete();

        Near(new(2, 4, 0), world.Read<BodyPose>(body).Position);
        Near(Vector3.Zero, world.Read<BodyVelocity>(body).Linear);
        Near(Vector3.Zero, world.Read<BodyVelocity>(body).Angular);
        Assert.InRange(MathF.Abs(Quaternion.Dot(Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2),
            world.Read<BodyPose>(body).Rotation)), .999999f, 1.000001f);
        world.ReleaseQuery(members);
    }

    private DeviceBodyInputKernels CompileBodyInputBuffers()
    {
        D3D12TestShader shader = shaderCompiler.CompileWithSearchPaths("body_timed_inputs", "#include \"physics_body_inputs.slang\"",
            [new("ApplyDeviceBodyInputs", SlangStage.Compute), new("ApplyDeviceKinematicTargets", SlangStage.Compute)], "sm_6_0", ShaderDirectory());
        ComputePipelineDesc Entry(int index) => new(shader.SlangCode, shader.Reflection, shader.GetEntryPoint(index), shader.GetEntryCode(index));
        return new(Entry(0), Entry(1));
    }
}
