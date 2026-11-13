using SomeEngine.Graphics;
using SomeEngine.Job;
using SomeEngine.Diagnostics;

namespace SomeEngine.Physics;

public interface IDevicePhysicsSubmission
{
    IGraphicsBackend Backend { get; }
    Device Device { get; }
    /// <summary>The queue used by synchronous Submit; omitted submissions complete separately through SubmitAsync.</summary>
    SomeEngine.Graphics.Queue? Queue => null;
    QueueCompletion Submit();
    ValueTask<QueueCompletion> SubmitAsync() => new(Submit());
}

public static class DevicePhysicsJobs
{
    public static JobHandle Schedule<T>(Scheduler scheduler, T work, JobResourceAccess[] accesses,
        JobHandle dependency) where T : IDevicePhysicsSubmission => PhysicsStageCommands.Recording is { } commands
            ? commands.Add(work, accesses)
            : scheduler.Schedule(new Submit<T>(scheduler, work, accesses), accesses, dependency);

    private readonly struct Submit<T>(Scheduler scheduler, T work, JobResourceAccess[] accesses) : IJob
        where T : IDevicePhysicsSubmission
    {
        public void Execute()
        {
            using var trace = EngineTrace.Begin(DeviceSubmissionTrace<T>.Submit);
            scheduler.CreateExternalFenceHandle(Complete(work), accesses);
        }
    }

    private static async ValueTask Complete<T>(T work) where T : IDevicePhysicsSubmission
    {
        QueueCompletion completion = await work.SubmitAsync().ConfigureAwait(false);
        await work.Backend.WaitAsync(completion).ConfigureAwait(false);
        work.Backend.CollectCompleted(work.Device);
    }
}

internal static class DeviceSubmissionTrace<T>
{
    internal static readonly TraceSite Submit = new("physics", $"Device.Submit/{typeof(T)}");
}
