using System.Numerics;
using System.Collections.Concurrent;
using SlangShaderSharp;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;
using SomeEngine.Physics;
using Xunit;

namespace SomeEngine.Graphics.Direct3D12.Tests;

public sealed partial class DeviceXpbdTests
{
    [Fact]
    public void CompletedDeviceMotionPreservesANewerCpuPoseAndVelocity()
    {
        using var backend = D3D12GraphicsBackend.Create();
        using Device device = CreateDevice(backend);
        using var jobs = new Scheduler(new JobConfiguration { WorkerCount = 2 });
        using World world = new World(execution: new WorldJobs(jobs));
        using var bodies = new DeviceBodies(backend, device, 1);
        Entity body = AddBody(world, bodies, 0, Vector3.Zero, BodyMotion.Dynamic);
        world.Set(body, new BodyVelocity { Linear = Vector3.UnitX });
        Query members = world.Query(new QueryDescription().WithStored<DeviceBodyBinding>());
        using var inputs = new DeviceBodyInputs(world, members, bodies);
        using var dynamics = new BodyDynamicsCommands(backend, device, world, members, bodies, CompileBodyDynamics(), 64);
        using var results = new DeviceBodyResults(backend, device, bodies);
        var motion = PhysicsSimulation.Create(world, world.Query(new QueryDescription()),
            inputs.Upload().Then(dynamics.Predict(Vector3.Zero)).Then(dynamics.UpdateVelocities()), 1);
        motion.Schedule(0, 0, .1).Complete();
        world.Set(body, new BodyPose { Position = new(100, 0, 0), Rotation = Quaternion.Identity });
        world.Set(body, new BodyVelocity { Linear = new(5, 0, 0) });

        PhysicsSimulation.Create(world, world.Query(new QueryDescription()), results.Readback(64), 1).Schedule(0, 0, .1).Complete();

        Near(new(100, 0, 0), world.ReadComponentData<BodyPose>(body).Position);
        Near(new(5, 0, 0), world.ReadComponentData<BodyVelocity>(body).Linear);
        motion.Schedule(1, .1, .1).Complete();
        PhysicsSimulation.Create(world, world.Query(new QueryDescription()), results.Readback(64), 1).Schedule(1, .1, .1).Complete();
        Near(new(100.5f, 0, 0), world.ReadComponentData<BodyPose>(body).Position);
        
    }

    [Fact]
    public void ChangedBodyMembershipStaysSelectedThroughDeviceMotionAndReadback()
    {
        using var backend = D3D12GraphicsBackend.Create();
        using Device device = CreateDevice(backend);
        using var jobs = new Scheduler(new JobConfiguration { WorkerCount = 2 });
        using World world = new World(execution: new WorldJobs(jobs));
        using var bodies = new DeviceBodies(backend, device, 1);
        Entity body = AddBody(world, bodies, 0, new(20, 0, 0), BodyMotion.Dynamic);
        world.Set(body, new BodyVelocity { Linear = Vector3.UnitX });
        Query members = world.Query(new QueryDescription().WithStored<DeviceBodyBinding>().WithChanged<BodyPose>(0));
        using var inputs = new DeviceBodyInputs(world, members, bodies);
        using var dynamics = new BodyDynamicsCommands(backend, device, world, members, bodies, CompileBodyDynamics(), 64);
        using var results = new DeviceBodyResults(backend, device, bodies, world, members, 1);
        var motion = inputs.Upload().Then(dynamics.Predict(Vector3.Zero)).Then(dynamics.UpdateVelocities()).Then(results.Readback(64));

        PhysicsSimulation.Create(world, world.Query(new QueryDescription()), motion, 1).Schedule(0, 0, .1).Complete();

        Near(new(20.1f, 0, 0), world.ReadComponentData<BodyPose>(body).Position);
        Near(Vector3.UnitX, world.ReadComponentData<BodyVelocity>(body).Linear);
        
    }

