using System.Numerics;
using SomeEngine.ECS;
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
        using var jobs = new Scheduler(new JobConfiguration { WorkerCount = 2 });
        using World world = new World(execution: new WorldJobs(jobs));
        using var bodies = new DeviceBodies(backend, device, 3);
        var anchor = AddBody(world, bodies, 0, Vector3.Zero, BodyMotion.Static);
        var first = AddBody(world, bodies, 1, Vector3.UnitX, BodyMotion.Dynamic);
        var other = AddBody(world, bodies, 2, new(10, 0, 0), BodyMotion.Dynamic);
        using var lengths = new DeviceConstraintData<DeviceLengthConstraint>(backend, device, 1, "DeviceLengths");
        using var multipliers = new DeviceConstraintState<float>(backend, device, 1, "DeviceLengthMultipliers");
        Query members = world.Query(new QueryDescription().WithStored<DeviceBodyBinding>());
        Query rows = world.Query(new QueryDescription().WithStored<XpbdLength>().WithStored<RelationEndpoints<XpbdLength>>()
            .WithStored<DeviceConstraintBinding<DeviceLengthConstraint>>().WithStored<DeviceXpbdState<float>>());
        using var activities = new DeviceBodyActivities(backend, device, world, members, bodies);
        foreach (var body in new[] { first, other })
        { world.Add(body, new BodyActivity { Radius = 1 }); world.Add(body, new DeviceBodyActivityBinding(activities)); }
        var activityBindings = new Dictionary<string, ResourceBinding[]>(); activities.AddBindings(activityBindings);
        using var dynamics = new BodyDynamicsCommands(backend, device, world, members, bodies, CompileActiveBodyDynamics(), 64,
            activityBindings, activities.GetWriteAccesses(jobs));
        using var bodyInputs = new DeviceBodyInputs(world, members, bodies);
        using var results = new DeviceBodyResults(backend, device, bodies);
        using var output = new PhysicsEventBuffer(world, 8);
        using var inputs = lengths.WriteInputs<XpbdLength, DeviceLengthSource>(world, rows, default, 64);
        using var selected = new DeviceConstraintSelection<XpbdLength, DeviceLengthConstraint, DeviceXpbdStateRows<float>>(
            backend, device, world, rows, lengths, new(multipliers), "DeviceLengthsSelection");
        var bindings = new Dictionary<string, ResourceBinding[]>(activityBindings);
        lengths.AddBindings(bindings); multipliers.AddBindings(bindings);
        JobResourceAccess[] reads = [.. lengths.GetReadAccesses(jobs), .. activities.GetReadAccesses(jobs)];
        using var graph = new DeviceConstraintGraph(backend, device, bodies, dynamics, CompileActiveLengthGraph(),
            new(1, 2), bindings, reads, 64, new[] { selected });
        using var solve = new XpbdGraphCommands(backend, device, bodies, graph, CompileGraphLengthProjection(), new(4, 1),
            bindings, reads, new[] { multipliers.States }, 64);
        using var activity = new DevicePhysicsActivity(backend, device, activities, graph, CompileLengthActivityKernels(),
            new(.05f, .25f, .01f, .01f), bindings, reads, 64);
        var stages = bodyInputs.Upload().Then(inputs).Then(lengths.UploadInputs())
            .Then(activity.UploadInputs()).Then(dynamics.UpdateSelection()).Then(graph.BuildConnectivity())
            .Then(activity.WakeConnected(buildGraph: false)).Then(dynamics.Predict(Vector3.Zero))
            .Then(graph.BuildConnectivity()).Then(activity.WakeConnected(buildGraph: false)).Then(graph.UpdateActiveWork())
            .Then(solve.Initialize(buildGraph: false)).Then(solve.Iterate(4, buildGraph: false)).Then(dynamics.UpdateVelocities())
            .Then(activity.Finish()).Then(activity.Readback(output)).Then(results.Readback(64));
        var physics = PhysicsSimulation.Create(world, world.Query(new QueryDescription()), stages, 1);
        physics.Schedule(0, 0, .125).Complete(); physics.Schedule(1, .125, .125).Complete();
        Assert.True(world.ReadComponentData<BodyActivity>(first).Sleeping); Assert.True(world.ReadComponentData<BodyActivity>(other).Sleeping);
        var edge = world.CreateRelation(anchor, first, new XpbdLength { RestLength = .5f }).Entity;
        world.Add(edge, new DeviceConstraintBinding<DeviceLengthConstraint>(lengths, 0));
        world.Add(edge, new DeviceXpbdState<float>(multipliers));

        physics.Schedule(2, .25, .125).Complete();

        Assert.False(world.ReadComponentData<BodyActivity>(first).Sleeping);
        Assert.True(world.ReadComponentData<BodyActivity>(other).Sleeping);
        Near(new(.5f, 0, 0), world.ReadComponentData<BodyPose>(first).Position);
        Near(new(-4, 0, 0), world.ReadComponentData<BodyVelocity>(first).Linear);
        Near(new(10, 0, 0), world.ReadComponentData<BodyPose>(other).Position);
         
    }
}
