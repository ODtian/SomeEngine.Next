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

public sealed partial class DeviceXpbdTests
{
    private struct DeviceTriggers;

    [Fact]
    public void TheFrameConsumesDeviceTriggerEntryAndExitBeforeTheBodyResults()
    {
        using var backend = D3D12GraphicsBackend.Create();
        using Device device = CreateDevice(backend);
        using var jobs = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using World world = WorldJobs.CreateWorld(scheduler: jobs);
        using var bodies = new DeviceBodies(backend, device, 2);
        Entity floor = AddBody(world, bodies, 0, Vector3.Zero, BodyMotion.Static);
        Entity moving = AddBody(world, bodies, 1, new(0, .125f, 0), BodyMotion.Dynamic);
        world.Replace(moving, new BodyVelocity { Linear = new(0, -2, 0) });
        Entity point = world.CreateRelation(floor, moving, new ContactPoint
            { Normal = Vector3.UnitY, Separation = .125f, BodyAnchorDelta = Vector3.UnitY * .125f, FirstBodyCount = 1 }).Entity;
        var first = world.CreateRelation(point, floor, new ContactParticipant());
        var second = world.CreateRelation(point, moving, new ContactParticipant());
        world.AddBuffer<ContactBodyLink>(point);
        world.ExecuteBufferWrite<ContactBodyLink>(point, links =>
        { links.Add(new(first, Vector3.Zero, 1)); links.Add(new(second, Vector3.Zero, 1)); });
        world.Add(point, new ContactMaterial());
        world.Add(point, new TriggerContact()); world.Add(point, new ContactEventState<DeviceTriggers>());
        using var parameters = new DeviceConstraintData<DeviceContactConstraint>(backend, device, 1, "DeviceContacts");
        using var anchors = new DeviceContactBodies(backend, device, 1, 2);
        world.Add(point, new DeviceConstraintBinding<DeviceContactConstraint>(parameters, 0));
        QueryHandle members = world.Query(world.QueryDefinition().Read<DeviceBodyBinding>());
        QueryHandle contacts = world.Query(world.QueryDefinition().Read<ContactPoint>());
        QueryHandle events = world.Query(world.QueryDefinition().Read<PhysicsStepEvents>().ReadBuffer<PhysicsEvent>());
        using var write = parameters.WriteInputs<ContactPoint, DeviceContactSource>(world, contacts, new(anchors), 64,
            [anchors.GetUploadAccess(jobs)]);
        using var selected = new DeviceConstraintSelection<ContactPoint, DeviceContactConstraint>(backend, device, world,
            contacts, parameters, "DeviceContactsSelection");
        using var dynamics = new DeviceBodyDynamics(backend, device, world, members, bodies, CompileBodyDynamics(), 64);
        using var bodyInputs = new DeviceBodyInputs(world, members, bodies);
        using var results = new DeviceBodyResults(backend, device, bodies);
        var bindings = new Dictionary<string, ResourceBinding[]>();
        parameters.AddBindings(bindings); anchors.AddBindings(bindings);
        JobResourceAccess[] reads = [.. parameters.GetReadAccesses(jobs), anchors.GetReadAccess(jobs)];
        using var samples = new DeviceContactSamples(backend, device, bodies, parameters, CompileTriggerContactSamples(),
            bindings, reads, 64, new[] { selected });
        using var output = new PhysicsEventBuffer(jobs, 4);
        using var observation = new ContactEvents<DeviceTriggers, DeviceContactEventSource>(world, members, contacts,
            output, 0, new(samples));
        var received = new List<(long Number, PhysicsEvent Event)>();
        using var consumer = new ConsumePhysicsEvents<RecordDeviceActivityEvents>(world, events, new(received),
            [JobResourceAccess.Write(jobs.GetResourceToken(received))]);
        var stages = bodyInputs.Upload().Then(parameters.BeginInputs()).Then(write).Then(parameters.UploadInputs())
            .Then(anchors.UploadInputs()).Then(dynamics.Predict(Vector3.Zero)).Then(dynamics.UpdateVelocities())
            .Then(samples.Readback()).Then(observation).Then(output.Emit());
        using var frame = PhysicsSimulation.Create(world, QueryDefinition.Empty, stages, 1).CreateFrame(consumer, .125, 0, 0, 0);

        frame.AdvanceTime(.125); frame.CompleteForRead();
        world.Replace(moving, new BodyVelocity { Linear = new(0, 2, 0) });
        frame.AdvanceTime(.125); PhysicsStepResult completed = frame.CompleteForRead();

        Assert.Equal(new[] { PhysicsEventPhase.Begin, PhysicsEventPhase.End }, received.Select(item => item.Event.Phase));
        Assert.Equal(new long[] { 0, 1 }, received.Select(item => item.Number));
        Assert.Equal(new[] { .125, .25 }, received.Select(item => item.Event.Time));
        Assert.All(received, item =>
        { Assert.Equal(PhysicsEventKind.Trigger, item.Event.Kind); Assert.Equal(point, item.Event.Subject);
            Assert.Equal(floor, item.Event.First); Assert.Equal(moving, item.Event.Second); Assert.Equal(Vector3.Zero, item.Event.Force); });
        Assert.Equal(-.125f, received[0].Event.Separation); Assert.Equal(.125f, received[1].Event.Separation);
        var context = new PhysicsStepContext(world, QueryDefinition.Empty, completed.Number, 0, 1, .125, .125);
        results.Readback(64).Schedule(in context, completed.Completion).Complete();
        Near(new(0, .125f, 0), world.Read<BodyPose>(moving).Position);
        Assert.Equal(1, world.Read<BodyStepState>(moving).Number); Assert.Equal(.25, world.Read<BodyStepState>(moving).Time);
        world.ReleaseQuery(events); world.ReleaseQuery(contacts); world.ReleaseQuery(members);
    }

    private ComputePipelineDesc CompileTriggerContactSamples()
    {
        D3D12TestShader shader = shaderCompiler.CompileWithSearchPaths("trigger_contact_samples", """
            #include "physics_contact_samples.slang"
            [shader("compute")][numthreads(64,1,1)]
            void ReadSamples(uint3 id : SV_DispatchThreadID)
            { DeviceTriggerEventForces forces; readDeviceContactSamples(id.x, forces); }
            """, [new("ReadSamples", SlangStage.Compute)], "sm_6_0", ShaderDirectory());
        return new(shader.SlangCode, shader.Reflection, shader.GetEntryPoint(0), shader.GetEntryCode(0));
    }
}
