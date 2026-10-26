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
    public void EditingADeviceContactMaterialWakesAndReleasesItsSleepingBody()
    {
        using var backend = D3D12GraphicsBackend.Create();
        using Device device = CreateDevice(backend);
        using var jobs = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using World world = WorldJobs.CreateWorld(scheduler: jobs);
        using var bodies = new DeviceBodies(backend, device, 2);
        using var equations = new DeviceAvbdBodyEquations(backend, device, bodies);
        Entity floor = AddBody(world, bodies, 0, Vector3.Zero, BodyMotion.Static);
        Entity moving = AddBody(world, bodies, 1, Vector3.UnitY, BodyMotion.Dynamic);
        world.Add(moving, new DeviceAvbdEquationBinding(equations, 1));
        world.Add(moving, new BodyActivity { Radius = 1 });
        Entity point = world.CreateRelation(floor, moving, new ContactPoint
            { Normal = Vector3.UnitY, BodyAnchorDelta = Vector3.UnitY, FirstBodyCount = 1, WarmStart = true }).Entity;
        var first = world.CreateRelation(point, floor, new ContactParticipant());
        var second = world.CreateRelation(point, moving, new ContactParticipant());
        world.AddBuffer<ContactBodyLink>(point);
        world.ExecuteBufferWrite<ContactBodyLink>(point, buffer =>
        { buffer.Add(new(first, Vector3.Zero, 1)); buffer.Add(new(second, Vector3.Zero, 1)); });
        world.Add(point, new ContactMaterial { NormalStiffness = float.PositiveInfinity });
        world.Add(point, new AvbdContactState { NormalPenalty = 1e4f }); world.Add(point, new ContactResponse());
        using var parameters = new DeviceConstraintData<DeviceContactConstraint>(backend, device, 1, "DeviceContacts");
        using var links = new DeviceContactBodies(backend, device, 1, 2);
        using var histories = new DeviceConstraintState<DeviceAvbdContactState>(backend, device, 1, "DeviceAvbdContactStates");
        using var responses = new DeviceConstraintState<DeviceContactResponseState>(backend, device, 1, "DeviceContactResponses");
        world.Add(point, new DeviceConstraintBinding<DeviceContactConstraint>(parameters, 0));
        world.Add(point, new DeviceAvbdState<DeviceAvbdContactState>(histories)); world.Add(point, new DeviceContactResponseBinding(responses));
        QueryHandle members = world.Query(world.QueryDefinition().Read<DeviceBodyBinding>());
        QueryHandle contacts = world.Query(world.QueryDefinition().Read<ContactPoint>());
        QueryHandle states = world.Query(world.QueryDefinition().Read<AvbdContactState>()
            .Read<DeviceConstraintBinding<DeviceContactConstraint>>().Read<DeviceAvbdState<DeviceAvbdContactState>>());
        QueryHandle events = world.Query(world.QueryDefinition().Read<PhysicsStepEvents>().ReadBuffer<PhysicsEvent>());
        using var activities = new DeviceBodyActivities(backend, device, world, members, bodies);
        world.Add(moving, new DeviceBodyActivityBinding(activities));
        var bindings = new Dictionary<string, ResourceBinding[]>(); activities.AddBindings(bindings);
        using var dynamics = new DeviceBodyDynamics(backend, device, world, members, bodies, CompileActiveBodyDynamics(), 64,
            bindings, activities.GetWriteAccesses(jobs));
        using var bodyInputs = new DeviceBodyInputs(world, members, bodies);
        using var bodyResults = new DeviceBodyResults(backend, device, bodies);
        using var input = new DeviceContactInputs(world, contacts, parameters, links);
        using var stateInput = new DeviceAvbdStateInputs<AvbdContactState, DeviceContactConstraint, DeviceAvbdContactState, DeviceAvbdContactStateSource>(
            backend, device, world, states, parameters, histories, default);
        using var selected = new DeviceConstraintSelection<ContactPoint, DeviceContactConstraint>(backend, device, world,
            contacts, parameters, "DeviceContactsSelection");
        using var selectedStates = new DeviceConstraintSelection<ContactPoint, DeviceContactConstraint, DeviceAvbdStateRows<DeviceAvbdContactState>>(
            backend, device, world, contacts, parameters, new(histories), "DeviceContactAvbdSelection");
        using var selectedResponses = new DeviceConstraintSelection<ContactPoint, DeviceContactConstraint, DeviceContactResponseRows>(
            backend, device, world, contacts, parameters, new(responses), "DeviceContactResponsesSelection");
        parameters.AddBindings(bindings); links.AddBindings(bindings); histories.AddBindings(bindings); responses.AddBindings(bindings);
        JobResourceAccess[] reads = [.. parameters.GetReadAccesses(jobs), links.GetReadAccess(jobs), .. activities.GetReadAccesses(jobs)];
        using var graph = new DeviceConstraintGraph(backend, device, bodies, dynamics, CompileSleepingContactGraph(), new(1, 2),
            bindings, reads, 64, new[] { selected });
        using var solve = new DeviceAvbdSolver(backend, device, bodies, equations, graph, CompileSleepingContactAvbd(), new(16, 1, .5f, 0),
            bindings, reads.Concat(new[] { histories.GetWriteAccess(jobs), responses.GetWriteAccess(jobs) }).ToArray(), 64,
            new IDeviceConstraintSelection[] { selectedStates, selectedResponses });
        using var actions = new DeviceContactActions(backend, device, bodies, graph, responses, CompileSleepingContactActions(), bindings,
            reads, 64, new[] { selectedResponses });
        using var activity = new DevicePhysicsActivity(backend, device, activities, graph, CompileSleepingContactActivity(),
            new(.05f, .2f, .01f, .01f), bindings, reads, 64);
        using var output = new PhysicsEventBuffer(jobs, 8);
        var received = new List<(long Number, PhysicsEvent Event)>();
        using var consumer = new ConsumePhysicsEvents<RecordDeviceActivityEvents>(world, events, new(received),
            [JobResourceAccess.Write(jobs.GetResourceToken(received))]);
        var stages = bodyInputs.Upload().Then(input.Upload()).Then(stateInput.Upload()).Then(activity.UploadInputs())
            .Then(dynamics.UpdateSelection()).Then(activity.WakeConnected()).Then(dynamics.Predict(new(0, -10, 0)))
            .Then(actions.Initialize()).Then(solve.Solve()).Then(dynamics.UpdateVelocities()).Then(activity.Finish())
            .Then(activity.Readback(output)).Then(bodyResults.Readback(64)).Then(output.Emit());
        using var frame = PhysicsSimulation.Create(world, QueryDefinition.Empty, stages, 1).CreateFrame(consumer, .1, 0, 0, 0);
        frame.AdvanceTime(.1); frame.CompleteForRead(); frame.AdvanceTime(.1); frame.CompleteForRead();
        Assert.True(world.Read<BodyActivity>(moving).Sleeping);

        world.Replace(point, new ContactMaterial { NormalStiffness = 0 });
        frame.AdvanceTime(.1); frame.CompleteForRead();

        Near(new(0, .9f, 0), world.Read<BodyPose>(moving).Position);
        Near(new(0, -1, 0), world.Read<BodyVelocity>(moving).Linear);
        Assert.False(world.Read<BodyActivity>(moving).Sleeping);
        var wake = Assert.Single(received, item => item.Event.Kind == PhysicsEventKind.Wake);
        Assert.Equal(moving, wake.Event.Subject); Assert.Equal(2, wake.Number); Assert.Equal(.2, wake.Event.Time);
        world.ReleaseQuery(events); world.ReleaseQuery(states); world.ReleaseQuery(contacts); world.ReleaseQuery(members);
    }
}
