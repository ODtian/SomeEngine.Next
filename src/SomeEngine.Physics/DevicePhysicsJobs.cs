using SomeEngine.Graphics;
using SomeEngine.Job;

namespace SomeEngine.Physics;

public interface IDevicePhysicsSubmission
{
    IGraphicsBackend Backend { get; }
    Device Device { get; }
    QueueCompletion Submit();
}

public static class DevicePhysicsJobs
{
    public static JobHandle Schedule<T>(Scheduler scheduler, T work, JobResourceAccess[] accesses,
        JobHandle dependency) where T : IDevicePhysicsSubmission =>
        scheduler.Schedule(new Submit<T>(scheduler, work, accesses), accesses, dependency);

    private readonly struct Submit<T>(Scheduler scheduler, T work, JobResourceAccess[] accesses) : IJob
        where T : IDevicePhysicsSubmission
    {
        public void Execute()
        {
            QueueCompletion completion = work.Submit();
            JobHandle ready = scheduler.CreateExternalFenceHandle(new DeviceQueueFence(scheduler, work.Backend, completion), accesses);
            scheduler.ScheduleFinally(new Collect<T>(work), default, ready);
        }
    }

    private readonly struct Collect<T>(T work) : IJob where T : IDevicePhysicsSubmission
    {
        public void Execute() => work.Backend.CollectCompleted(work.Device);
    }
}
