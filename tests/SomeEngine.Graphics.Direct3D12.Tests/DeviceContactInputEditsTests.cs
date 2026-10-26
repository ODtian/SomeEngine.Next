using System.Numerics;
using SomeEngine.ECS;
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
    public void DeviceContactLinkEditChangesTheNextResidentBodyResponse()
    {
        using var backend = D3D12GraphicsBackend.Create();
        using Device device = CreateDevice(backend);
        using var jobs = new Scheduler(new JobRuntimeConfig { WorkerCount = 0 });
        using World world = WorldJobs.CreateWorld(scheduler: jobs);
        using var bodies = new DeviceBodies(backend, device, 2);
        using var equations = new DeviceAvbdBodyEquations(backend, device, bodies);
        var floor = CreateBody(world, bodies, equations, 0, Vector3.Zero, BodyMotion.Static);
        var moving = CreateBody(world, bodies, equations, 1, Vector3.UnitY, BodyMotion.Dynamic);
        world.Replace(moving, new BodyMass { Motion = BodyMotion.Dynamic, InverseMass = 1, InverseInertia = Matrix4x4.Identity });
        var point = world.CreateRelation(floor, moving, new ContactPoint
        { Normal = Vector3.UnitY, BodyAnchorDelta = Vector3.UnitY, FirstBodyCount = 1, WarmStart = true });
        var first = world.CreateRelation(point.Entity, floor, new ContactParticipant());
        var second = world.CreateRelation(point.Entity, moving, new ContactParticipant());
        world.AddBuffer<ContactBodyLink>(point.Entity);
        world.ExecuteBufferWrite<ContactBodyLink>(point.Entity, links =>
        { links.Add(new(first, Vector3.Zero, 1)); links.Add(new(second, Vector3.Zero, 1)); });
        world.Add(point.Entity, new ContactMaterial { NormalStiffness = 100 });
        world.Add(point.Entity, new ContactResponse());
        world.Add(point.Entity, new AvbdContactState { NormalPenalty = 100, TangentPenalty = 100 });
        using var parameters = new DeviceConstraintData<DeviceContactConstraint>(backend, device, 1, "DeviceContacts");
        using var anchors = new DeviceContactBodies(backend, device, 1, 2);
        using var histories = new DeviceConstraintState<DeviceAvbdContactState>(backend, device, 1, "DeviceAvbdContactStates");
        using var responses = new DeviceConstraintState<DeviceContactResponseState>(backend, device, 1, "DeviceContactResponses");
        world.Add(point.Entity, new DeviceConstraintBinding<DeviceContactConstraint>(parameters, 0));
        world.Add(point.Entity, new DeviceAvbdState<DeviceAvbdContactState>(histories));
        world.Add(point.Entity, new DeviceContactResponseBinding(responses));
        QueryHandle members = world.Query(new QueryDefinitionBuilder().Read<DeviceBodyBinding>());
        QueryHandle contacts = world.Query(new QueryDefinitionBuilder().Read<ContactPoint>());
        QueryHandle stateRows = world.Query(new QueryDefinitionBuilder().Read<AvbdContactState>()
            .Read<DeviceConstraintBinding<DeviceContactConstraint>>().Read<DeviceAvbdState<DeviceAvbdContactState>>());
        using var input = new DeviceContactInputs(world, contacts, parameters, anchors);
        using var bodyInputs = new DeviceBodyInputs(world, members, bodies);
        using var stateInput = new DeviceAvbdStateInputs<AvbdContactState, DeviceContactConstraint, DeviceAvbdContactState, DeviceAvbdContactStateSource>(
            backend, device, world, stateRows, parameters, histories, default);
        using var selected = new DeviceConstraintSelection<ContactPoint, DeviceContactConstraint>(backend, device, world, contacts, parameters, "DeviceContactsSelection");
        using var selectedState = new DeviceConstraintSelection<ContactPoint, DeviceContactConstraint, DeviceAvbdStateRows<DeviceAvbdContactState>>(
            backend, device, world, contacts, parameters, new(histories), "DeviceContactAvbdSelection");
        using var selectedResponse = new DeviceConstraintSelection<ContactPoint, DeviceContactConstraint, DeviceContactResponseRows>(
            backend, device, world, contacts, parameters, new(responses), "DeviceContactResponsesSelection");
        using var dynamics = new DeviceBodyDynamics(backend, device, world, members, bodies, CompileDynamics(), 64);
        using var results = new DeviceBodyResults(backend, device, bodies);
        var bindings = new Dictionary<string, ResourceBinding[]>();
        parameters.AddBindings(bindings); anchors.AddBindings(bindings); histories.AddBindings(bindings); responses.AddBindings(bindings);
        JobResourceAccess[] reads = [.. parameters.GetReadAccesses(jobs), anchors.GetReadAccess(jobs)];
        using var graph = new DeviceConstraintGraph(backend, device, bodies, dynamics, CompileContactGraph(), new(1, 2), bindings, reads, 64, new[] { selected });
        using var solver = new DeviceAvbdSolver(backend, device, bodies, equations, graph, CompileContactAvbd(), new(1, 1, 1, 0), bindings,
            reads.Concat(new[] { histories.GetWriteAccess(jobs), responses.GetWriteAccess(jobs) }).ToArray(), 64,
            new IDeviceConstraintSelection[] { selectedState, selectedResponse });
        using var response = new DeviceContactActions(backend, device, bodies, graph, responses, CompileContactActions(), bindings, reads, 64, new[] { selectedResponse });
        var stages = input.Upload().Then(stateInput.Upload()).Then(bodyInputs.Upload()).Then(dynamics.Predict(new(0, -10, 0)))
            .Then(response.Initialize()).Then(solver.Solve()).Then(dynamics.UpdateVelocities()).Then(results.Readback(64));
        var simulation = PhysicsSimulation.Create(world, QueryDefinition.Empty, stages, 1);

        simulation.Schedule(0, 0, .1).Complete();
        world.ExecuteBufferWrite<ContactBodyLink>(point.Entity, links => links[1] = new(second, new(.5f, 0, 0), 1));
        simulation.Schedule(1, .1, .1).Complete();

        Assert.InRange(world.Read<BodyPose>(moving).Position.Y, .84870f, .84874f);
        Assert.InRange(world.Read<BodyVelocity>(moving).Linear.Y, -.8463f, -.8460f);
        Assert.InRange(world.Read<BodyVelocity>(moving).Angular.Z, .4100f, .4104f);
        world.ReleaseQuery(stateRows); world.ReleaseQuery(contacts); world.ReleaseQuery(members);
    }
}
