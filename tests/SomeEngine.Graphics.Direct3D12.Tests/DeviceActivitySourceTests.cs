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
    public void AnActivityEditAfterDeviceFinishingControlsTheNextPrediction()
    {
        using var backend = D3D12GraphicsBackend.Create();
        using Device device = CreateDevice(backend);
        using var jobs = new Scheduler(new JobConfiguration { WorkerCount = 2 });
        using World world = new World(execution: new WorldJobs(jobs));
        using var bodies = new DeviceBodies(backend, device, 4);
        Entity first = AddBody(world, bodies, 1, Vector3.Zero, BodyMotion.Dynamic);
        Entity other = AddBody(world, bodies, 3, new(10, 0, 0), BodyMotion.Dynamic);
        world.Add(first, new BodyActivity { Radius = 1 }); world.Add(other, new BodyActivity { Radius = 1 });
        Query members = world.Query(new QueryDescription().WithStored<DeviceBodyBinding>());
        Query edits = world.Query(new QueryDescription().With<BodyActivity>());
        JobResourceAccess[] editAccesses = ((WorldJobs)world.Execution!).CollectAccess(new SystemAccesses().WriteRandom<BodyActivity>());
        using var activities = new DeviceBodyActivities(backend, device, world, members, bodies);
        world.Add(first, new DeviceBodyActivityBinding(activities)); world.Add(other, new DeviceBodyActivityBinding(activities));
        var bindings = new Dictionary<string, ResourceBinding[]>(); activities.AddBindings(bindings);
        using var dynamics = new BodyDynamicsCommands(backend, device, world, members, bodies, CompileActiveBodyDynamics(), 64,
            bindings, activities.GetWriteAccesses(jobs));
        using var results = new DeviceBodyResults(backend, device, bodies);
        using var bodyInputs = new DeviceBodyInputs(world, members, bodies);
        using var output = new PhysicsEventBuffer(world, 8);
        using var graph = new DeviceConstraintGraph(backend, device, bodies, dynamics, CompileEmptyActivityGraph(),
            new(1, 1), bindings, activities.GetReadAccesses(jobs), 64);
        using var activity = new DevicePhysicsActivity(backend, device, activities, graph, CompileEmptyActivityKernels(),
            new(.05f, .2f, .01f, .01f), bindings, default, 64);
        var stages = bodyInputs.Upload().Then(activity.UploadInputs()).Then(dynamics.UpdateSelection()).Then(activity.WakeConnected())
            .Then(dynamics.Predict(new(.01f, 0, 0))).Then(dynamics.UpdateVelocities()).Then(activity.Finish())
            .Then(new EditActivityAtStep(edits, first, 2, editAccesses))
            .Then(activity.Readback(output)).Then(results.Readback(64));
        var physics = PhysicsSimulation.Create(world, world.Query(new QueryDescription()), stages, 1);
        physics.Schedule(0, 0, .1).Complete(); physics.Schedule(1, .1, .1).Complete();
        Assert.True(world.ReadComponentData<BodyActivity>(first).Sleeping); Assert.True(world.ReadComponentData<BodyActivity>(other).Sleeping);

        physics.Schedule(2, .2, .1).Complete();
        Assert.False(world.ReadComponentData<BodyActivity>(first).Sleeping);
        Assert.Equal(2, world.ReadComponentData<BodyActivity>(first).Radius);
        Assert.InRange(world.ReadComponentData<BodyPose>(first).Position.X, .000299f, .000301f);

        physics.Schedule(3, .3, .1).Complete();

        Assert.False(world.ReadComponentData<BodyActivity>(first).Sleeping); Assert.True(world.ReadComponentData<BodyActivity>(other).Sleeping);
        Assert.Equal(2, world.ReadComponentData<BodyActivity>(first).Radius);
        Assert.InRange(world.ReadComponentData<BodyPose>(first).Position.X, .000399f, .000401f);
        Assert.InRange(world.ReadComponentData<BodyPose>(other).Position.X, 10.00029f, 10.00031f);
        Assert.Equal(3, world.ReadComponentData<BodyStepState>(first).Number);
        Assert.Equal(.4, world.ReadComponentData<BodyStepState>(first).Time, 10);
         
    }

    private readonly struct EditActivityAtStep(Query rows, Entity body, long number,
        JobResourceAccess[] accesses) : IPhysicsStage
    {
        public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => context.Number == number
            ? WorldJobs.GetScheduler(context.World).Schedule(new EditActivity(context.World, rows, body), accesses, dependency)
            : dependency;
    }

    private readonly struct EditActivity(World world, Query rows, Entity body) : IJob
    {
        public void Execute()
        {
            ref BodyActivity activity = ref world.Get<BodyActivity>(body);
            activity.Sleeping = false; activity.QuietSeconds = 0; activity.Radius = 2;
        }
    }
}
