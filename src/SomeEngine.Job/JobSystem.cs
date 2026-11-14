namespace SomeEngine.Job;

public static partial class JobSystem
{
    private const int DefaultTestingWorkerCount = 2;
    private static readonly Lock Sync = new();
    private static Scheduler? s_scheduler;
    private static bool s_stopped;

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

    public static int WorkerCount => Current.WorkerCount;

    public static void Initialize(JobConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        config.Validate();
        if (Scheduler.Executing is not null)
            throw new InvalidOperationException("The default scheduler cannot be replaced from a Job callback.");

        Scheduler replacement = new(config);
        Scheduler? previous;
        lock (Sync)
        {
            previous = s_scheduler;
            s_scheduler = replacement;
            s_stopped = false;
        }
        previous?.Dispose();
    }

    public static void Shutdown()
    {
        if (Scheduler.Executing is not null)
            throw new InvalidOperationException("The default scheduler cannot be stopped from a Job callback.");
        Scheduler? previous;
        lock (Sync)
        {
            previous = s_scheduler;
            s_scheduler = null;
            s_stopped = true;
        }
        previous?.Dispose();
    }

    public static Scheduler Default
    {
        get
        {
            lock (Sync)
            {
                if (s_stopped)
                    throw new InvalidOperationException("The default scheduler is stopped. Call Initialize before submitting work.");
                return s_scheduler ??= new Scheduler();
            }
        }
    }

    public static JobStatistics GetStats()
    {
        return Current.GetStats();
    }

    public static JobPayloadLane GetPayloadLane<T>()
        where T : struct
    {
        return JobTraits.GetPayloadLane<T>();
    }

}

public static partial class JobSystem
{
    internal static bool IsExecutingJob => JobThreads.IsActive;

    internal static void OnCompleted(
        JobHandle handle,
        Action<JobHandle, object?> continuation,
        object? state = null)
    {
        // Observer registration does not wait for completion. Callbacks are best-effort notifications
        // and run inline on the registering, completing, or signaling thread; exceptions are suppressed.
        if (handle.Scheduler is { } scheduler)
            scheduler.OnCompleted(handle, continuation, state);
        else
        {
            ArgumentNullException.ThrowIfNull(continuation);
            try { continuation(handle, state); }
            catch { /* Completion observers do not alter the completed operation. */ }
        }
    }

    internal static bool IsCompleted(JobHandle handle)
    {
        return handle.IsCompleted;
    }

    internal static bool NeedsLifetimeTracking(JobHandle handle)
    {
        return handle.Scheduler?.NeedsLifetimeTracking(handle) ?? false;
    }

    internal static void Complete(JobHandle handle)
    {
        handle.Complete();
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
        return Scheduler.Executing?.GetCurrentScope() ?? default;
    }

    internal static bool IsScopeDescendantOf(JobHandle scope, JobHandle ancestor)
    {
        return scope.Scheduler?.IsScopeDescendantOf(scope, ancestor) ?? false;
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

    internal static SynchronousResourceAccess AcquireSynchronousAccess(
        JobResourceAccess access)
    {
        ReadOnlySpan<JobResourceAccess> accesses = stackalloc JobResourceAccess[] { access };
        return Current.AcquireSynchronousAccess(accesses);
    }

    internal static SynchronousResourceAccess AcquireSynchronousAccess(
        ReadOnlySpan<JobResourceAccess> accesses)
    {
        return Current.AcquireSynchronousAccess(accesses);
    }

    /// <summary>The executing job's scheduler, or the default scheduler for a direct caller.</summary>
    /// <remarks>Capture this value before awaiting to retain the initiating scheduler for later work.</remarks>
    public static Scheduler Current => Scheduler.Executing ?? Default;
}
