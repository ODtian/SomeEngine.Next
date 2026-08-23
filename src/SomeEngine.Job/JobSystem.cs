namespace SomeEngine.Job;

public static partial class JobSystem
{
    private const int DefaultTestingWorkerCount = 2;
    private static readonly Lock Sync = new();
    private static long s_nextGeneration;
    private static Scheduler s_scheduler = CreateScheduler(JobRuntimeConfig.Default);

    public static JobSafetyMode SafetyMode
    {
        get => Current.SafetyMode;
        set => Current.SafetyMode = value;
    }

    public static ManagedPayloadPolicy ManagedPayloadPolicy
    {
        get => Current.ManagedPayloadPolicy;
        set => Current.ManagedPayloadPolicy = value;
    }

    public static void Initialize(JobRuntimeConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        config.Validate();

        Scheduler previous;
        lock (Sync)
        {
            previous = s_scheduler;
            s_scheduler = CreateScheduler(config);
        }

        previous.Dispose();
    }

    public static void Shutdown()
    {
        Scheduler previous;
        lock (Sync)
        {
            previous = s_scheduler;
            s_scheduler = CreateScheduler(new JobRuntimeConfig { WorkerCount = 0 });
        }

        previous.Dispose();
    }

    public static JobRuntimeStats GetStats()
    {
        return Current.GetStats();
    }

    public static JobPayloadLane GetPayloadLane<T>()
        where T : struct
    {
        return JobTraits.GetPayloadLane<T>();
    }

    public static JobHandle Schedule<T>(in T job, JobHandle dependency = default)
        where T : struct, IJob
    {
        return Current.Schedule(job, dependency);
    }

    public static JobHandle Schedule<T>(
        in T job,
        JobScheduleOptions options,
        JobHandle dependency = default)
        where T : struct, IJob
    {
        return Current.Schedule(job, options, dependency);
    }

    public static JobHandle Schedule<T>(in T job, JobResourceAccess access, JobHandle dependency = default)
        where T : struct, IJob
    {
        return Current.Schedule(job, access, dependency);
    }

    public static JobHandle Schedule<T>(
        in T job,
        JobResourceAccess access,
        JobScheduleOptions options,
        JobHandle dependency = default)
        where T : struct, IJob
    {
        return Current.Schedule(job, access, options, dependency);
    }

    public static JobHandle Schedule<T>(
        in T job,
        ReadOnlySpan<JobResourceAccess> accesses,
        JobHandle dependency = default)
        where T : struct, IJob
    {
        return Current.Schedule(job, accesses, dependency);
    }

    public static JobHandle Schedule<T>(
        in T job,
        ReadOnlySpan<JobResourceAccess> accesses,
        JobScheduleOptions options,
        JobHandle dependency = default)
        where T : struct, IJob
    {
        return Current.Schedule(job, accesses, options, dependency);
    }

    public static JobHandle ScheduleParallel<T>(
        in T job,
        int length,
        int batchSize,
        JobHandle dependency = default)
        where T : struct, IJobParallelFor
    {
        return Current.ScheduleParallel(job, length, batchSize, dependency);
    }

    public static JobHandle ScheduleParallel<T>(
        in T job,
        int length,
        int batchSize,
        JobScheduleOptions options,
        JobHandle dependency = default)
        where T : struct, IJobParallelFor
    {
        return Current.ScheduleParallel(job, length, batchSize, options, dependency);
    }

    public static JobHandle ScheduleParallel<T>(
        in T job,
        int length,
        int batchSize,
        JobResourceAccess access,
        JobHandle dependency = default)
        where T : struct, IJobParallelFor
    {
        return Current.ScheduleParallel(job, length, batchSize, access, dependency);
    }

    public static JobHandle ScheduleParallel<T>(
        in T job,
        int length,
        int batchSize,
        JobResourceAccess access,
        JobScheduleOptions options,
        JobHandle dependency = default)
        where T : struct, IJobParallelFor
    {
        return Current.ScheduleParallel(job, length, batchSize, access, options, dependency);
    }

    public static JobHandle ScheduleParallel<T>(
        in T job,
        int length,
        int batchSize,
        ReadOnlySpan<JobResourceAccess> accesses,
        JobHandle dependency = default)
        where T : struct, IJobParallelFor
    {
        return Current.ScheduleParallel(job, length, batchSize, accesses, dependency);
    }

    public static JobHandle ScheduleParallel<T>(
        in T job,
        int length,
        int batchSize,
        ReadOnlySpan<JobResourceAccess> accesses,
        JobScheduleOptions options,
        JobHandle dependency = default)
        where T : struct, IJobParallelFor
    {
        return Current.ScheduleParallel(job, length, batchSize, accesses, options, dependency);
    }

    public static JobHandle CombineDependencies(ReadOnlySpan<JobHandle> handles)
    {
        return Current.CombineDependencies(handles);
    }
}

public static partial class JobSystem
{
    internal static bool IsExecutingJob => JobExecutionContext.IsActive;

    public static JobHandle CreateExternalFenceHandle(IJobExternalFence fence)
    {
        return Current.CreateExternalFenceHandle(fence, ReadOnlySpan<JobResourceAccess>.Empty);
    }

