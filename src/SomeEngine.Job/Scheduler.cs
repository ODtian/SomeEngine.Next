using System.Collections.Generic;
using System.Buffers;
using System.Runtime.ExceptionServices;

namespace SomeEngine.Job;

internal sealed partial class Scheduler : IDisposable
{
    private const string InvalidJobPriorityMessage = "Job priority is invalid.";
    private const string InvalidBatchSizeMessage = "Batch size must be positive.";
    private readonly JobRuntimeConfig _config;
    private readonly RuntimeCounters _counters;
    private readonly ICompletionStore _completion;
    private readonly IDependencyStore _dependencies;
    private readonly IExecutionStore _execution;
    private readonly IScopeStore _scopes;
    private readonly IFenceStore _fences;
    private readonly ResourceManager _resources;
    private readonly IWorkQueue _workQueue;
    private int _managedPayloadPolicy;

    [ThreadStatic]
    private static ScopeToken s_currentScope;

    internal Scheduler(JobRuntimeConfig config, long generation)
    {
        ArgumentNullException.ThrowIfNull(config);
        config.Validate();

        Generation = generation;
        _config = config;
        _counters = new RuntimeCounters(config.EnableCounters);
        var completion = new CompletionStore(config, _counters, generation);
        _completion = completion;
        _dependencies = completion;
        _execution = completion;
        _scopes = completion;
        _fences = completion;
        _resources = new ResourceManager(config, _counters, generation);
        _workQueue = new WorkQueue(this, config, _counters);
        _managedPayloadPolicy = (int)config.ManagedPayloadPolicy;
    }

    internal long Generation { get; }

    internal JobSafetyMode SafetyMode
    {
        get => _resources.SafetyMode;
        set => _resources.SafetyMode = value;
    }