    [Fact]
    public async Task SelectedDeviceBodyResultsCopyOnlyTheRequestedBodyRange()
    {
        using var backend = D3D12GraphicsBackend.Create();
        using Device device = CreateDevice(backend);
        using var jobs = new Scheduler(new JobConfiguration { WorkerCount = 2 });
        using World world = new World(execution: new WorldJobs(jobs));
        using var bodies = new DeviceBodies(backend, device, 8);
        Entity selected = AddBody(world, bodies, 3, Vector3.UnitX, BodyMotion.Dynamic);
        Entity other = AddBody(world, bodies, 7, new(10, 0, 0), BodyMotion.Dynamic);
        world.Add(selected, new CpuBodyResult());
        world.Set(selected, new BodyVelocity { Linear = Vector3.UnitX });
        world.Set(other, new BodyVelocity { Linear = new(2, 0, 0) });
        Query members = world.Query(new QueryDescription().WithStored<DeviceBodyBinding>());
        Query requested = world.Query(new QueryDescription().WithStored<DeviceBodyBinding>().WithStored<CpuBodyResult>());
        using var inputs = new DeviceBodyInputs(world, members, bodies);
        using var dynamics = new BodyDynamicsCommands(backend, device, world, members, bodies, CompileBodyDynamics(), 64);
        using var results = new DeviceBodyResults(backend, device, bodies, world, requested, capacity: 1);
        var motion = inputs.Upload().Then(dynamics.Predict(Vector3.Zero)).Then(dynamics.UpdateVelocities());
        PhysicsSimulation.Create(world, world.Query(new QueryDescription()), motion, 1).Schedule(0, 0, .1).Complete();
        var unselectedReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        JobHandle held = jobs.CreateExternalFenceHandle(unselectedReady.Task,
            [JobResourceAccess.Write(jobs.GetResourceToken(bodies.States), 7 * DeviceBodies.StateStride, DeviceBodies.StateStride)]);
        var events = new ConcurrentQueue<string>();
        PhysicsStepResult result = PhysicsSimulation.Create(world, world.Query(new QueryDescription()), results.Readback(64), 1).Schedule(0, 0, .1);
        async Task FinishSelected()
        {
            await result.Completion.WaitAsync();
            events.Enqueue("selected-consumed");
            unselectedReady.SetResult();
            events.Enqueue("other-released");
        }
        try { await FinishSelected().WaitAsync(TimeSpan.FromSeconds(20)); }
        finally { unselectedReady.TrySetResult(); }
        result.Complete(); held.Complete();

        Near(new(1.1f, 0, 0), world.ReadComponentData<BodyPose>(selected).Position);
        Near(new(10, 0, 0), world.ReadComponentData<BodyPose>(other).Position);
        Assert.Equal(["selected-consumed", "other-released"], events.ToArray());
         
    }

    [Fact]
    public void SuspendedDeviceBodiesResumeFromTheSameSubstepOrigin()
    {
        using var backend = D3D12GraphicsBackend.Create();
        using Device device = CreateDevice(backend);
        using var jobs = new Scheduler(new JobConfiguration { WorkerCount = 2 });
        using World world = new World(execution: new WorldJobs(jobs));
        using var bodies = new DeviceBodies(backend, device, 1);
        Entity body = AddBody(world, bodies, 0, Vector3.UnitX, BodyMotion.Dynamic);
        world.Set(body, new BodyVelocity { Linear = new(2, 0, 0) });
        Query members = world.Query(new QueryDescription().WithStored<DeviceBodyBinding>());
        D3D12TestShader shader = shaderCompiler.CompileWithSearchPaths("body_suspend_resume", """
            #include "physics_body_dynamics.slang"
            [shader("compute")][numthreads(64,1,1)]
            void Suspend(uint3 id : SV_DispatchThreadID)
            { if(id.x < BodyStep.count) predictDeviceBody(DeviceBodyIndices[id.x], false, false); }
            [shader("compute")][numthreads(64,1,1)]
            void Resume(uint3 id : SV_DispatchThreadID)
            { if(id.x < BodyStep.count) predictDeviceBody(DeviceBodyIndices[id.x], true, true); }
            """, [new("Suspend", SlangStage.Compute), new("UpdateDeviceBodyVelocities", SlangStage.Compute),
                new("Resume", SlangStage.Compute)], "sm_6_0", ShaderDirectory());
        ComputePipelineDesc Entry(int index) => new(shader.SlangCode, shader.Reflection, shader.GetEntryPoint(index), shader.GetEntryCode(index));
        using var inputs = new DeviceBodyInputs(world, members, bodies);
        using var dynamics = new BodyDynamicsCommands(backend, device, world, members, bodies,
            new(Entry(0), Entry(1), Resume: Entry(2)), 64);
        using var results = new DeviceBodyResults(backend, device, bodies);
        var suspend = inputs.Upload().Then(dynamics.Predict(new(0, -10, 0))).Then(results.Readback(64));

        PhysicsSimulation.Create(world, world.Query(new QueryDescription()), suspend, 1).Schedule(4, 2, .1).Complete();

        Near(Vector3.UnitX, world.ReadComponentData<BodyPose>(body).Position);
        Assert.Equal(2.1, world.ReadComponentData<BodyStepState>(body).Time);

        var resume = dynamics.Resume(new(0, -10, 0)).Then(dynamics.UpdateVelocities()).Then(results.Readback(64));
        PhysicsSimulation.Create(world, world.Query(new QueryDescription()), resume, 1).Schedule(4, 2, .1).Complete();

        Near(new(1.2f, -.1f, 0), world.ReadComponentData<BodyPose>(body).Position);
        Near(new(2, -1, 0), world.ReadComponentData<BodyVelocity>(body).Linear);
        Near(Vector3.UnitX, world.ReadComponentData<BodyStepState>(body).StartCenterOfMass);
        Assert.Equal(4, world.ReadComponentData<BodyStepState>(body).Number);
        
    }

