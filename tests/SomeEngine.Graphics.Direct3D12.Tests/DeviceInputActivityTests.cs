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
    public void ATimedDeviceKinematicInputWakesItsSleepingReferenceIslandInTheCurrentStep()
    {
        using var backend = D3D12GraphicsBackend.Create();
        using Device device = CreateDevice(backend);
        using var jobs = new Scheduler(new JobConfiguration { WorkerCount = 2 });
        using World world = new World(execution: new WorldJobs(jobs));
        using var bodies = new DeviceBodies(backend, device, 4);
        Entity reference = AddBody(world, bodies, 0, Vector3.Zero, BodyMotion.Kinematic);
        world.AddBuffer<BodyKinematicTarget>(reference);
        Entity moving = AddBody(world, bodies, 1, Vector3.UnitY, BodyMotion.Dynamic);
        Entity other = AddBody(world, bodies, 3, new(10, 0, 0), BodyMotion.Dynamic);
        using var lengths = new DeviceConstraintData<DeviceLengthConstraint>(backend, device, 1, "DeviceLengths");
        using var multipliers = new DeviceConstraintState<float>(backend, device, 1, "DeviceLengthMultipliers");
        Entity edge = world.CreateRelation(reference, moving, new XpbdLength { RestLength = 1 }).Entity;
        world.Add(edge, new DeviceConstraintBinding<DeviceLengthConstraint>(lengths, 0));
        world.Add(edge, new DeviceXpbdState<float>(multipliers));
        Query members = world.Query(new QueryDescription().WithStored<DeviceBodyBinding>());
        Query rows = world.Query(new QueryDescription().WithStored<XpbdLength>().WithStored<RelationEndpoints<XpbdLength>>()
            .WithStored<DeviceConstraintBinding<DeviceLengthConstraint>>().WithStored<DeviceXpbdState<float>>());
        using var activities = new DeviceBodyActivities(backend, device, world, members, bodies);
        foreach (Entity body in new[] { moving, other })
        { world.Add(body, new BodyActivity { Radius = 1 }); world.Add(body, new DeviceBodyActivityBinding(activities)); }
        var bindings = new Dictionary<string, ResourceBinding[]>(); activities.AddBindings(bindings);
        using var dynamics = new BodyDynamicsCommands(backend, device, world, members, bodies, CompileActiveBodyDynamics(), 64,
            bindings, activities.GetWriteAccesses(jobs));
        using var bodyInputs = new DeviceBodyInputs(world, members, bodies);
        using var timed = new DeviceBodyInputBuffers(backend, device, world, members, bodies, CompileBodyInputBuffers(), 64);
        using var results = new DeviceBodyResults(backend, device, bodies);
        using var inputs = lengths.WriteInputs<XpbdLength, DeviceLengthSource>(world, rows, default, 64);
        using var selected = new DeviceConstraintSelection<XpbdLength, DeviceLengthConstraint, DeviceXpbdStateRows<float>>(
            backend, device, world, rows, lengths, new(multipliers), "DeviceLengthsSelection");
        lengths.AddBindings(bindings); multipliers.AddBindings(bindings);
        JobResourceAccess[] reads = [.. lengths.GetReadAccesses(jobs), .. activities.GetReadAccesses(jobs)];
        using var graph = new DeviceConstraintGraph(backend, device, bodies, dynamics, CompileActiveLengthGraph(),
            new(1, 2), bindings, reads, 64, new[] { selected });
        using var solve = new XpbdGraphCommands(backend, device, bodies, graph, CompileGraphLengthProjection(), new(4, 1),
            bindings, reads, new[] { multipliers.States }, 64);
        timed.AddBindings(bindings);
        using var activity = new DevicePhysicsActivity(backend, device, activities, graph, CompileTimedLengthActivity(),
            new(.05f, .2f, .01f, .01f), bindings, reads.Concat(timed.GetReadAccesses(jobs)).ToArray(), 64);
        using var output = new PhysicsEventBuffer(world, 8);
        var received = new List<(long Number, PhysicsEvent Event)>();
        using var consumer = new RecordPhysicsEvents(jobs, output.Watch(), received);
        var stages = bodyInputs.Upload().Then(timed.WriteInputs()).Then(timed.Apply()).Then(inputs)
            .Then(lengths.UploadInputs()).Then(activity.UploadInputs()).Then(dynamics.UpdateSelection()).Then(activity.WakeConnected())
            .Then(dynamics.Predict(Vector3.Zero)).Then(timed.ApplyKinematicTargets()).Then(solve.Solve())
            .Then(dynamics.UpdateVelocities()).Then(activity.Finish()).Then(activity.Readback(output))
            .Then(results.Readback(64)).Then(output.Emit());
        using var frame = PhysicsSimulation.Create(world, world.Query(new QueryDescription()), stages, 1).CreateFrame(consumer, .1, 0, 0, 0);
        frame.AdvanceTime(.1); frame.CompleteForRead(); frame.AdvanceTime(.1); frame.CompleteForRead();
        Assert.True(world.ReadComponentData<BodyActivity>(moving).Sleeping); Assert.True(world.ReadComponentData<BodyActivity>(other).Sleeping);
        { using var targets = world.GetBuffer<BodyKinematicTarget>(reference); targets.Add(new BodyKinematicTarget
        {
            StartPose = new() { Rotation = Quaternion.Identity },
            EndPose = new() { Position = new(.1f, 0, 0), Rotation = Quaternion.Identity }, StartTime = .2, EndTime = .3,
        }); }

        frame.AdvanceTime(.1); frame.CompleteForRead();

        Assert.False(world.ReadComponentData<BodyActivity>(moving).Sleeping); Assert.True(world.ReadComponentData<BodyActivity>(other).Sleeping);
        Near(new(.1f, 0, 0), world.ReadComponentData<BodyPose>(reference).Position);
        var wake = Assert.Single(received, item => item.Event.Kind == PhysicsEventKind.Wake);
        Assert.Equal(moving, wake.Event.Subject); Assert.Equal(2, wake.Number); Assert.Equal(.2, wake.Event.Time);
          
    }

    private DeviceActivityKernels CompileTimedLengthActivity()
    {
        D3D12TestShader shader = shaderCompiler.CompileWithSearchPaths("timed_length_activity", """
            #include "physics_length_activity.slang"
            #include "physics_body_input_activity.slang"
            [shader("compute")][numthreads(64,1,1)]
            void ReadRows(uint3 id:SV_DispatchThreadID) { DeviceLengthActivity rows; readDeviceActivityRows(id.x,rows); }
            """, [new("ApplyDeviceActivityInputs", SlangStage.Compute), new("ClearDeviceActivitySignals", SlangStage.Compute),
                new("CollectDeviceActivityBodies", SlangStage.Compute), new("ReadRows", SlangStage.Compute),
                new("WakeDeviceBodies", SlangStage.Compute), new("AdvanceDeviceBodyQuietTime", SlangStage.Compute),
                new("FinishDeviceBodyActivity", SlangStage.Compute), new("ReadDeviceBodyInputActivity", SlangStage.Compute)],
            "sm_6_0", ShaderDirectory());
        ComputePipelineDesc Entry(int index) => new(shader.SlangCode, shader.Reflection, shader.GetEntryPoint(index), shader.GetEntryCode(index));
        return new(Entry(0), Entry(1), Entry(2), Entry(3), Entry(4), Entry(5), Entry(6), ReadBodyInputs: Entry(7));
    }
}
