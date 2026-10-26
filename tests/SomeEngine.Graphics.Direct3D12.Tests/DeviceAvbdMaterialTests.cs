using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;
using SomeEngine.Physics;
using Xunit;

namespace SomeEngine.Graphics.Direct3D12.Tests;

public sealed partial class DeviceAvbdTests
{
    [Theory]
    [InlineData(100, 10, 0, 0, .9666667f, 0, 0)]
    [InlineData(1e6f, 0, .2f, .2f, 1, 0, 0)]
    [InlineData(1e6f, 0, 3, 4, 1, 2.88f, 3.84f)]
    public void DeviceContactMaterialProducesItsNormalAndTangentialResponse(float stiffness, float damping,
        float initialX, float initialZ, float height, float velocityX, float velocityZ)
    {
        using var backend = D3D12GraphicsBackend.Create();
        using Device device = CreateDevice(backend);
        using var jobs = new Scheduler(new JobRuntimeConfig { WorkerCount = 0 });
        using World world = WorldJobs.CreateWorld(scheduler: jobs);
        using var bodies = new DeviceBodies(backend, device, 2);
        using var equations = new DeviceAvbdBodyEquations(backend, device, bodies);
        var floor = CreateBody(world, bodies, equations, 0, Vector3.Zero, BodyMotion.Static);
        var moving = CreateBody(world, bodies, equations, 1, Vector3.UnitY, BodyMotion.Dynamic);
        world.Replace(moving, new BodyVelocity { Linear = new(initialX, 0, initialZ) });
        var point = world.CreateRelation(floor, moving, new ContactPoint
        { Normal = Vector3.UnitY, BodyAnchorDelta = Vector3.UnitY, FirstBodyCount = 1 });
        var first = world.CreateRelation(point.Entity, floor, new ContactParticipant());
        var second = world.CreateRelation(point.Entity, moving, new ContactParticipant());
        world.AddBuffer<ContactBodyLink>(point.Entity);
        world.ExecuteBufferWrite<ContactBodyLink>(point.Entity, buffer =>
        { buffer.Add(new(first, Vector3.Zero, 1)); buffer.Add(new(second, Vector3.Zero, 1)); });
        world.Add(point.Entity, new ContactMaterial { NormalStiffness = stiffness, NormalDamping = damping, StaticFriction = .3f, DynamicFriction = .2f });
        world.Add(point.Entity, new ContactResponse());
        world.Add(point.Entity, new AvbdContactState { NormalPenalty = stiffness, TangentPenalty = 100, FrictionRegularizationSpeed = .01f });
        using var parameters = new DeviceConstraintData<DeviceContactConstraint>(backend, device, 1, "DeviceContacts");
        using var links = new DeviceContactBodies(backend, device, 1, 2);
        using var histories = new DeviceConstraintState<DeviceAvbdContactState>(backend, device, 1, "DeviceAvbdContactStates");
        using var responses = new DeviceConstraintState<DeviceContactResponseState>(backend, device, 1, "DeviceContactResponses");
        world.Add(point.Entity, new DeviceConstraintBinding<DeviceContactConstraint>(parameters, 0));
        world.Add(point.Entity, new DeviceAvbdState<DeviceAvbdContactState>(histories)); world.Add(point.Entity, new DeviceContactResponseBinding(responses));
        QueryHandle members = world.Query(new QueryDefinitionBuilder().Read<DeviceBodyBinding>());
        QueryHandle contacts = world.Query(new QueryDefinitionBuilder().Read<ContactPoint>().Read<ContactMaterial>().ReadBuffer<ContactBodyLink>());
        QueryHandle states = world.Query(new QueryDefinitionBuilder().Read<AvbdContactState>()
            .Read<DeviceConstraintBinding<DeviceContactConstraint>>().Read<DeviceAvbdState<DeviceAvbdContactState>>());
        using var input = parameters.WriteInputs<ContactPoint, DeviceContactSource>(world, contacts, new(links), 64, [links.GetUploadAccess(jobs)]);
        using var stateInput = new DeviceAvbdStateInputs<AvbdContactState, DeviceContactConstraint, DeviceAvbdContactState, DeviceAvbdContactStateSource>(
            backend, device, world, states, parameters, histories, default);
        using var selected = new DeviceConstraintSelection<ContactPoint, DeviceContactConstraint>(backend, device, world, contacts, parameters, "DeviceContactsSelection");
        using var selectedState = new DeviceConstraintSelection<ContactPoint, DeviceContactConstraint, DeviceAvbdStateRows<DeviceAvbdContactState>>(
            backend, device, world, contacts, parameters, new(histories), "DeviceContactAvbdSelection");
        using var selectedResponse = new DeviceConstraintSelection<ContactPoint, DeviceContactConstraint, DeviceContactResponseRows>(
            backend, device, world, contacts, parameters, new(responses), "DeviceContactResponsesSelection");
        using var dynamics = new DeviceBodyDynamics(backend, device, world, members, bodies, CompileDynamics(), 64);
        using var results = new DeviceBodyResults(backend, device, bodies);
        using var contactResults = new DeviceContactResults(backend, device, parameters, responses);
        var bindings = new Dictionary<string, ResourceBinding[]>();
        parameters.AddBindings(bindings); links.AddBindings(bindings); histories.AddBindings(bindings); responses.AddBindings(bindings);
        JobResourceAccess[] reads = [.. parameters.GetReadAccesses(jobs), links.GetReadAccess(jobs)];
        using var graph = new DeviceConstraintGraph(backend, device, bodies, dynamics, CompileContactGraph(), new(1, 2), bindings, reads, 64, new[] { selected });
        using var solver = new DeviceAvbdSolver(backend, device, bodies, equations, graph, CompileContactAvbd(), new(32, 1, 0, 0), bindings,
            reads.Concat(new[] { histories.GetWriteAccess(jobs), responses.GetWriteAccess(jobs) }).ToArray(), 64,
            new IDeviceConstraintSelection[] { selectedState, selectedResponse });
        using var response = new DeviceContactActions(backend, device, bodies, graph, responses, CompileContactActions(), bindings, reads, 64, new[] { selectedResponse });
        var step = parameters.BeginInputs().Then(input).Then(parameters.UploadInputs()).Then(links.UploadInputs()).Then(stateInput.Upload())
            .Then(bodies.WriteInputs(64)).Then(bodies.UploadInputs()).Then(dynamics.Predict(new(0, -10, 0))).Then(response.Initialize())
            .Then(solver.Solve()).Then(dynamics.UpdateVelocities()).Then(results.Readback(64)).Then(contactResults.Readback(64));

        PhysicsSimulation.Create(world, QueryDefinition.Empty, step, 1).Schedule(0, 0, .1).Complete();

        Assert.InRange(MathF.Abs(world.Read<BodyPose>(moving).Position.Y - height), 0, 2e-5f);
        Assert.InRange(Vector2.Distance(new(velocityX, velocityZ), new(world.Read<BodyVelocity>(moving).Linear.X, world.Read<BodyVelocity>(moving).Linear.Z)), 0, 1e-3f);
        Assert.InRange(Vector2.Distance(new((velocityX - initialX) / .1f, (velocityZ - initialZ) / .1f),
            new(world.Read<ContactResponse>(point.Entity).ForceOnSecond.X, world.Read<ContactResponse>(point.Entity).ForceOnSecond.Z)), 0, .01f);
        world.ReleaseQuery(states); world.ReleaseQuery(contacts); world.ReleaseQuery(members);
    }
}
