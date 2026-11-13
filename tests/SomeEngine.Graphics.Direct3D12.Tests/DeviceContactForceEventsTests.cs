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

public sealed partial class DeviceAvbdTests
{

    [Fact]
    public void TheFrameConsumesTheDeviceContactForceAndItsGeometryDestruction()
    {
        using var backend = D3D12GraphicsBackend.Create();
        using Device device = CreateDevice(backend);
        using var jobs = new Scheduler(new JobConfiguration { WorkerCount = 2 });
        using World world = new(execution: new WorldJobs(jobs));
        using var bodies = new DeviceBodies(backend, device, 2);
        using var equations = new DeviceAvbdBodyEquations(backend, device, bodies);
        Entity floor = CreateBody(world, bodies, equations, 0, Vector3.Zero, BodyMotion.Static);
        Entity moving = CreateBody(world, bodies, equations, 1, Vector3.UnitY, BodyMotion.Dynamic);
        world.Add(moving, new ReportContacts());
        using var source = CreateAvbdContactValues(jobs, floor, moving,
            new() { Normal = Vector3.UnitY, BodyAnchorDelta = Vector3.UnitY, FirstBodyCount = 1 }, new() { NormalStiffness = 100 },
            [new(floor, Vector3.Zero, 1), new(moving, Vector3.Zero, 1)], new() { NormalPenalty = 900 });
        using var contacts = new DeviceContactManifolds<DeviceAvbdContactState>(backend, device, new(1, 1, 2), default);
        using var inputs = new DeviceContactInputs<DeviceAvbdContactState>(backend, device, world, source, contacts);
        Query members = world.Query(new QueryDescription().WithStored<DeviceBodyBinding>().With<DeviceAvbdEquationBinding>());
        using var dynamics = new BodyDynamicsCommands(backend, device, world, members, bodies, CompileDynamics(), 64);
        using var bodyInputs = new DeviceBodyInputs(world, members, bodies);
        var bindings = new Dictionary<string, ResourceBinding[]>(); contacts.Current.AddBindings(bindings);
        JobResourceAccess[] reads = [];
        using var graph = new DeviceConstraintGraph(backend, device, bodies, dynamics, CompileContactGraph(), new(1, 2), bindings, reads, 64, contacts: contacts);
        using var solve = new AvbdGraphCommands(backend, device, bodies, equations, graph, CompileContactAvbd(), new(1, 1, 0, 0), bindings, reads, 64);
        using var actions = new DeviceContactActions(backend, device, bodies, graph, contacts, CompileContactActions(), bindings, reads, 64, Array.Empty<IDeviceConstraintSelection>());
        using var samples = new DeviceContactSamples(backend, device, bodies, contacts, CompileResponseContactSamples());
        using var common = new DeviceContactResults(backend, device, jobs);
        using var output = new PhysicsEventBuffer(world, 4);
        using var observation = new DeviceContactEvents(world, samples, common.Contacts, output, 0);
        var received = new List<(long Number, PhysicsEvent Event)>();
        using var consumer = new RecordContactNotifications(jobs, output.Watch(), received);
        var stages = bodyInputs.Upload().Then(inputs.Upload()).Then(dynamics.Predict(new(0, -10, 0)))
            .Then(actions.Initialize()).Then(solve.Solve()).Then(dynamics.UpdateVelocities()).Then(samples.Readback())
            .Then(common.Readback(contacts)).Then(new ObserveContact(observation)).Then(output.Emit());
        using var frame = PhysicsSimulation.Create(world, members, stages, 1).CreateFrame(consumer, .1, 0, 0, 0);
        frame.AdvanceTime(.1); frame.CompleteForRead();

        world.Destroy(floor);
        source.Resize(0, 0, 0);
        frame.AdvanceTime(.1); frame.CompleteForRead();

        Assert.Equal(new[] { PhysicsEventPhase.Begin, PhysicsEventPhase.End }, received.Select(item => item.Event.Phase));
        Assert.Equal(new long[] { 0, 1 }, received.Select(item => item.Number));
        Assert.Equal(new[] { .1, .2 }, received.Select(item => item.Event.Time));
        Assert.All(received, item =>
        {
            Assert.Equal(PhysicsEventKind.Contact, item.Event.Kind); Assert.Equal(0u, item.Event.FirstShape); Assert.Equal(0u, item.Event.SecondShape);
            Assert.Equal(floor, item.Event.First); Assert.Equal(moving, item.Event.Second);
        });
        Assert.InRange(received[0].Event.Force.Y, 4.7367f, 4.7370f);
        Assert.Equal(Vector3.Zero, received[1].Event.Force);
        Assert.False(world.Has<BodyPose>(floor));
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
    private readonly struct ObserveContact(DeviceContactEvents events) : IPhysicsStage
    { public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => events.Schedule(in context, dependency); }
    private readonly struct RecordContactNotifications(Scheduler scheduler, EventReader<PhysicsEvent> reader,
        List<(long Number, PhysicsEvent Event)> values) : IPhysicsStage, IDisposable
    {
        public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => scheduler.Schedule(
            new Capture(scheduler, reader, values), JobResourceAccess.Write(scheduler.GetResourceToken(reader)), dependency);
        private readonly struct Capture(Scheduler scheduler, EventReader<PhysicsEvent> reader, List<(long Number, PhysicsEvent Event)> values) : IJob
        {
            public void Execute()
            {
                EventReadRange range = reader.Capture();
                scheduler.Schedule(new Read(reader, values, range), [range.Read, JobResourceAccess.Write(scheduler.GetResourceToken(values))], range.Ready);
            }
        }
        private readonly struct Read(EventReader<PhysicsEvent> reader, List<(long Number, PhysicsEvent Event)> values, EventReadRange range) : IJob
        {
            public void Execute()
            {
                using EventRead<PhysicsEvent> read = reader.Open(range);
                foreach (PhysicsEvent value in read) values.Add((value.StepNumber, value));
                reader.Advance(read.Through);
            }
        }
        public void Dispose() => reader.Dispose();
    }
}
