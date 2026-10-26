using System.Collections.Concurrent;
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
    public async Task BodySelectionPrecedesMotionWhileTheDeviceStateHasAnActiveWriter()
    {
        using var backend = D3D12GraphicsBackend.Create();
        using Device device = CreateDevice(backend);
        using var jobs = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using World world = WorldJobs.CreateWorld(scheduler: jobs);
        using var bodies = new DeviceBodies(backend, device, 4);
        var body = AddBody(world, bodies, 3, Vector3.UnitX, BodyMotion.Dynamic);
        world.Replace(body, new BodyVelocity { Linear = new(2, 0, 0) });
        QueryHandle members = world.Query(world.QueryDefinition().Read<DeviceBodyBinding>());
        using var inputs = new DeviceBodyInputs(world, members, bodies);
        using var dynamics = new DeviceBodyDynamics(backend, device, world, members, bodies, CompileBodyDynamics(), 64);
        using var results = new DeviceBodyResults(backend, device, bodies, world, members, 1);
        var context = new PhysicsStepContext(world, QueryDefinition.Empty, 4, 0, 1, 2, .1);
        inputs.Upload().Schedule(in context, default).Complete();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        JobHandle writer = jobs.CreateExternalFenceHandle(release.Task, [bodies.GetWriteAccess(jobs)]);
        var events = new ConcurrentQueue<string>();
        JobHandle selected = dynamics.UpdateSelection().Schedule(in context, default);
        async Task SelectBeforeRelease()
        {
            await selected.WaitAsync();
            events.Enqueue("selection-ready");
            release.SetResult();
            events.Enqueue("device-state-released");
        }
        try { await SelectBeforeRelease().WaitAsync(TimeSpan.FromSeconds(20)); }
        finally { release.TrySetResult(); }
        selected.Complete(); writer.Complete();

        dynamics.Predict(Vector3.Zero).Then(dynamics.UpdateVelocities()).Then(results.Readback(64))
            .Schedule(in context, selected).Complete();

        Assert.Equal(["selection-ready", "device-state-released"], events.ToArray());
        Near(new(1.2f, 0, 0), world.Read<BodyPose>(body).Position);
        Near(new(2, 0, 0), world.Read<BodyVelocity>(body).Linear);
        Assert.Equal(4, world.Read<BodyStepState>(body).Number);
        world.ReleaseQuery(members);
    }
}
