using System.Numerics;
using SomeEngine.ECS;
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
    public void ANewDeviceConstraintAtRestWakesOnlyItsConnectedSleepingIsland()
    {
        using var backend = D3D12GraphicsBackend.Create();
        using Device device = CreateDevice(backend);
        using var jobs = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using World world = WorldJobs.CreateWorld(scheduler: jobs);
        using var bodies = new DeviceBodies(backend, device, 3);
        var anchor = AddBody(world, bodies, 0, Vector3.Zero, BodyMotion.Static);
        var first = AddBody(world, bodies, 1, Vector3.UnitX, BodyMotion.Dynamic);
        var other = AddBody(world, bodies, 2, new(10, 0, 0), BodyMotion.Dynamic);
        using var lengths = new DeviceConstraintData<DeviceLengthConstraint>(backend, device, 1, "DeviceLengths");
        using var multipliers = new DeviceConstraintState<float>(backend, device, 1, "DeviceLengthMultipliers");
        QueryHandle members = world.Query(world.QueryDefinition().Read<DeviceBodyBinding>());
        QueryHandle rows = world.Query(world.QueryDefinition().Read<XpbdLength>().Read<UndirectedRelationEndpoints<XpbdLength>>()
            .Read<DeviceConstraintBinding<DeviceLengthConstraint>>().All<DeviceXpbdState<float>>());
        using var activities = new DeviceBodyActivities(backend, device, world, members, bodies);
        foreach (var body in new[] { first, other })
        { world.Add(body, new BodyActivity { Radius = 1 }); world.Add(body, new DeviceBodyActivityBinding(activities)); }
        var activityBindings = new Dictionary<string, ResourceBinding[]>(); activities.AddBindings(activityBindings);
        using var dynamics = new DeviceBodyDynamics(backend, device, world, members, bodies, CompileActiveBodyDynamics(), 64,
            activityBindings, activities.GetWriteAccesses(jobs));
        using var bodyInputs = new DeviceBodyInputs(world, members, bodies);
        using var output = new PhysicsEventBuffer(jobs, 8);
        using var inputs = lengths.WriteInputs<XpbdLength, DeviceLengthSource>(world, rows, default, 64);
        using var selected = new DeviceConstraintSelection<XpbdLength, DeviceLengthConstraint, DeviceXpbdStateRows<float>>(
            backend, device, world, rows, lengths, new(multipliers), "DeviceLengthsSelection");
        var bindings = new Dictionary<string, ResourceBinding[]>(activityBindings);
        lengths.AddBindings(bindings); multipliers.AddBindings(bindings);
        JobResourceAccess[] reads = [.. lengths.GetReadAccesses(jobs), .. activities.GetReadAccesses(jobs)];
        using var graph = new DeviceConstraintGraph(backend, device, bodies, dynamics, CompileActiveLengthGraph(),
            new(1, 2), bindings, reads, 64, new[] { selected });
        using var solve = new DeviceXpbdSolver(backend, device, bodies, graph, CompileGraphLengthProjection(), new(4, 1),
            bindings, reads, new[] { multipliers.States }, 64);
        using var activity = new DevicePhysicsActivity(backend, device, activities, graph, CompileLengthActivityKernels(),
            new(.05f, .25f, .01f, .01f), bindings, reads, 64);
        var stages = bodyInputs.Upload().Then(lengths.BeginInputs()).Then(inputs).Then(lengths.UploadInputs())
            .Then(activity.UploadInputs()).Then(dynamics.UpdateSelection()).Then(activity.WakeConnected())
            .Then(dynamics.Predict(Vector3.Zero)).Then(solve.Solve()).Then(dynamics.UpdateVelocities())
            .Then(activity.Finish()).Then(activity.Readback(output));
        var physics = PhysicsSimulation.Create(world, QueryDefinition.Empty, stages, 1);
        physics.Schedule(0, 0, .125).Complete(); physics.Schedule(1, .125, .125).Complete();
        Assert.True(world.Read<BodyActivity>(first).Sleeping); Assert.True(world.Read<BodyActivity>(other).Sleeping);
        var edge = world.CreateRelation(anchor, first, new XpbdLength { RestLength = 1 }).Entity;
        world.Add(edge, new DeviceConstraintBinding<DeviceLengthConstraint>(lengths, 0));
        world.Add(edge, new DeviceXpbdState<float>(multipliers));

        physics.Schedule(2, .25, .125).Complete();

        Assert.False(world.Read<BodyActivity>(first).Sleeping);
        Assert.True(world.Read<BodyActivity>(other).Sleeping);
        world.ReleaseQuery(rows); world.ReleaseQuery(members);
    }
}
