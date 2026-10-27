using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;
using SomeEngine.Physics;
using Xunit;

namespace SomeEngine.Graphics.Direct3D12.Tests;

public sealed partial class DeviceXpbdTests
{
    [Fact]
    public void GravityKeepsTheCompletedDeviceBodyMomentum()
    {
        using var backend = D3D12GraphicsBackend.Create();
        using Device device = CreateDevice(backend);
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using World world = WorldJobs.CreateWorld(scheduler: scheduler);
        using var bodies = new DeviceBodies(backend, device, 1);
        var body = AddBody(world, bodies, 0, new(0, 0, 10), BodyMotion.Dynamic);
        QueryHandle members = world.Query(world.QueryDefinition().Read<DeviceBodyBinding>());
        using var inputs = new DeviceBodyInputs(world, members, bodies);
        using var dynamics = new DeviceBodyDynamics(backend, device, world, members, bodies, CompileBodyDynamics(), 64);
        using var results = new DeviceBodyResults(backend, device, bodies);
        var stages = inputs.Upload().Then(dynamics.Predict(new(0, 0, -10)))
            .Then(dynamics.UpdateVelocities()).Then(results.Readback(64));
        var simulation = PhysicsSimulation.Create(world, QueryDefinition.Empty, stages, substeps: 1);
        const float duration = 1f / 60;
        for (int number = 0; number < 211; number++)
            simulation.Schedule(number, number * (double)duration, duration).Complete();

        Vector3 momentum = world.Read<BodyVelocity>(body).Linear;
        Vector3 expected = new(0, 0, -10 * 211 * duration);
        float error = Vector3.Distance(momentum, expected);
        Assert.True(error <= 1e-4f, $"Completed momentum {momentum}; gravity momentum {expected}; error {error:R}.");
        world.ReleaseQuery(members);
    }
}
