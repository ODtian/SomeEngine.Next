using SomeEngine.Graphics;
using SomeEngine.Job;
using SomeEngine.Diagnostics;

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
            using var trace = EngineTrace.Begin(DeviceSubmissionTrace<T>.Submit);
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

internal static class DeviceSubmissionTrace<T>
{
    internal static readonly TraceSite Submit = new("physics", $"Device.Submit/{typeof(T)}");
}
