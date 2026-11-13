using SomeEngine.ECS.Systems;
using SomeEngine.Graphics;
using SomeEngine.Job;

namespace SomeEngine.Physics;

public sealed partial class DeviceBodies
{
    /// <summary>Uploads values after the restored World has accepted its CPU fields and rebuilt dense body bindings.</summary>
    public UploadDeviceBodyStates UploadStates(ReadOnlyMemory<DeviceBodyState> values) => new(this, values);
    internal JobHandle ScheduleStateValues(in PhysicsStepContext context, ReadOnlyMemory<DeviceBodyState> values, JobHandle dependency)
    {
        Scheduler scheduler = context.World.Execution!.Scheduler!;
        JobResourceAccess[] accesses = [JobResourceAccess.Read(scheduler, values), JobResourceAccess.Read(scheduler, _cpuVersions),
            JobResourceAccess.Write(scheduler.GetResourceToken(_upload), 0, (long)values.Length * StateStride)];
        JobHandle written = scheduler.Schedule(new WriteStates(this, values), accesses, dependency);
        return DevicePhysicsJobs.Schedule(scheduler, new TransferStates(this, values.Length), GetInputTransferAccesses(scheduler), written);
    }
    private readonly struct WriteStates(DeviceBodies bodies, ReadOnlyMemory<DeviceBodyState> values) : IJob
    {
        public void Execute()
        {
            Span<DeviceBodyState> destination = bodies.InputStates(0, values.Length);
            values.Span.CopyTo(destination);
            for (int index = 0; index < destination.Length; index++)
                destination[index].InputVersion = bodies._cpuVersions[index].InputVersion;
        }
    }
    private readonly struct TransferStates(DeviceBodies bodies, int count) : IDevicePhysicsSubmission
    {
        public IGraphicsBackend Backend => bodies._backend;
        public Device Device => bodies._device;
        public SomeEngine.Graphics.Queue Queue => Backend.GetQueue(Device, QueueType.Compute);
        public QueueCompletion Submit() => bodies.SubmitInputRanges([new(0, (ulong)count * StateStride)]);
    }
}

public readonly struct UploadDeviceBodyStates(DeviceBodies bodies, ReadOnlyMemory<DeviceBodyState> values) : IPhysicsStage
{
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => bodies.ScheduleStateValues(in context, values, dependency);
}