    internal ManagedPayloadPolicy ManagedPayloadPolicy
    {
        get => (ManagedPayloadPolicy)Volatile.Read(ref _managedPayloadPolicy);
        set
        {
            if (!Enum.IsDefined(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            Volatile.Write(ref _managedPayloadPolicy, (int)value);
        }
    }

    internal JobRuntimeStats GetStats()
    {
        return _counters.Snapshot();
    }

    internal JobHandle Schedule<T>(in T job, JobHandle dependency)
        where T : struct, IJob
    {
        return Schedule(job, JobScheduleOptions.Default, dependency);
    }

    internal JobHandle Schedule<T>(in T job, JobScheduleOptions options, JobHandle dependency)
        where T : struct, IJob
    {
        return Schedule(job, ReadOnlySpan<JobResourceAccess>.Empty, options, dependency);
    }

    internal JobHandle Schedule<T>(in T job, JobResourceAccess access, JobHandle dependency)
        where T : struct, IJob
    {
        ReadOnlySpan<JobResourceAccess> accesses = stackalloc JobResourceAccess[] { access };
        return Schedule(job, accesses, JobScheduleOptions.Default, dependency);
    }

    internal JobHandle Schedule<T>(
        in T job,
        JobResourceAccess access,
        JobScheduleOptions options,
        JobHandle dependency)
        where T : struct, IJob
    {
        ReadOnlySpan<JobResourceAccess> accesses = stackalloc JobResourceAccess[] { access };
        return Schedule(job, accesses, options, dependency);
    }

    internal JobHandle Schedule<T>(in T job, ReadOnlySpan<JobResourceAccess> accesses, JobHandle dependency)
        where T : struct, IJob
    {
        return Schedule(job, accesses, JobScheduleOptions.Default, dependency);
    }

    internal JobHandle Schedule<T>(
        in T job,
        ReadOnlySpan<JobResourceAccess> accesses,
        JobScheduleOptions options,
        JobHandle dependency)
        where T : struct, IJob
    {
        JobPayloadLane lane = BeginSchedule<T>(options);
        bool hasResourceAccesses = accesses.Length != 0;
        bool hasScheduleDependencies = dependency.Index != 0 || hasResourceAccesses;
        JobHandle state = CreateState(
            pendingWork: 1,
            pendingDependencies: 0,
            scheduleDependenciesSealed: !hasScheduleDependencies);
        ResourceAccessRegistration registration = ResourceAccessRegistration.Empty;
        WorkBatch work = default;
        try
        {
            registration = RegisterAccesses<T>(state, accesses, hasResourceAccesses);
            AttachToCurrentScope(state);
            ScheduleSingleWork(job, state, dependency, registration, options, hasScheduleDependencies, ref work);
            _counters.Scheduled(lane);
            return state;
        }
        catch
        {
            CleanupUnscheduled(state, registration, work);
            throw;
        }
    }

    private JobPayloadLane BeginSchedule<T>(JobScheduleOptions options)
        where T : struct
    {
        ValidatePriority(options);
        EnsureCurrentScopeBelongsToThisRuntime();
        JobPayloadLane lane = JobTraits.GetPayloadLane<T>();
        ApplyManagedPayloadPolicy<T>(lane);
        return lane;
    }

    private static void ValidatePriority(JobScheduleOptions options)
    {
        if (!Enum.IsDefined(options.Priority))
            throw new ArgumentOutOfRangeException(nameof(options), options.Priority, InvalidJobPriorityMessage);
    }

    private ResourceAccessRegistration RegisterAccesses<T>(
        JobHandle state,
        ReadOnlySpan<JobResourceAccess> accesses,
        bool hasResourceAccesses)
        where T : struct
    {
        if (!hasResourceAccesses)
            return ResourceAccessRegistration.Empty;

        var registration = _resources.RegisterAccesses(state, accesses, typeof(T), s_currentScope.ToHandle());
        SetResourceAccesses(state, registration);
        return registration;
    }

    private void ScheduleSingleWork<T>(
        in T job,
        JobHandle state,
        JobHandle dependency,
        ResourceAccessRegistration registration,
        JobScheduleOptions options,
        bool hasScheduleDependencies,
        ref WorkBatch work)
        where T : struct, IJob
    {
        WorkStream<ScheduledJob<T>> stream = WorkStream<ScheduledJob<T>>.Instance;
        var scheduled = new ScheduledJob<T>(job, state);
        if (!hasScheduleDependencies)
        {
            _workQueue.EnqueueReadySingle(stream, scheduled, options.Priority);
            return;
        }

        int slot = stream.Prepare(scheduled);
        work = WorkBatch.CreateSingle(stream, slot, options.Priority);
        if (RegisterScheduleDependencies(state, dependency, registration, work))
            _workQueue.Enqueue(work);
    }

    private void CleanupUnscheduled(
        JobHandle state,
        ResourceAccessRegistration registration,
        WorkBatch work)
    {
        work.ReleaseJobs();
        ReleaseAccessesIfRegistered(registration);
        CancelUnscheduledState(state);
    }

    internal JobHandle ScheduleParallel<T>(in T job, int length, int batchSize, JobHandle dependency)
        where T : struct, IJobParallelFor
    {
        return ScheduleParallel(job, length, batchSize, JobScheduleOptions.Default, dependency);
    }

    internal JobHandle ScheduleParallel<T>(
        in T job,
        int length,
        int batchSize,
        JobScheduleOptions options,
        JobHandle dependency)
        where T : struct, IJobParallelFor
    {
        return ScheduleParallel(job, length, batchSize, ReadOnlySpan<JobResourceAccess>.Empty, options, dependency);
    }

    internal JobHandle ScheduleParallel<T>(
        in T job,
        int length,
        int batchSize,
        JobResourceAccess access,
        JobHandle dependency)
        where T : struct, IJobParallelFor
    {
        ReadOnlySpan<JobResourceAccess> accesses = stackalloc JobResourceAccess[] { access };
        return ScheduleParallel(job, length, batchSize, accesses, JobScheduleOptions.Default, dependency);
    }

    internal JobHandle ScheduleParallel<T>(
        in T job,
        int length,
        int batchSize,
        JobResourceAccess access,
        JobScheduleOptions options,
        JobHandle dependency)
        where T : struct, IJobParallelFor
    {
        ReadOnlySpan<JobResourceAccess> accesses = stackalloc JobResourceAccess[] { access };
        return ScheduleParallel(job, length, batchSize, accesses, options, dependency);
    }

    internal JobHandle ScheduleParallel<T>(
        in T job,
        int length,
        int batchSize,
        ReadOnlySpan<JobResourceAccess> accesses,
        JobHandle dependency)
        where T : struct, IJobParallelFor
    {
        return ScheduleParallel(job, length, batchSize, accesses, JobScheduleOptions.Default, dependency);
    }

    internal JobHandle ScheduleParallel<T>(
        in T job,
        int length,
        int batchSize,
        ReadOnlySpan<JobResourceAccess> accesses,
        JobScheduleOptions options,
        JobHandle dependency)
        where T : struct, IJobParallelFor
    {
        var lane = BeginParallelSchedule<T>(options, batchSize);
        if (length <= 0)
            return dependency;

        int batchCount = CalculateParallelBatchCount(length, batchSize);
        _workQueue.EnsureItemCapacity(batchCount);
        bool hasResourceAccesses = accesses.Length != 0;
        bool hasScheduleDependencies = dependency.Index != 0 || hasResourceAccesses;
        JobHandle state = CreateState(
            batchCount,
            pendingDependencies: 0,
            scheduleDependenciesSealed: !hasScheduleDependencies);
        ResourceAccessRegistration registration = ResourceAccessRegistration.Empty;
        WorkBatch work = default;
        int[]? slots = null;
        WorkStream<ScheduledParallelJob<T>>? stream = null;
        int preparedSlots = 0;
        try
        {
            registration = RegisterAccesses<T>(state, accesses, hasResourceAccesses);
            AttachToCurrentScope(state);
            ScheduleParallelWork(
                job,
                state,
                dependency,
                registration,
                options,
                length,
                batchSize,
                batchCount,
                hasScheduleDependencies,
                ref work,
                ref slots,
                ref stream,
                ref preparedSlots);
            _counters.Scheduled(lane);
            return state;
        }
        catch
        {
            CleanupParallelUnscheduled(work, stream, slots, preparedSlots);
            ReleaseAccessesIfRegistered(registration);
            CancelUnscheduledState(state);
            throw;
        }
    }

    private JobPayloadLane BeginParallelSchedule<T>(JobScheduleOptions options, int batchSize)
        where T : struct
    {
        if (batchSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(batchSize), InvalidBatchSizeMessage);

        ValidatePriority(options);
        EnsureCurrentScopeBelongsToThisRuntime();
        JobPayloadLane lane = JobTraits.GetPayloadLane<T>();
        ApplyManagedPayloadPolicy<T>(lane);
        return lane;
    }

    private void ScheduleParallelWork<T>(
        in T job,
        JobHandle state,
        JobHandle dependency,
        ResourceAccessRegistration registration,
        JobScheduleOptions options,
        int length,
        int batchSize,
        int batchCount,
        bool hasScheduleDependencies,
        ref WorkBatch work,
        ref int[]? slots,
        ref WorkStream<ScheduledParallelJob<T>>? stream,
        ref int preparedSlots)
        where T : struct, IJobParallelFor
    {
        stream = WorkStream<ScheduledParallelJob<T>>.Instance;
        ScheduledParallelJobSource<T> source = new(job, state, length, batchSize);
        if (!hasScheduleDependencies)
        {
            _workQueue.EnqueueReadyMany(stream, batchCount, ref source, options.Priority);
            return;
        }

        slots = ArrayPool<int>.Shared.Rent(batchCount);
        preparedSlots = stream.PrepareMany(slots.AsSpan(0, batchCount), ref source);
        work = WorkBatch.CreateArray(stream, slots, batchCount, pooledSlotsArray: true, options.Priority);
        slots = null;
        if (RegisterScheduleDependencies(state, dependency, registration, work))
            _workQueue.Enqueue(work);
    }

    private static void CleanupParallelUnscheduled<T>(
        WorkBatch work,
        WorkStream<ScheduledParallelJob<T>>? stream,
        int[]? slots,
        int preparedSlots)
        where T : struct, IJobParallelFor
    {
        if (work.HasValue)
        {
            work.ReleaseJobs();
            return;
        }

        if (stream is not null && slots is not null)
        {
            for (int i = 0; i < preparedSlots; i++)
                stream.Cancel(slots[i]);
        }

        if (slots is not null)
            ArrayPool<int>.Shared.Return(slots);
    }
}

internal sealed partial class Scheduler
{
    internal JobResource CreateResource(string? name)
    {
        EnsureCurrentScopeBelongsToThisRuntime();
        return _resources.CreateResource(name);
    }

    internal JobResourceToken CreateResourceToken(string? name)
    {
        EnsureCurrentScopeBelongsToThisRuntime();
        return _resources.CreateToken(name);
    }

    internal void ReleaseResource(JobResource resource)
    {
        _resources.Release(resource);
    }

    internal void ReleaseResourceToken(JobResourceToken token)
    {
        _resources.Release(token);
    }

    public void Dispose()
    {
        _workQueue.Dispose();
    }

    private static int CalculateParallelBatchCount(int length, int batchSize)
    {
        return ((length - 1) / batchSize) + 1;
    }

}



