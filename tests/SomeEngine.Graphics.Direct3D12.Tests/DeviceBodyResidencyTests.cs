using System.Numerics;
using System.Collections.Concurrent;
using SlangShaderSharp;
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
    public async Task SelectedDeviceBodyResultsCopyOnlyTheRequestedBodyRange()
    {
        using var backend = D3D12GraphicsBackend.Create();
        using Device device = CreateDevice(backend);
        using var jobs = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using World world = WorldJobs.CreateWorld(scheduler: jobs);
        using var bodies = new DeviceBodies(backend, device, 8);
        Entity selected = AddBody(world, bodies, 3, Vector3.UnitX, BodyMotion.Dynamic);
        Entity other = AddBody(world, bodies, 7, new(10, 0, 0), BodyMotion.Dynamic);
        world.Add(selected, new CpuBodyResult());
        world.Replace(selected, new BodyVelocity { Linear = Vector3.UnitX });
        world.Replace(other, new BodyVelocity { Linear = new(2, 0, 0) });
        QueryHandle members = world.Query(world.QueryDefinition().Read<DeviceBodyBinding>());
        QueryHandle requested = world.Query(world.QueryDefinition().Read<DeviceBodyBinding>().All<CpuBodyResult>());
        using var inputs = new DeviceBodyInputs(world, members, bodies);
        using var dynamics = new DeviceBodyDynamics(backend, device, world, members, bodies, CompileBodyDynamics(), 64);
        using var results = new DeviceBodyResults(backend, device, bodies, world, requested, capacity: 1);
        var motion = inputs.Upload().Then(dynamics.Predict(Vector3.Zero)).Then(dynamics.UpdateVelocities());
        PhysicsSimulation.Create(world, QueryDefinition.Empty, motion, 1).Schedule(0, 0, .1).Complete();
        var unselectedReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        JobHandle held = jobs.CreateExternalFenceHandle(unselectedReady.Task,
            [JobResourceAccess.Write(jobs.GetResourceToken(bodies.States), 7 * DeviceBodies.StateStride, DeviceBodies.StateStride)]);
        var events = new ConcurrentQueue<string>();
        PhysicsStepResult result = PhysicsSimulation.Create(world, QueryDefinition.Empty, results.Readback(64), 1).Schedule(0, 0, .1);
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

        Near(new(1.1f, 0, 0), world.Read<BodyPose>(selected).Position);
        Near(new(10, 0, 0), world.Read<BodyPose>(other).Position);
        Assert.Equal(["selected-consumed", "other-released"], events.ToArray());
        world.ReleaseQuery(requested); world.ReleaseQuery(members);
    }

    [Fact]
    public void SuspendedDeviceBodiesResumeFromTheSameSubstepOrigin()
    {
        using var backend = D3D12GraphicsBackend.Create();
        using Device device = CreateDevice(backend);
        using var jobs = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using World world = WorldJobs.CreateWorld(scheduler: jobs);
        using var bodies = new DeviceBodies(backend, device, 1);
        Entity body = AddBody(world, bodies, 0, Vector3.UnitX, BodyMotion.Dynamic);
        world.Replace(body, new BodyVelocity { Linear = new(2, 0, 0) });
        QueryHandle members = world.Query(world.QueryDefinition().Read<DeviceBodyBinding>());
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
        using var dynamics = new DeviceBodyDynamics(backend, device, world, members, bodies,
            new(Entry(0), Entry(1), Resume: Entry(2)), 64);
        using var results = new DeviceBodyResults(backend, device, bodies);
        var suspend = inputs.Upload().Then(dynamics.Predict(new(0, -10, 0))).Then(results.Readback(64));

        PhysicsSimulation.Create(world, QueryDefinition.Empty, suspend, 1).Schedule(4, 2, .1).Complete();

        Near(Vector3.UnitX, world.Read<BodyPose>(body).Position);
        Assert.Equal(2.1, world.Read<BodyStepState>(body).Time);

        var resume = dynamics.Resume(new(0, -10, 0)).Then(dynamics.UpdateVelocities()).Then(results.Readback(64));
        PhysicsSimulation.Create(world, QueryDefinition.Empty, resume, 1).Schedule(4, 2, .1).Complete();

        Near(new(1.2f, -.1f, 0), world.Read<BodyPose>(body).Position);
        Near(new(2, -1, 0), world.Read<BodyVelocity>(body).Linear);
        Near(Vector3.UnitX, world.Read<BodyStepState>(body).StartCenterOfMass);
        Assert.Equal(4, world.Read<BodyStepState>(body).Number);
        world.ReleaseQuery(members);
    }

    [Fact]
    public void ConsumingAnEarlierCompletedBodyKeepsTheLaterDeviceStepResident()
    {
        using var backend = D3D12GraphicsBackend.Create();
        using Device device = CreateDevice(backend);
        using var jobs = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using World world = WorldJobs.CreateWorld(scheduler: jobs);
        using var bodies = new DeviceBodies(backend, device, 1);
        Entity body = AddBody(world, bodies, 0, Vector3.Zero, BodyMotion.Dynamic);
        world.Replace(body, new BodyVelocity { Linear = Vector3.UnitX });
        QueryHandle members = world.Query(world.QueryDefinition().Read<DeviceBodyBinding>());
        QueryDefinition poseWrite = world.QueryDefinition().ReadWrite<BodyPose>().Build();
        var poseAccesses = new JobResourceAccess[QueryJobAccess.Count(poseWrite)];
        QueryJobAccess.CopyAccesses(world, poseWrite, poseAccesses);
        using var inputs = new DeviceBodyInputs(world, members, bodies);
        using var dynamics = new DeviceBodyDynamics(backend, device, world, members, bodies, CompileBodyDynamics(), 64);
        using var results = new DeviceBodyResults(backend, device, bodies);
        var motion = dynamics.Predict(Vector3.Zero).Then(dynamics.UpdateVelocities());
        PhysicsStepResult first = PhysicsSimulation.Create(world, QueryDefinition.Empty, inputs.Upload().Then(motion), 1)
            .Schedule(0, 0, .1);
        first.Complete();
        var readGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        JobHandle heldPose = jobs.CreateExternalFenceHandle(readGate.Task, poseAccesses);
        PhysicsStepResult earlier = PhysicsSimulation.Create(world, QueryDefinition.Empty, results.Readback(64), 1)
            .Schedule(0, 0, .1, first.Completion);
        try
        {
            PhysicsSimulation.Create(world, QueryDefinition.Empty, motion, 1).Schedule(1, .1, .1, first.Completion).Complete();
        }
        finally { readGate.SetResult(); }
        heldPose.Complete(); earlier.Complete();
        Near(new(.1f, 0, 0), world.Read<BodyPose>(body).Position);

        PhysicsSimulation.Create(world, QueryDefinition.Empty, inputs.Upload().Then(motion).Then(results.Readback(64)), 1)
            .Schedule(2, .2, .1).Complete();

        Near(new(.3f, 0, 0), world.Read<BodyPose>(body).Position);
        Near(Vector3.UnitX, world.Read<BodyVelocity>(body).Linear);
        Assert.Equal(2, world.Read<BodyStepState>(body).Number);
        world.ReleaseQuery(members);
    }

    [Fact]
    public void CpuVelocityEditsAndNewBodiesJoinTheContinuingResidentMotion()
    {
        using var backend = D3D12GraphicsBackend.Create();
        using Device device = CreateDevice(backend);
        using var jobs = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using World world = WorldJobs.CreateWorld(scheduler: jobs);
        using var bodies = new DeviceBodies(backend, device, 3);
        Entity edited = AddBody(world, bodies, 0, Vector3.Zero, BodyMotion.Dynamic);
        Entity resident = AddBody(world, bodies, 1, new(10, 0, 0), BodyMotion.Dynamic);
        world.Replace(edited, new BodyVelocity { Linear = Vector3.UnitX });
        world.Replace(resident, new BodyVelocity { Linear = new(2, 0, 0) });
        QueryHandle members = world.Query(world.QueryDefinition().Read<DeviceBodyBinding>());
        using var inputs = new DeviceBodyInputs(world, members, bodies);
        using var dynamics = new DeviceBodyDynamics(backend, device, world, members, bodies, CompileBodyDynamics(), 64);
        using var results = new DeviceBodyResults(backend, device, bodies);
        var motion = inputs.Upload().Then(dynamics.Predict(Vector3.Zero))
            .Then(dynamics.UpdateVelocities());
        PhysicsStepResult first = PhysicsSimulation.Create(world, QueryDefinition.Empty, motion, 1).Schedule(0, 0, .1);
        first.Complete();
        world.Replace(edited, new BodyVelocity { Linear = new(3, 0, 0) });
        Entity added = AddBody(world, bodies, 2, new(20, 0, 0), BodyMotion.Dynamic);
        world.Replace(added, new BodyVelocity { Linear = new(4, 0, 0) });

        PhysicsSimulation.Create(world, QueryDefinition.Empty, motion.Then(results.Readback(64)), 1)
            .Schedule(1, .1, .1, first.Completion).Complete();

        Near(new(.4f, 0, 0), world.Read<BodyPose>(edited).Position);
        Near(new(10.4f, 0, 0), world.Read<BodyPose>(resident).Position);
        Near(new(20.4f, 0, 0), world.Read<BodyPose>(added).Position);
        Near(new(3, 0, 0), world.Read<BodyVelocity>(edited).Linear);
        Near(new(2, 0, 0), world.Read<BodyVelocity>(resident).Linear);
        Assert.Equal(.2, world.Read<BodyStepState>(resident).Time);
        world.ReleaseQuery(members);
    }

    private readonly struct CpuBodyResult : IComponent;
}
