using SomeEngine.ECS.Systems;
using SomeEngine.Graphics;
using SomeEngine.Job;

namespace SomeEngine.Physics;

public sealed partial class DeviceBodies
{
    /// <summary>Continues an existing representation in a larger buffer, including its accepted component versions.</summary>
    public CopyDeviceBodies CopyFrom(DeviceBodies source) => new(this, source);

    internal JobHandle ScheduleCopy(DeviceBodies source, Scheduler scheduler, JobHandle dependency) =>
        DevicePhysicsJobs.Schedule(scheduler, new Copy(this, source),
            [source.GetReadAccess(scheduler), GetWriteAccess(scheduler),
                JobResourceAccess.Read(scheduler, source._cpuVersions), JobResourceAccess.Write(scheduler, _cpuVersions),
                JobResourceAccess.Write(scheduler.GetResourceToken(_commands))], dependency);

    private QueueCompletion SubmitCopy(DeviceBodies source)
    {
        _backend.Begin(_commands, new(0, 0, 8));
        _backend.Barrier(_commands, new BufferBarrier(source.States, PipelineSync.ComputeShading, PipelineSync.Copy,
            ResourceAccess.UnorderedAccess, ResourceAccess.CopySource));
        _backend.Barrier(_commands, new BufferBarrier(States, States.InitialSync, PipelineSync.Copy,
            States.InitialAccess, ResourceAccess.CopyDestination));
        _backend.CopyBuffer(_commands, new(source.States, 0, States, 0, source.States.Info.Size));
        _backend.Barrier(_commands, new BufferBarrier(source.States, PipelineSync.Copy, PipelineSync.ComputeShading,
            ResourceAccess.CopySource, ResourceAccess.UnorderedAccess));
        _backend.Barrier(_commands, new BufferBarrier(States, PipelineSync.Copy, PipelineSync.ComputeShading,
            ResourceAccess.CopyDestination, ResourceAccess.UnorderedAccess));
        source._cpuVersions.CopyTo(_cpuVersions, 0);
        _initialized = true;
        using RecordedCommands recorded = _backend.End(_commands);
        return _backend.Submit(_backend.GetQueue(_device, QueueType.Compute), new([], [], [recorded], [], []));
    }

    private readonly struct Copy(DeviceBodies destination, DeviceBodies source) : IDevicePhysicsSubmission
    {
        public IGraphicsBackend Backend => destination._backend;
        public Device Device => destination._device;
        public QueueCompletion Submit() => destination.SubmitCopy(source);
    }
}

public readonly struct CopyDeviceBodies(DeviceBodies destination, DeviceBodies source) : IPhysicsStage
{
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) =>
        destination.ScheduleCopy(source, WorldJobs.GetScheduler(context.World), dependency);
}
