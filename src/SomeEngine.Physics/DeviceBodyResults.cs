using System.Runtime.InteropServices;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;
using SomeEngine.Graphics;
using SomeEngine.Job;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.Physics;

/// <summary>Copies selected completed device states into the original entity components.</summary>
public sealed class DeviceBodyResults : IDisposable
{
    private readonly IGraphicsBackend _backend;
    private readonly Device _device;
    private readonly Buffer _readback;
    private readonly CommandContext _commands;
    internal readonly DeviceBodies Bodies;
    internal readonly DeviceBodyState[] States;

    public DeviceBodyResults(IGraphicsBackend backend, Device device, DeviceBodies bodies)
    {
        _backend = backend; _device = device; Bodies = bodies;
        States = new DeviceBodyState[bodies.Capacity];
        _readback = backend.CreateBuffer(device, new(bodies.States.Info.Size, BufferUsages.CopyDestination,
            "Completed body state readback"), MemoryType.Readback);
        _commands = backend.CreateCommandContext(device, new(QueueType.Compute, 0, 1, Label: "Device body readback"));
    }

    public ReadDeviceBodyResults Readback(int rowsPerPacket) => new(this, rowsPerPacket);
    internal JobHandle Schedule(in PhysicsStepContext context, int rowsPerPacket, JobHandle dependency)
    {
        Scheduler scheduler = WorldJobs.GetScheduler(context.World);
        JobResourceAccess[] accesses = [Bodies.GetReadAccess(scheduler),
            JobResourceAccess.Write(scheduler.GetResourceToken(_readback), 0, (long)_readback.Info.Size),
            JobResourceAccess.Write(scheduler.GetResourceToken(_commands))];
        return scheduler.Schedule(new Copy(this, scheduler, context, rowsPerPacket, accesses), accesses, dependency);
    }

    private QueueCompletion Submit()
    {
        _backend.Begin(_commands, new(0, 0, 8));
        _backend.Barrier(_commands, new BufferBarrier(Bodies.States, PipelineSync.ComputeShading, PipelineSync.Copy,
            ResourceAccess.UnorderedAccess, ResourceAccess.CopySource));
        _backend.CopyBuffer(_commands, new(Bodies.States, 0, _readback, 0, _readback.Info.Size));
        _backend.Barrier(_commands, new BufferBarrier(Bodies.States, PipelineSync.Copy, PipelineSync.ComputeShading,
            ResourceAccess.CopySource, ResourceAccess.UnorderedAccess));
        using RecordedCommands recorded = _backend.End(_commands);
        return _backend.Submit(_backend.GetQueue(_device, QueueType.Compute), new([], [], [recorded], [], []));
    }

    private readonly struct Copy(DeviceBodyResults results, Scheduler scheduler, PhysicsStepContext context,
        int rowsPerPacket, JobResourceAccess[] accesses) : IJob
    {
        public void Execute()
        {
            QueueCompletion completion = results.Submit();
            JobHandle ready = scheduler.CreateExternalFenceHandle(new DeviceQueueFence(scheduler, results._backend, completion), accesses);
            JobResourceAccess[] read = [JobResourceAccess.Read(scheduler.GetResourceToken(results._readback), 0, (long)results._readback.Info.Size),
                JobResourceAccess.Write(scheduler, results.States)];
            JobHandle mapped = scheduler.Schedule(new Map(results), read, ready);
            JobHandle applied = new ApplyDeviceBodyResultJob(results).ScheduleParallel(context.World,
                new[] { JobResourceAccess.Read(scheduler, results.States) },
                new JobEntityScheduleOptions(rowsPerPacket: rowsPerPacket, filter: context.Bodies), mapped);
            scheduler.ScheduleFinally(new Collect(results), default, applied);
        }
    }
    private readonly struct Map(DeviceBodyResults results) : IJob
    {
        public void Execute()
        {
            using MappedBuffer mapping = results._backend.Map(results._readback, MapType.Read, BufferRange.Whole);
            mapping.Invalidate(mapping.Range);
            MemoryMarshal.Cast<byte, DeviceBodyState>(mapping.Bytes).CopyTo(results.States);
        }
    }
    private readonly struct Collect(DeviceBodyResults results) : IJob
    {
        public void Execute() => results._backend.CollectCompleted(results._device);
    }
    public void Dispose() { _commands.Dispose(); _readback.Dispose(); }
}

public readonly struct ReadDeviceBodyResults(DeviceBodyResults results, int rowsPerPacket) : IPhysicsStage
{
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => results.Schedule(in context, rowsPerPacket, dependency);
}

internal readonly partial struct ApplyDeviceBodyResultJob(DeviceBodyResults results) : IJobEntity
{
    public void Execute(Entity entity, [JobResources<DeviceBodyReferenceResources>] in DeviceBodyBinding binding,
        ref BodyPose pose, ref BodyVelocity velocity, ref BodyStepState step)
    {
        if (!ReferenceEquals(binding.Bodies, results.Bodies)) return;
        ref readonly DeviceBodyState state = ref results.States[binding.Index];
        if (state.EntityIndex != (uint)entity.Index || state.EntityGeneration != (uint)entity.Generation) return;
        pose.Position = state.Position; pose.Rotation = state.Rotation;
        velocity.Linear = state.LinearVelocity; velocity.Angular = state.AngularVelocity;
        step.StartCenterOfMass = state.StartCenterOfMass; step.StartRotation = state.StartRotation;
        step.PredictedCenterOfMass = state.PredictedCenterOfMass; step.PredictedRotation = state.PredictedRotation;
        step.Number = state.Number; step.Substep = state.Substep; step.Time = state.Time;
    }
}
