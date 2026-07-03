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

    internal static void Complete(JobHandle handle)
    {
        Current.Complete(handle);
    }

    internal static JobResourceToken GetContainerResourceToken(object container)
    {
        return Current.GetContainerResourceToken(container);
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




