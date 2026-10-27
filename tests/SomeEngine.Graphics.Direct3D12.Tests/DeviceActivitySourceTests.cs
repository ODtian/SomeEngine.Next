using System.Numerics;
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
    public void AnActivityEditAfterDeviceFinishingControlsTheNextPrediction()
    {
        using var backend = D3D12GraphicsBackend.Create();
        using Device device = CreateDevice(backend);
        using var jobs = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using World world = WorldJobs.CreateWorld(scheduler: jobs);
        using var bodies = new DeviceBodies(backend, device, 4);
        Entity first = AddBody(world, bodies, 1, Vector3.Zero, BodyMotion.Dynamic);
        Entity other = AddBody(world, bodies, 3, new(10, 0, 0), BodyMotion.Dynamic);
        world.Add(first, new BodyActivity { Radius = 1 }); world.Add(other, new BodyActivity { Radius = 1 });
        QueryHandle members = world.Query(world.QueryDefinition().Read<DeviceBodyBinding>());
        QueryDefinition editDefinition = world.QueryDefinition().ReadWrite<BodyActivity>().Build();
        QueryHandle edits = world.Query(editDefinition);
        var editAccesses = new JobResourceAccess[QueryJobAccess.Count(editDefinition)];
        QueryJobAccess.CopyAccesses(world, editDefinition, editAccesses);
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
            new(.05f, .2f, .01f, .01f), bindings, default, 64);
        var stages = bodyInputs.Upload().Then(activity.UploadInputs()).Then(dynamics.UpdateSelection()).Then(activity.WakeConnected())
            .Then(dynamics.Predict(new(.01f, 0, 0))).Then(dynamics.UpdateVelocities()).Then(activity.Finish())
            .Then(new EditActivityAtStep(edits, first, 2, editAccesses))
            .Then(activity.Readback(output)).Then(results.Readback(64));
        var physics = PhysicsSimulation.Create(world, QueryDefinition.Empty, stages, 1);
        physics.Schedule(0, 0, .1).Complete(); physics.Schedule(1, .1, .1).Complete();
        Assert.True(world.Read<BodyActivity>(first).Sleeping); Assert.True(world.Read<BodyActivity>(other).Sleeping);

        physics.Schedule(2, .2, .1).Complete();
        Assert.False(world.Read<BodyActivity>(first).Sleeping);
        Assert.Equal(2, world.Read<BodyActivity>(first).Radius);
        Assert.InRange(world.Read<BodyPose>(first).Position.X, .000299f, .000301f);

        physics.Schedule(3, .3, .1).Complete();

        Assert.False(world.Read<BodyActivity>(first).Sleeping); Assert.True(world.Read<BodyActivity>(other).Sleeping);
        Assert.Equal(2, world.Read<BodyActivity>(first).Radius);
        Assert.InRange(world.Read<BodyPose>(first).Position.X, .000399f, .000401f);
        Assert.InRange(world.Read<BodyPose>(other).Position.X, 10.00029f, 10.00031f);
        Assert.Equal(3, world.Read<BodyStepState>(first).Number);
        Assert.Equal(.4, world.Read<BodyStepState>(first).Time, 10);
        world.ReleaseQuery(edits); world.ReleaseQuery(members);
    }

    private readonly struct EditActivityAtStep(QueryHandle rows, Entity body, long number,
        JobResourceAccess[] accesses) : IPhysicsStage
    {
        public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => context.Number == number
            ? WorldJobs.GetScheduler(context.World).Schedule(new EditActivity(context.World, rows, body), accesses, dependency)
            : dependency;
    }

    private readonly struct EditActivity(World world, QueryHandle rows, Entity body) : IJob
    {
        public void Execute()
        {
            Entity target = body;
            world.ExecuteQuery(rows, ref target, static (QueryCursor rows, ref Entity target) =>
            {
                foreach (QueryRow row in rows.Rows)
                    if (row.Entity == target)
                    {
                        ref BodyActivity activity = ref row.ReadWrite<BodyActivity>();
                        activity.Sleeping = false; activity.QuietSeconds = 0; activity.Radius = 2;
                    }
            });
        }
    }
}