    public static JobHandle CreateExternalFenceHandle(IJobExternalFence fence, JobResourceAccess access)
    {
        ReadOnlySpan<JobResourceAccess> accesses = stackalloc JobResourceAccess[] { access };
        return Current.CreateExternalFenceHandle(fence, accesses);
    }

    public static JobHandle CreateExternalFenceHandle(
        IJobExternalFence fence,
        ReadOnlySpan<JobResourceAccess> accesses)
    {
        return Current.CreateExternalFenceHandle(fence, accesses);
    }

    public static void OnCompleted(
        JobHandle handle,
        Action<JobHandle, object?> continuation,
        object? state = null)
    {
        // Observer registration does not wait for completion. Callbacks are best-effort notifications
        // and run inline on the registering, completing, or signaling thread; exceptions are suppressed.
        Current.OnCompleted(handle, continuation, state);
    }

    public static JobResource CreateResource(string? name = null)
    {
        return Current.CreateResource(name);
    }

    public static JobResourceToken CreateResourceToken(string? name = null)
    {
        return Current.CreateResourceToken(name);
    }

    public static JobResource CreateScopeResource(string? name = null)
    {
        return Current.CreateScopeResource(name);
    }

    public static JobResourceToken CreateScopeResourceToken(string? name = null)
    {
        return Current.CreateScopeResourceToken(name);
    }

    public static void ReleaseResource(JobResource resource)
    {
        Current.ReleaseResource(resource);
    }

    public static void ReleaseResourceToken(JobResourceToken token)
    {
        Current.ReleaseResourceToken(token);
    }

    internal static bool IsCompleted(JobHandle handle)
    {
        return Current.IsCompleted(handle);
    }

    internal static bool NeedsLifetimeTracking(JobHandle handle)
    {
        return Current.NeedsLifetimeTracking(handle);
    }

    internal static void Complete(JobHandle handle)
    {
        Current.Complete(handle);
    }

    internal static bool TryHandoffLatencyWork(
        object? state,
        Action<object?, int> action,
        int value,
        JobPriority priority,
        out long sequence)
    {
        return Current.TryHandoffLatencyWork(state, action, value, priority, out sequence);
    }

    internal static void JoinLatencyWork(long sequence)
    {
        Current.JoinLatencyWork(sequence);
    }

    internal static bool TryReclaimLatencyWork(long sequence)
    {
        return Current.TryReclaimLatencyWork(sequence);
    }

    internal static JobSubmissionScope EnterSubmissionScope(IJobSubmissionObserver observer)
    {
        return JobSubmissionTracker.Enter(observer);
    }

    internal static JobHandle GetCurrentScope()
    {
        return Current.GetCurrentScope();
    }

    internal static bool IsScopeDescendantOf(JobHandle scope, JobHandle ancestor)
    {
        return Current.IsScopeDescendantOf(scope, ancestor);
    }

    internal static JobResourceToken GetContainerResourceToken(object container)
    {
        return Current.GetContainerResourceToken(container);
    }

    internal static void RequireCurrentAccess(
        JobResourceAccess required,
        bool requireSingleWorkItem = false)
    {
        Current.RequireCurrentAccess(required, requireSingleWorkItem);
    }

    /// <summary>
    /// Schedules an internal cleanup job after <paramref name="dependency"/> reaches a terminal
    /// state, even when that dependency faulted. The cleanup job is responsible for observing and
    /// propagating the dependency fault after it has restored its owner's invariants.
    /// </summary>
    internal static JobHandle ScheduleFinally<T>(
        in T job,
        JobScheduleOptions options,
        JobHandle dependency)
        where T : struct, IJob
    {
        return Current.ScheduleFinally(job, options, dependency);
    }

    internal static SynchronousResourceOwner AcquireSynchronousAccess(
        JobResourceAccess access)
    {
        ReadOnlySpan<JobResourceAccess> accesses = stackalloc JobResourceAccess[] { access };
        return Current.AcquireSynchronousAccess(accesses);
    }

    internal static SynchronousResourceOwner AcquireSynchronousAccess(
        ReadOnlySpan<JobResourceAccess> accesses)
    {
        return Current.AcquireSynchronousAccess(accesses);
    }

    internal static void ResetForTesting(int workerCount = DefaultTestingWorkerCount)
    {
        ResetForTesting(new JobRuntimeConfig { WorkerCount = workerCount });
    }

    internal static void ResetForTesting(JobRuntimeConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        config.Validate();

        Scheduler previous;
        lock (Sync)
        {
            previous = s_scheduler;
            s_scheduler = CreateScheduler(config);
        }

        previous.Dispose();
    }

    internal static void ShutdownForTesting()
    {
        lock (Sync)
        {
            s_scheduler.Dispose();
            s_scheduler = CreateScheduler(new JobRuntimeConfig { WorkerCount = 0 });
        }
    }

    private static Scheduler CreateScheduler(JobRuntimeConfig config)
    {
        return new Scheduler(config, Interlocked.Increment(ref s_nextGeneration));
    }

    private static Scheduler Current => Volatile.Read(ref s_scheduler);
}