    [Fact]
    public void ConsumingAnEarlierCompletedBodyKeepsTheLaterDeviceStepResident()
    {
        using var backend = D3D12GraphicsBackend.Create();
        using Device device = CreateDevice(backend);
        using var jobs = new Scheduler(new JobConfiguration { WorkerCount = 2 });
        using World world = new World(execution: new WorldJobs(jobs));
        using var bodies = new DeviceBodies(backend, device, 1);
        Entity body = AddBody(world, bodies, 0, Vector3.Zero, BodyMotion.Dynamic);
        world.Set(body, new BodyVelocity { Linear = Vector3.UnitX });
        Query members = world.Query(new QueryDescription().WithStored<DeviceBodyBinding>());
        JobResourceAccess[] poseAccesses = ((WorldJobs)world.Execution!).CollectAccess(new SystemAccesses().WriteRandom<BodyPose>());
        using var inputs = new DeviceBodyInputs(world, members, bodies);
        using var dynamics = new BodyDynamicsCommands(backend, device, world, members, bodies, CompileBodyDynamics(), 64);
        using var results = new DeviceBodyResults(backend, device, bodies);
        var motion = dynamics.Predict(Vector3.Zero).Then(dynamics.UpdateVelocities());
        PhysicsStepResult first = PhysicsSimulation.Create(world, world.Query(new QueryDescription()), inputs.Upload().Then(motion), 1)
            .Schedule(0, 0, .1);
        first.Complete();
        var readGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        JobHandle heldPose = jobs.CreateExternalFenceHandle(readGate.Task, poseAccesses);
        PhysicsStepResult earlier = PhysicsSimulation.Create(world, world.Query(new QueryDescription()), results.Readback(64), 1)
            .Schedule(0, 0, .1, first.Completion);
        try
        {
            PhysicsSimulation.Create(world, world.Query(new QueryDescription()), motion, 1).Schedule(1, .1, .1, first.Completion).Complete();
        }
        finally { readGate.SetResult(); }
        heldPose.Complete(); earlier.Complete();
        Near(new(.1f, 0, 0), world.ReadComponentData<BodyPose>(body).Position);

        PhysicsSimulation.Create(world, world.Query(new QueryDescription()), inputs.Upload().Then(motion).Then(results.Readback(64)), 1)
            .Schedule(2, .2, .1).Complete();

        Near(new(.3f, 0, 0), world.ReadComponentData<BodyPose>(body).Position);
        Near(Vector3.UnitX, world.ReadComponentData<BodyVelocity>(body).Linear);
        Assert.Equal(2, world.ReadComponentData<BodyStepState>(body).Number);
        
    }

    [Fact]
    public void CpuVelocityEditsAndNewBodiesJoinTheContinuingResidentMotion()
    {
        using var backend = D3D12GraphicsBackend.Create();
        using Device device = CreateDevice(backend);
        using var jobs = new Scheduler(new JobConfiguration { WorkerCount = 2 });
        using World world = new World(execution: new WorldJobs(jobs));
        using var bodies = new DeviceBodies(backend, device, 3);
        Entity edited = AddBody(world, bodies, 0, Vector3.Zero, BodyMotion.Dynamic);
        Entity resident = AddBody(world, bodies, 1, new(10, 0, 0), BodyMotion.Dynamic);
        world.Set(edited, new BodyVelocity { Linear = Vector3.UnitX });
        world.Set(resident, new BodyVelocity { Linear = new(2, 0, 0) });
        Query members = world.Query(new QueryDescription().WithStored<DeviceBodyBinding>());
        using var inputs = new DeviceBodyInputs(world, members, bodies);
        using var dynamics = new BodyDynamicsCommands(backend, device, world, members, bodies, CompileBodyDynamics(), 64);
        using var results = new DeviceBodyResults(backend, device, bodies);
        var motion = inputs.Upload().Then(dynamics.Predict(Vector3.Zero))
            .Then(dynamics.UpdateVelocities());
        PhysicsStepResult first = PhysicsSimulation.Create(world, world.Query(new QueryDescription()), motion, 1).Schedule(0, 0, .1);
        first.Complete();
        world.Set(edited, new BodyVelocity { Linear = new(3, 0, 0) });
        Entity added = AddBody(world, bodies, 2, new(20, 0, 0), BodyMotion.Dynamic);
        world.Set(added, new BodyVelocity { Linear = new(4, 0, 0) });

        PhysicsSimulation.Create(world, world.Query(new QueryDescription()), motion.Then(results.Readback(64)), 1)
            .Schedule(1, .1, .1, first.Completion).Complete();

        Near(new(.4f, 0, 0), world.ReadComponentData<BodyPose>(edited).Position);
        Near(new(10.4f, 0, 0), world.ReadComponentData<BodyPose>(resident).Position);
        Near(new(20.4f, 0, 0), world.ReadComponentData<BodyPose>(added).Position);
        Near(new(3, 0, 0), world.ReadComponentData<BodyVelocity>(edited).Linear);
        Near(new(2, 0, 0), world.ReadComponentData<BodyVelocity>(resident).Linear);
        Assert.Equal(.2, world.ReadComponentData<BodyStepState>(resident).Time);
        
    }

    private readonly struct CpuBodyResult : IComponent;
}
