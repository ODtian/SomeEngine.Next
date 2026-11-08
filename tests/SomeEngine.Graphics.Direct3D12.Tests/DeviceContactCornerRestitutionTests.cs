using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
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
        using World world = WorldJobs.CreateWorld(scheduler: jobs);
        using var bodies = new DeviceBodies(backend, device, 3);
        using var equations = new DeviceAvbdBodyEquations(backend, device, bodies);
        using var grid = new DeviceCollisionGrid(backend, device, CompileHistoryGrid(), new(1, 3), new(3, 512, 521, 8, 1));
        using var geometry = new DeviceCollisionGeometry(backend, device, new(3, 0, 0, 0), bodies);
        Entity left = CreateBody(world, bodies, equations, 0, new(-.01f, 1.99f, 0), BodyMotion.Static);
        Entity bottom = CreateBody(world, bodies, equations, 1, new(1.99f, -.01f, 0), BodyMotion.Static);
        Entity moving = CreateBody(world, bodies, equations, 2, new(1.99f, 1.99f, 0), BodyMotion.Dynamic);
        world.Replace(moving, new BodyVelocity { Linear = new(-1, -1, 0) });
        foreach (var (entity, index) in new[] { (left, 0), (bottom, 1), (moving, 2) })
        {
            world.Add(entity, new SphereGeometry(1));
            world.Add(entity, new ContactMaterial { NormalStiffness = 100, Restitution = .5f, RestitutionThreshold = .1f });
            world.Add(entity, new DeviceCollisionBody(grid, index));
            world.Add(entity, new DeviceGeometryBinding(geometry, index));
        }
        QueryHandle members = world.Query(new QueryDefinitionBuilder().Read<DeviceBodyBinding>());
        QueryHandle spheres = world.Query(new QueryDefinitionBuilder().Read<SphereGeometry>());
        QueryHandle points = world.Query(new QueryDefinitionBuilder().Read<ContactPointReference>());
        var pairs = world.Within(spheres);
        using var parameters = new DeviceConstraintData<DeviceContactConstraint>(backend, device, 8, "DeviceContacts");
        using var anchors = new DeviceContactBodies(backend, device, 8, 2);
        using var histories = new DeviceConstraintState<DeviceAvbdContactState>(backend, device, 8, "DeviceAvbdContactStates");
        using var responses = new DeviceConstraintState<DeviceContactResponseState>(backend, device, 8, "DeviceContactResponses");
        using var detection = new DeviceCollisionDetection(backend, device, grid, geometry, CompileHistoryReferencedDetection(), 0,
            new(8, 4, 8) { Contacts = 8, BodiesPerContact = 2 }, new(1e-5f, 32, 64), .02f,
            DeviceInputTransfer.Resident, DeviceCollisionReadback.ContactChanges);
        using var matching = new DeviceContactMatches(backend, device, world, points, parameters, detection, bodies, anchors,
            CompileContactMatching(), new(.25f, .99f, true), 17, 64);
        using var identities = new ContactIdentities<AverageContactMaterials, DeviceIdentityHistoryComponents>(world, points, matching, parameters,
            CompileHistoryIdentityInputs(), new(), new(histories, responses));
        using var stateInputs = new ContactStateInitialization<DeviceAvbdContactState>(backend, device, matching, histories,
            new() { NormalPenalty = 100, TangentPenalty = 100 }, CompileHistoryStateInitialization());
        using var current = matching.CreateGeometryUpdate(bodies, anchors, CompileCurrentContactGeometry());
        using var selected = new DeviceConstraintSelection<ContactPointReference, DeviceContactConstraint>(backend, device, world, points, parameters, "DeviceContactsSelection");
        using var selectedState = new DeviceConstraintSelection<DeviceAvbdState<DeviceAvbdContactState>, DeviceContactConstraint, DeviceAvbdStateRows<DeviceAvbdContactState>>(
            backend, device, world, points, parameters, new(histories), "DeviceContactAvbdSelection");
        using var selectedResponse = new DeviceConstraintSelection<DeviceContactResponseBinding, DeviceContactConstraint, DeviceContactResponseRows>(
            backend, device, world, points, parameters, new(responses), "DeviceContactResponsesSelection");
        using var dynamics = new BodyDynamicsCommands(backend, device, world, members, bodies, CompileDynamics(), 64);
        using var bodyInputs = new DeviceBodyInputs(world, members, bodies);
        using var sphereInputs = grid.WriteGeometryReferences<SphereGeometry, DeviceComponentSource<SphereGeometry, DeviceSphereReader>>(pairs, new(), 0, 64);
        using var update = new UpdateDeviceGeometry(backend, device, grid, geometry, CompileHistoryGeometryUpdate(), 0,
            DeviceInputTransfer.Upload, contactDistance: .02f);
        using var results = new DeviceBodyResults(backend, device, bodies);
        var bindings = new Dictionary<string, ResourceBinding[]>();
        parameters.AddBindings(bindings); anchors.AddBindings(bindings); histories.AddBindings(bindings); responses.AddBindings(bindings);
        JobResourceAccess[] reads = [.. parameters.GetReadAccesses(jobs), anchors.GetReadAccess(jobs)];
        using var graph = new DeviceConstraintGraph(backend, device, bodies, dynamics, CompileHistoryAvbdGraph(), new(8, 2), bindings, reads, 64, new[] { selected });
        using var solve = new AvbdGraphCommands(backend, device, bodies, equations, graph, CompileContactAvbd(), new(1, 1, 1, 0), bindings,
            reads.Concat(new[] { histories.GetWriteAccess(jobs), responses.GetWriteAccess(jobs) }).ToArray(), 64,
            new IDeviceConstraintSelection[] { selectedState, selectedResponse });
        using var response = new DeviceContactActions(backend, device, bodies, graph, responses, CompileContactActions(), bindings, reads, 64, new[] { selectedResponse });
        var stages = bodyInputs.Upload().Then(grid.BeginInputs()).Then(sphereInputs).Then(update.Update())
            .Then(grid.Build(pairs, DevicePairRules.None, DeviceInputTransfer.Resident, DevicePairReadback.None)).Then(detection.Detect())
            .Then(identities.UploadInputs()).Then(matching.Match()).Then(identities.Update())
            .Then(matching.UpdateRows()).Then(current.Update()).Then(stateInputs.Initialize())
            .Then(dynamics.Predict(Vector3.Zero)).Then(graph.Build()).Then(response.Initialize()).Then(solve.Solve(buildGraph: false))
            .Then(dynamics.UpdateVelocities()).Then(response.ApplyRestitution(1, buildGraph: false));
        PhysicsStepResult result = PhysicsSimulation.Create(world, QueryDefinition.Empty, stages, 1).Schedule(0, 0, .1);
        var context = new PhysicsStepContext(world, QueryDefinition.Empty, 0, 0, 1, 0, .1);
        results.Readback(64).Schedule(in context, result.Completion).Complete();
        Vector3 velocity = world.Read<BodyVelocity>(moving).Linear;
        Assert.InRange(velocity.X, .4999f, .5001f);
        Assert.InRange(velocity.Y, .4999f, .5001f);
        world.ReleaseQuery(points); world.ReleaseQuery(spheres); world.ReleaseQuery(members);
    }
}
