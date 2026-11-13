using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;
using SomeEngine.Physics;
using Xunit;

namespace SomeEngine.Graphics.Direct3D12.Tests;

public sealed partial class DeviceAvbdTests
{
    [Fact]
    public void RestitutionUpdatesBothVelocityAxesAtSharedBodyContacts()
    {
        using var backend = D3D12GraphicsBackend.Create();
        using Device device = CreateDevice(backend);
        using var jobs = new Scheduler(new() { WorkerCount = 2 });
        using World world = new(execution: new WorldJobs(jobs));
        using var bodies = new DeviceBodies(backend, device, 3);
        using var equations = new DeviceAvbdBodyEquations(backend, device, bodies);
        using var grid = new DeviceCollisionGrid(backend, device, CompileHistoryGrid(), new(1, 3), new(3, 512, 521, 8, 1));
        using var geometry = new DeviceCollisionGeometry(backend, device, new(3, 0, 0, 0), bodies);
        Entity left = CreateBody(world, bodies, equations, 0, new(-.01f, 1.99f, 0), BodyMotion.Static);
        Entity bottom = CreateBody(world, bodies, equations, 1, new(1.99f, -.01f, 0), BodyMotion.Static);
        Entity moving = CreateBody(world, bodies, equations, 2, new(1.99f, 1.99f, 0), BodyMotion.Dynamic);
        world.Set(moving, new BodyVelocity { Linear = new(-1, -1, 0) });
        foreach (var (entity, index) in new[] { (left, 0), (bottom, 1), (moving, 2) })
        {
            world.Add(entity, new SphereGeometry(1));
            world.Add(entity, new ContactMaterial { NormalStiffness = 100, Restitution = .5f, RestitutionThreshold = .1f });
            world.Add(entity, new DeviceCollisionBody(grid, index));
            world.Add(entity, new DeviceGeometryBinding(geometry, index));
        }
        Query members = world.Query(new QueryDescription().WithStored<DeviceBodyBinding>());
        Query spheres = world.Query(new QueryDescription().WithStored<SphereGeometry>());
        var pairs = world.Within(spheres);
        using var contacts = new DeviceContactManifolds<DeviceAvbdContactState>(backend, device, new(8, 8, 16),
            new() { NormalPenalty = 100, TangentPenalty = 100 });
        using var detection = new DeviceCollisionDetection(backend, device, grid, geometry, CompileHistoryReferencedDetection(), 0,
            new(8, 4, 8) { Contacts = 8, BodiesPerContact = 2 }, new(1e-5f, 32, 64), .02f, DeviceInputTransfer.Resident, DeviceCollisionReadback.None);
        using var matching = new DeviceContactMatches<DeviceAvbdContactState>(backend, device, contacts, detection, bodies,
            CompileContactMatching(), new(.25f, .99f, true), new() { NormalStiffness = 100, Restitution = .5f, RestitutionThreshold = .1f }, 17);
        using var stateInputs = new ContactStateInitialization<DeviceAvbdContactState>(backend, device, matching, CompileHistoryStateInitialization());
        using var dynamics = new BodyDynamicsCommands(backend, device, world, members, bodies, CompileDynamics(), 64);
        using var bodyInputs = new DeviceBodyInputs(world, members, bodies);
        using var sphereInputs = grid.WriteGeometryReferences<SphereGeometry, DeviceComponentSource<SphereGeometry, DeviceSphereReader>>(pairs, new(), 0, 64);
        using var update = new UpdateDeviceGeometry(backend, device, grid, geometry, CompileHistoryGeometryUpdate(), 0,
            DeviceInputTransfer.Upload, contactDistance: .02f);
        using var results = new DeviceBodyResults(backend, device, bodies);
        var bindings = new Dictionary<string, ResourceBinding[]>(); contacts.Current.AddBindings(bindings);
        JobResourceAccess[] reads = [];
        using var graph = new DeviceConstraintGraph(backend, device, bodies, dynamics, CompileHistoryAvbdGraph(), new(8, 2), bindings, reads, 64, contacts: contacts);
        using var solve = new AvbdGraphCommands(backend, device, bodies, equations, graph, CompileContactAvbd(), new(1, 1, 1, 0), bindings, reads, 64);
        using var response = new DeviceContactActions(backend, device, bodies, graph, contacts, CompileContactActions(), bindings, reads, 64,
            Array.Empty<IDeviceConstraintSelection>());
        var stages = bodyInputs.Upload().Then(grid.BeginInputs()).Then(sphereInputs).Then(update.Update())
            .Then(grid.Build(pairs, DevicePairRules.None, DeviceInputTransfer.Resident, DevicePairReadback.None)).Then(detection.Detect())
            .Then(matching.Match()).Then(stateInputs.Initialize()).Then(dynamics.Predict(Vector3.Zero))
            .Then(graph.Build()).Then(response.Initialize()).Then(solve.Solve(buildGraph: false))
            .Then(dynamics.UpdateVelocities()).Then(response.ApplyRestitution(1, buildGraph: false));
        PhysicsStepResult result = PhysicsSimulation.Create(world, world.Query(new QueryDescription()), stages, 1).Schedule(0, 0, .1);
        var context = new PhysicsStepContext(world, world.Query(new QueryDescription()), 0, 0, 1, 0, .1);
        results.Readback(64).Schedule(in context, result.Completion).Complete();
        Vector3 velocity = world.ReadComponentData<BodyVelocity>(moving).Linear;
        Assert.InRange(velocity.X, .4999f, .5001f);
        Assert.InRange(velocity.Y, .4999f, .5001f);
    }
}
