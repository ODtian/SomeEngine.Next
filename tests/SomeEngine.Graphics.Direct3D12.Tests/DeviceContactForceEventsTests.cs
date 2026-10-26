using System.Numerics;
using SlangShaderSharp;
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
    private struct ContactNotifications;

    [Fact]
    public void TheFrameConsumesTheDeviceContactForceAndItsGeometryDestruction()
    {
        using var backend = D3D12GraphicsBackend.Create();
        using Device device = CreateDevice(backend);
        using var jobs = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using World world = WorldJobs.CreateWorld(scheduler: jobs);
        using var bodies = new DeviceBodies(backend, device, 2);
        using var equations = new DeviceAvbdBodyEquations(backend, device, bodies);
        Entity floor = CreateBody(world, bodies, equations, 0, Vector3.Zero, BodyMotion.Static);
        Entity moving = CreateBody(world, bodies, equations, 1, Vector3.UnitY, BodyMotion.Dynamic);
        Entity point = world.CreateRelation(floor, moving, new ContactPoint
            { Normal = Vector3.UnitY, BodyAnchorDelta = Vector3.UnitY, FirstBodyCount = 1 }).Entity;
        var first = world.CreateRelation(point, floor, new ContactParticipant());
        var second = world.CreateRelation(point, moving, new ContactParticipant());
        world.AddBuffer<ContactBodyLink>(point);
        world.ExecuteBufferWrite<ContactBodyLink>(point, links =>
        { links.Add(new(first, Vector3.Zero, 1)); links.Add(new(second, Vector3.Zero, 1)); });
        world.Add(point, new ContactMaterial { NormalStiffness = 100 });
        world.Add(point, new ContactResponse()); world.Add(point, new AvbdContactState { NormalPenalty = 900 });
        world.Add(point, new ContactEventState<ContactNotifications>());
        using var parameters = new DeviceConstraintData<DeviceContactConstraint>(backend, device, 1, "DeviceContacts");
        using var anchors = new DeviceContactBodies(backend, device, 1, 2);
        using var histories = new DeviceConstraintState<DeviceAvbdContactState>(backend, device, 1, "DeviceAvbdContactStates");
        using var responses = new DeviceConstraintState<DeviceContactResponseState>(backend, device, 1, "DeviceContactResponses");
        world.Add(point, new DeviceConstraintBinding<DeviceContactConstraint>(parameters, 0));
        world.Add(point, new DeviceAvbdState<DeviceAvbdContactState>(histories)); world.Add(point, new DeviceContactResponseBinding(responses));
        QueryHandle members = world.Query(world.QueryDefinition().Read<DeviceBodyBinding>().All<DeviceAvbdEquationBinding>());
        QueryHandle contacts = world.Query(world.QueryDefinition().Read<ContactPoint>());
        QueryHandle states = world.Query(world.QueryDefinition().Read<AvbdContactState>()
            .Read<DeviceConstraintBinding<DeviceContactConstraint>>().Read<DeviceAvbdState<DeviceAvbdContactState>>());
        QueryHandle events = world.Query(world.QueryDefinition().Read<PhysicsStepEvents>().ReadBuffer<PhysicsEvent>());
        using var inputs = new DeviceContactInputs(world, contacts, parameters, anchors);
        using var stateInputs = new DeviceAvbdStateInputs<AvbdContactState, DeviceContactConstraint, DeviceAvbdContactState, DeviceAvbdContactStateSource>(
            backend, device, world, states, parameters, histories, default);
        using var dynamics = new DeviceBodyDynamics(backend, device, world, members, bodies, CompileDynamics(), 64);
        using var bodyInputs = new DeviceBodyInputs(world, members, bodies);
        using var selected = new DeviceConstraintSelection<ContactPoint, DeviceContactConstraint>(backend, device, world, contacts,
            parameters, "DeviceContactsSelection");
        using var selectedStates = new DeviceConstraintSelection<ContactPoint, DeviceContactConstraint, DeviceAvbdStateRows<DeviceAvbdContactState>>(
            backend, device, world, contacts, parameters, new(histories), "DeviceContactAvbdSelection");
        using var selectedResponses = new DeviceConstraintSelection<ContactPoint, DeviceContactConstraint, DeviceContactResponseRows>(
            backend, device, world, contacts, parameters, new(responses), "DeviceContactResponsesSelection");
        var bindings = new Dictionary<string, ResourceBinding[]>();
        parameters.AddBindings(bindings); anchors.AddBindings(bindings); histories.AddBindings(bindings); responses.AddBindings(bindings);
        JobResourceAccess[] reads = [.. parameters.GetReadAccesses(jobs), anchors.GetReadAccess(jobs)];
        using var graph = new DeviceConstraintGraph(backend, device, bodies, dynamics, CompileContactGraph(), new(1, 2), bindings, reads, 64, new[] { selected });
        using var solve = new DeviceAvbdSolver(backend, device, bodies, equations, graph, CompileContactAvbd(), new(1, 1, 0, 0),
            bindings, reads.Concat(new[] { histories.GetWriteAccess(jobs), responses.GetWriteAccess(jobs) }).ToArray(), 64,
            new IDeviceConstraintSelection[] { selectedStates, selectedResponses });
        using var actions = new DeviceContactActions(backend, device, bodies, graph, responses, CompileContactActions(), bindings, reads, 64, new[] { selectedResponses });
        using var samples = new DeviceContactSamples(backend, device, bodies, parameters, CompileResponseContactSamples(), bindings,
            reads.Append(responses.GetReadAccess(jobs)).ToArray(), 64, new IDeviceConstraintSelection[] { selected, selectedResponses });
        using var output = new PhysicsEventBuffer(jobs, 4);
        using var observation = new ContactEvents<ContactNotifications, DeviceContactEventSource>(world, members, contacts, output, 0, new(samples));
        var received = new List<(long Number, PhysicsEvent Event)>();
        using var consumer = new ConsumePhysicsEvents<RecordContactNotifications>(world, events, new(received),
            [JobResourceAccess.Write(jobs.GetResourceToken(received))]);
        var stages = bodyInputs.Upload().Then(inputs.Upload()).Then(stateInputs.Upload()).Then(dynamics.Predict(new(0, -10, 0)))
            .Then(actions.Initialize()).Then(solve.Solve()).Then(dynamics.UpdateVelocities()).Then(samples.Readback())
            .Then(observation).Then(output.Emit());
        using var frame = PhysicsSimulation.Create(world, QueryDefinition.Empty, stages, 1).CreateFrame(consumer, .1, 0, 0, 0);
        frame.AdvanceTime(.1); frame.CompleteForRead();

        world.DestroyEntity(floor);
        frame.AdvanceTime(.1); frame.CompleteForRead();

        Assert.Equal(new[] { PhysicsEventPhase.Begin, PhysicsEventPhase.End }, received.Select(item => item.Event.Phase));
        Assert.Equal(new long[] { 0, 1 }, received.Select(item => item.Number));
        Assert.Equal(new[] { .1, .2 }, received.Select(item => item.Event.Time));
        Assert.All(received, item => { Assert.Equal(PhysicsEventKind.Contact, item.Event.Kind); Assert.Equal(point, item.Event.Subject);
            Assert.Equal(floor, item.Event.First); Assert.Equal(moving, item.Event.Second); });
        Assert.InRange(received[0].Event.Force.Y, 4.7367f, 4.7370f);
        Assert.Equal(Vector3.Zero, received[1].Event.Force);
        Assert.False(world.Has<ContactEventState<ContactNotifications>>(point));
        world.ReleaseQuery(events); world.ReleaseQuery(states); world.ReleaseQuery(contacts); world.ReleaseQuery(members);
    }

    private ComputePipelineDesc CompileResponseContactSamples()
    {
        D3D12TestShader shader = shaderCompiler.CompileWithSearchPaths("response_contact_samples", """
            #include "physics_contact_response_samples.slang"
            [shader("compute")][numthreads(64,1,1)]
            void ReadSamples(uint3 id : SV_DispatchThreadID)
            { DeviceResponseEventForces forces; readDeviceContactSamples(id.x, forces); }
            """, [new("ReadSamples", SlangStage.Compute)], "sm_6_0", ShaderDirectory());
        return new(shader.SlangCode, shader.Reflection, shader.GetEntryPoint(0), shader.GetEntryCode(0));
    }
    private readonly struct RecordContactNotifications(List<(long Number, PhysicsEvent Event)> values) : IPhysicsEventConsumer
    {
        public void Consume(scoped in PhysicsStepEvents step, scoped ReadOnlySpan<PhysicsEvent> events, ref JobCommandWriter commands)
        { foreach (PhysicsEvent value in events) values.Add((step.Number, value)); }
    }
}
