using System.Numerics;
using SlangShaderSharp;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;
using SomeEngine.Physics;
using Xunit;

namespace SomeEngine.Graphics.Direct3D12.Tests;

public sealed partial class DeviceXpbdTests
{

    [Fact]
    public void TheFrameConsumesDeviceTriggerEntryAndExitBeforeTheBodyResults()
    {
        using var backend = D3D12GraphicsBackend.Create();
        using Device device = CreateDevice(backend);
        using var jobs = new Scheduler(new JobConfiguration { WorkerCount = 2 });
        using World world = new(execution: new WorldJobs(jobs));
        using var bodies = new DeviceBodies(backend, device, 2);
        Entity floor = AddBody(world, bodies, 0, Vector3.Zero, BodyMotion.Static);
        Entity moving = AddBody(world, bodies, 1, new(0, .125f, 0), BodyMotion.Dynamic);
        world.Set(moving, new BodyVelocity { Linear = new(0, -2, 0) });
        world.Add(moving, new ReportContacts());
        using var source = CreateContactValues<XpbdContactState>(jobs, floor, moving,
            new() { Normal = Vector3.UnitY, Separation = .125f, BodyAnchorDelta = Vector3.UnitY * .125f, FirstBodyCount = 1 }, default,
            [new(floor, Vector3.Zero, 1), new(moving, Vector3.Zero, 1)]);
        source.Columns.Flags[0] = ContactPointFlags.Trigger;
        using var contacts = new DeviceContactManifolds<XpbdContactState>(backend, device, new(1, 1, 2), default);
        using var input = new DeviceContactInputs<XpbdContactState>(backend, device, world, source, contacts);
        Query members = world.Query(new QueryDescription().WithStored<DeviceBodyBinding>());
        using var dynamics = new BodyDynamicsCommands(backend, device, world, members, bodies, CompileBodyDynamics(), 64);
        using var bodyInputs = new DeviceBodyInputs(world, members, bodies);
        using var results = new DeviceBodyResults(backend, device, bodies);
        using var resultsContacts = new DeviceContactResults(backend, device, jobs);
        using var samples = new DeviceContactSamples(backend, device, bodies, contacts, CompileTriggerContactSamples());
        using var output = new PhysicsEventBuffer(world, 4);
        using var observation = new DeviceContactEvents(world, samples, resultsContacts.Contacts, output, 0);
        var received = new List<(long Number, PhysicsEvent Event)>();
        using var consumer = new RecordDeviceActivityEvents(jobs, output.Watch(), received);
        var stages = bodyInputs.Upload().Then(input.Upload()).Then(dynamics.Predict(Vector3.Zero)).Then(dynamics.UpdateVelocities())
            .Then(samples.Readback()).Then(resultsContacts.Readback(contacts)).Then(new ObserveTrigger(observation)).Then(output.Emit());
        using var frame = PhysicsSimulation.Create(world, members, stages, 1).CreateFrame(consumer, .125, 0, 0, 0);

        frame.AdvanceTime(.125); frame.CompleteForRead();
        world.Set(moving, new BodyVelocity { Linear = new(0, 2, 0) });
        frame.AdvanceTime(.125); PhysicsStepResult completed = frame.CompleteForRead();

        Assert.Equal(new[] { PhysicsEventPhase.Begin, PhysicsEventPhase.End }, received.Select(item => item.Event.Phase));
        Assert.Equal(new long[] { 0, 1 }, received.Select(item => item.Number));
        Assert.Equal(new[] { .125, .25 }, received.Select(item => item.Event.Time));
        Assert.All(received, item =>
        {
            Assert.Equal(PhysicsEventKind.Trigger, item.Event.Kind); Assert.Equal(0u, item.Event.FirstShape); Assert.Equal(0u, item.Event.SecondShape);
            Assert.Equal(floor, item.Event.First); Assert.Equal(moving, item.Event.Second); Assert.Equal(Vector3.Zero, item.Event.Force);
        });
        Assert.Equal(-.125f, received[0].Event.Separation); Assert.Equal(.125f, received[1].Event.Separation);
        var context = new PhysicsStepContext(world, world.Query(new QueryDescription()), completed.Number, 0, 1, .125, .125);
        results.Readback(64).Schedule(in context, completed.Completion).Complete();
        Near(new(0, .125f, 0), world.ReadComponentData<BodyPose>(moving).Position);
        Assert.Equal(1, world.ReadComponentData<BodyStepState>(moving).Number); Assert.Equal(.25, world.ReadComponentData<BodyStepState>(moving).Time);
    }

    private readonly struct ObserveTrigger(DeviceContactEvents events) : IPhysicsStage
    { public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => events.Schedule(in context, dependency); }

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
