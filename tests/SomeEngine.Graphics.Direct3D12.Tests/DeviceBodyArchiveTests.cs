using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;
using SomeEngine.Physics;
using Xunit;

namespace SomeEngine.Graphics.Direct3D12.Tests;

public sealed partial class DeviceXpbdTests
{
    [Fact]
    public void TypedDeviceStatesCaptureContinuingMotionWhileCpuPoseRemainsAtItsInput()
    {
        using var backend = D3D12GraphicsBackend.Create();
        using Device device = CreateDevice(backend);
        using var jobs = new Scheduler(new JobConfiguration { WorkerCount = 2 });
        using var world = new World(execution: new WorldJobs(jobs));
        using var bodies = new DeviceBodies(backend, device, 1);
        Entity body = AddBody(world, bodies, 0, Vector3.Zero, BodyMotion.Dynamic);
        world.Set(body, new BodyVelocity { Linear = Vector3.UnitX });
        Query members = world.Query(new QueryDescription().With<DeviceBodyBinding>());
        using var inputs = new DeviceBodyInputs(world, members, bodies);
        using var dynamics = new BodyDynamicsCommands(backend, device, world, members, bodies, CompileBodyDynamics(), 64);
        using var states = new DeviceBodyResults(backend, device, bodies);
        var stages = inputs.Upload().Then(dynamics.Predict(Vector3.Zero)).Then(dynamics.UpdateVelocities()).Then(states.ReadStates());

        PhysicsSimulation.Create(world, members, stages, 1).Schedule(0, 0, .1).Complete();

        Assert.Single(states.Values.ToArray());
        Near(new(.1f, 0, 0), states.Values.Span[0].Position);
        Near(Vector3.Zero, world.ReadComponentData<BodyPose>(body).Position);
        Assert.Equal((uint)body.Id, states.Values.Span[0].EntityIndex);
        Assert.Equal((uint)body.Generation, states.Values.Span[0].EntityGeneration);
    }

    [Fact]
    public void UploadedDeviceValuesContinueAfterTheRestoredCpuFieldsHaveBeenAccepted()
    {
        using var backend = D3D12GraphicsBackend.Create();
        using Device device = CreateDevice(backend);
        using var jobs = new Scheduler(new JobConfiguration { WorkerCount = 2 });
        using var world = new World(execution: new WorldJobs(jobs));
        using var bodies = new DeviceBodies(backend, device, 1);
        Entity body = AddBody(world, bodies, 0, Vector3.Zero, BodyMotion.Dynamic);
        Query members = world.Query(new QueryDescription().With<DeviceBodyBinding>());
        using var inputs = new DeviceBodyInputs(world, members, bodies);
        using var dynamics = new BodyDynamicsCommands(backend, device, world, members, bodies, CompileBodyDynamics(), 64);
        using var results = new DeviceBodyResults(backend, device, bodies);
        PhysicsSimulation.Create(world, members, inputs.Upload().Then(results.ReadStates()), 1).Schedule(0, 0, .1).Complete();
        DeviceBodyState[] values = results.Values.ToArray();
        values[0].Position = new(7, 0, 0); values[0].LinearVelocity = new(2, 0, 0);
        values[0].Number = 9; values[0].Time = 3; values[0].InputVersion = 123;
        var upload = bodies.UploadStates(values).Then(results.ReadStates());

        PhysicsSimulation.Create(world, members, upload, 1).Schedule(9, 2.9, .1).Complete();

        Near(new(7, 0, 0), results.Values.Span[0].Position);
        Assert.Equal(9, results.Values.Span[0].Number);
        Assert.Equal(3, results.Values.Span[0].Time);
        var resumed = inputs.Upload().Then(dynamics.Predict(Vector3.Zero)).Then(dynamics.UpdateVelocities()).Then(results.Readback(64));
        PhysicsSimulation.Create(world, members, resumed, 1).Schedule(10, 3, .1).Complete();
        Near(new(7.2f, 0, 0), world.ReadComponentData<BodyPose>(body).Position);
        Near(new(2, 0, 0), world.ReadComponentData<BodyVelocity>(body).Linear);
        Assert.Equal(10, world.ReadComponentData<BodyStepState>(body).Number);
    }
}
