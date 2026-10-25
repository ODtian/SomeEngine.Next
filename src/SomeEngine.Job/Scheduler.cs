using System.Collections.Generic;
using System.Buffers;
using System.Runtime.ExceptionServices;

namespace SomeEngine.Job;

public sealed partial class Scheduler : IDisposable
{
    private const string InvalidJobPriorityMessage = "Job priority is invalid.";
    private const string InvalidBatchSizeMessage = "Batch size must be positive.";
    private readonly JobRuntimeConfig _config;
    private readonly RuntimeCounters _counters;
    private readonly CompletionStore _completion;
    private readonly IDependencyStore _dependencies;
    private readonly IExecutionStore _execution;
    private readonly IScopeStore _scopes;
    private readonly IFenceStore _fences;
    private readonly ResourceManager _resources;
    private readonly Lock _submissionObserverGate = new();
    private readonly Dictionary<SubmissionResourceIdentity, IJobSubmissionObserver> _submissionObservers = [];
    private readonly IWorkQueue _workQueue;
    private int _managedPayloadPolicy;
    private static long s_nextGeneration;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Type, WorkStream> _streams = new();

    [ThreadStatic]
    private static Scheduler? s_executing;

    internal static Scheduler? Executing => s_executing;

    public Scheduler(JobRuntimeConfig? config = null)
        : this(config ?? JobRuntimeConfig.Default, Interlocked.Increment(ref s_nextGeneration))
    {
    }

    private WorkStream<TItem> GetWorkStream<TItem>(JobCategory? category = null)
        where TItem : struct, IWorkStreamItem<TItem> =>
        category is not null ? category.GetWorkStream<TItem>() :
        (WorkStream<TItem>)_streams.GetOrAdd(typeof(TItem), static _ => new WorkStream<TItem>());

    [ThreadStatic]
    private static ScopeToken s_currentScope;

    internal Scheduler(JobRuntimeConfig config, long generation)
    {
        ArgumentNullException.ThrowIfNull(config);
        config.Validate();

        Generation = generation;
        _config = config;
        _counters = new RuntimeCounters(config.EnableCounters);
        var completion = new CompletionStore(this, config, _counters, generation);
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

    internal IJobSubmissionObserver? GetSubmissionObserver(JobResourceAccess access)
    {
        var identity = new SubmissionResourceIdentity(
            access.Kind,
            access.Id,
            access.Version,
            access.Generation);
        lock (_submissionObserverGate)
        {
            return _submissionObservers.TryGetValue(identity, out IJobSubmissionObserver? observer)
                ? observer
                : null;
        }
    }

    private readonly record struct SubmissionResourceIdentity(
        ResourceKind Kind,
        int Id,
        int Version,
        long Generation);

    public JobSafetyMode SafetyMode
    {
        get => _resources.SafetyMode;
        set => _resources.SafetyMode = value;
    }

    public ManagedPayloadPolicy ManagedPayloadPolicy
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

    internal int WorkerCount => _config.WorkerCount;

    public JobRuntimeStats GetStats()
    {
        return _counters.Snapshot();
    }

    internal bool TryHandoffLatencyWork(
        object? state,
        Action<object?, int> action,
        int value,
        JobPriority priority,
        out long sequence)
    {
        EnsureCurrentScopeBelongsToThisRuntime();
        return _workQueue.TryHandoffLatencyWork(state, action, value, priority, out sequence);
    }

    internal void JoinLatencyWork(long sequence)
    {
        _workQueue.JoinLatencyWork(sequence);
    }

    internal bool TryReclaimLatencyWork(long sequence) =>
        _workQueue.TryReclaimLatencyWork(sequence);

    public JobHandle Schedule<T>(in T job, JobHandle dependency = default)
        where T : struct, IJob
    {
        return Schedule(job, JobScheduleOptions.Default, dependency);
    }

    public JobHandle Schedule<T>(in T job, JobScheduleOptions options, JobHandle dependency = default)
        where T : struct, IJob
    {
        return Schedule(job, ReadOnlySpan<JobResourceAccess>.Empty, options, dependency);
    }

    public JobHandle Schedule<T>(in T job, JobResourceAccess access, JobHandle dependency = default)
        where T : struct, IJob
    {
        ReadOnlySpan<JobResourceAccess> accesses = stackalloc JobResourceAccess[] { access };
        return Schedule(job, accesses, JobScheduleOptions.Default, dependency);
    }

    public JobHandle Schedule<T>(
        in T job,
        JobResourceAccess access,
        JobScheduleOptions options,
        JobHandle dependency = default)
        where T : struct, IJob
    {
        ReadOnlySpan<JobResourceAccess> accesses = stackalloc JobResourceAccess[] { access };
        return Schedule(job, accesses, options, dependency);
    }

    public JobHandle Schedule<T>(in T job, ReadOnlySpan<JobResourceAccess> accesses, JobHandle dependency = default)
        where T : struct, IJob
    {
        return Schedule(job, accesses, JobScheduleOptions.Default, dependency);
    }

    public JobHandle Schedule<T>(
        in T job,
        ReadOnlySpan<JobResourceAccess> accesses,
        JobScheduleOptions options,
        JobHandle dependency = default)
        where T : struct, IJob
    {
        return ScheduleCore(
            job,
            accesses,
            options,
            dependency,
            ignoreExplicitDependencyFault: false);
    }

    public JobHandle ScheduleFinally<T>(
        in T job,
        JobScheduleOptions options,
        JobHandle dependency)
        where T : struct, IJob
    {
        return ScheduleFinally(job, ReadOnlySpan<JobResourceAccess>.Empty, options, dependency);
    }

    public JobHandle ScheduleFinally<T>(
        in T job,
        ReadOnlySpan<JobResourceAccess> accesses,
        JobScheduleOptions options,
        JobHandle dependency)
        where T : struct, IJob
    {
        return ScheduleCore(
            job,
            accesses,
            options,
            dependency,
            ignoreExplicitDependencyFault: true);
    }

    private JobHandle ScheduleCore<T>(
        in T job,
        ReadOnlySpan<JobResourceAccess> accesses,
        JobScheduleOptions options,
        JobHandle dependency,
        bool ignoreExplicitDependencyFault)
        where T : struct, IJob
    {
        using Submission submissionScope = EnterSubmission();
        ValidateDependency(dependency);
        JobPayloadLane lane = BeginSchedule<T>(options);
        options = ResolveCategoryOptions(options);
        JobSubmissionReservation submission = default;
        bool ownsCategoryRequest = false;
        bool hasResourceAccesses = accesses.Length != 0;
        bool hasScheduleDependencies = dependency.Index != 0 || hasResourceAccesses;
        bool deferResourceActivation =
            hasResourceAccesses &&
            dependency.Index != 0 &&
            !_dependencies.IsSatisfied(dependency, waitForWorkOnly: false);
        JobHandle state = default;
        ResourceAccessRegistration registration = ResourceAccessRegistration.Empty;
        ResourceAccessReservation reservation = ResourceAccessReservation.Empty;
        WorkBatch work = default;
        try
        {
            ownsCategoryRequest = StartCategoryRequest(options.Category);
            submission = JobSubmissionTracker.Begin(this, accesses);
            state = CreateState(
                pendingWork: 1,
                pendingDependencies: 0,
                scheduleDependenciesSealed: !hasScheduleDependencies);
            if (deferResourceActivation)
                reservation = ReserveAccesses<T>(accesses);
            else
                registration = RegisterAccesses<T>(state, accesses, hasResourceAccesses);
            AttachToCurrentScope(state);
            submission.Bind(state);
            ScheduleSingleWork(
                job,
                state,
                dependency,
                registration,
                reservation,
                options,
                hasScheduleDependencies,
                deferResourceActivation,
                ignoreExplicitDependencyFault,
                ref work);
            BindCategoryRequest(state, options.Category, ownsCategoryRequest);
            _counters.Scheduled(lane);
            return state;
        }
        catch
        {
            try
            {
                CancelReservation(reservation);
                if (state.Index != 0)
                    CleanupUnscheduled(state, registration, work);
            }
            finally
            {
                submission.Rollback();
                if (ownsCategoryRequest)
                    options.Category!.CompleteRequest();
            }
            throw;
        }
        finally { submission.Release(); }
    }

    private JobPayloadLane BeginSchedule<T>(JobScheduleOptions options)
        where T : struct, IJob
    {
        ValidatePriority(options);
        EnsureCurrentScopeBelongsToThisRuntime();
        JobTraits.RequireSynchronousJob<T>();
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

        var registration = _resources.RegisterAccesses(state, accesses, typeof(T));
        SetResourceAccesses(state, registration);
        return registration;
    }

    private void ScheduleSingleWork<T>(
        in T job,
        JobHandle state,
        JobHandle dependency,
        ResourceAccessRegistration registration,
        ResourceAccessReservation reservation,
        JobScheduleOptions options,
        bool hasScheduleDependencies,
        bool deferResourceActivation,
        bool ignoreExplicitDependencyFault,
        ref WorkBatch work)
        where T : struct, IJob
    {
        WorkStream<ScheduledJob<T>> stream = GetWorkStream<ScheduledJob<T>>(options.Category);
        var scheduled = new ScheduledJob<T>(job, state);
        if (!hasScheduleDependencies)
        {
            _workQueue.EnqueueReadySingle(stream, scheduled, options.Priority);
            return;
        }

        int slot = stream.Prepare(scheduled);
        work = WorkBatch.CreateSingle(stream, slot, options.Priority);
        if (deferResourceActivation)
        {
            RegisterDeferredResourceScheduleDependency(
                state,
                dependency,
                reservation,
                work,
                ignoreExplicitDependencyFault);
        }
        else if (RegisterScheduleDependencies(
                     state,
                     dependency,
                     registration,
                     work,
                     ignoreExplicitDependencyFault))
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

    public JobHandle ScheduleParallel<T>(in T job, int length, int batchSize, JobHandle dependency = default)
        where T : struct, IJobParallelFor
    {
        return ScheduleParallel(job, length, batchSize, JobScheduleOptions.Default, dependency);
    }

    public JobHandle ScheduleParallel<T>(
        in T job,
        int length,
        int batchSize,
        JobScheduleOptions options,
        JobHandle dependency = default)
        where T : struct, IJobParallelFor
    {
        return ScheduleParallel(job, length, batchSize, ReadOnlySpan<JobResourceAccess>.Empty, options, dependency);
    }

    public JobHandle ScheduleParallel<T>(
        in T job,
        int length,
        int batchSize,
        JobResourceAccess access,
        JobHandle dependency = default)
        where T : struct, IJobParallelFor
    {
        ReadOnlySpan<JobResourceAccess> accesses = stackalloc JobResourceAccess[] { access };
        return ScheduleParallel(job, length, batchSize, accesses, JobScheduleOptions.Default, dependency);
    }

    public JobHandle ScheduleParallel<T>(
        in T job,
        int length,
        int batchSize,
        JobResourceAccess access,
        JobScheduleOptions options,
        JobHandle dependency = default)
        where T : struct, IJobParallelFor
    {
        ReadOnlySpan<JobResourceAccess> accesses = stackalloc JobResourceAccess[] { access };
        return ScheduleParallel(job, length, batchSize, accesses, options, dependency);
    }

    public JobHandle ScheduleParallel<T>(
        in T job,
        int length,
        int batchSize,
        ReadOnlySpan<JobResourceAccess> accesses,
        JobHandle dependency = default)
        where T : struct, IJobParallelFor
    {
        return ScheduleParallel(job, length, batchSize, accesses, JobScheduleOptions.Default, dependency);
    }

    public JobHandle ScheduleParallel<T>(
        in T job,
        int length,
        int batchSize,
        ReadOnlySpan<JobResourceAccess> accesses,
        JobScheduleOptions options,
        JobHandle dependency = default)
        where T : struct, IJobParallelFor
    {
        using Submission submissionScope = EnterSubmission();
        ValidateDependency(dependency);
        var lane = BeginParallelSchedule<T>(options, batchSize);
        if (length <= 0)
            return dependency;
        options = ResolveCategoryOptions(options);

        bool automaticBatch = batchSize == JobScheduleOptions.AutomaticBatchSize;
        int resolvedBatchSize = automaticBatch
            ? ParallelBatchSizer<T>.Resolve(
                length,
                _config.WorkerCount,
                _config.AutoBatchTargetMicroseconds,
                _config.AutoBatchMaxTilesPerWorker)
            : batchSize;

        JobSubmissionReservation submission = default;
        bool ownsCategoryRequest = false;

        int batchCount = CalculateParallelBatchCount(length, resolvedBatchSize);
        _workQueue.EnsureItemCapacity(batchCount);
        int tokenCount = CalculateParallelTokenCount(batchCount);
        if (options.Category is { } category)
            tokenCount = Math.Min(tokenCount, category.Config.MaxExecuting);
        bool hasResourceAccesses = accesses.Length != 0;
        bool hasScheduleDependencies = dependency.Index != 0 || hasResourceAccesses;
        bool deferResourceActivation =
            hasResourceAccesses &&
            dependency.Index != 0 &&
            !_dependencies.IsSatisfied(dependency, waitForWorkOnly: false);
        JobHandle state = default;
        ResourceAccessRegistration registration = ResourceAccessRegistration.Empty;
        ResourceAccessReservation reservation = ResourceAccessReservation.Empty;
        WorkBatch work = default;
        int[]? slots = null;
        WorkStream<ScheduledParallelToken<T>>? stream = null;
        ParallelJobGroup<T>? group = null;
        int preparedSlots = 0;
        try
        {
            ownsCategoryRequest = StartCategoryRequest(options.Category);
            submission = JobSubmissionTracker.Begin(this, accesses);
            state = CreateState(
                batchCount,
                pendingDependencies: 0,
                scheduleDependenciesSealed: !hasScheduleDependencies);
            if (deferResourceActivation)
                reservation = ReserveAccesses<T>(accesses);
            else
                registration = RegisterAccesses<T>(state, accesses, hasResourceAccesses);
            AttachToCurrentScope(state);
            submission.Bind(state);
            ScheduleParallelWork(
                job,
                state,
                dependency,
                registration,
                reservation,
                options,
                length,
                resolvedBatchSize,
                batchCount,
                tokenCount,
                automaticBatch,
                hasScheduleDependencies,
                deferResourceActivation,
                ref work,
                ref slots,
                ref stream,
                ref group,
                ref preparedSlots);
            BindCategoryRequest(state, options.Category, ownsCategoryRequest);
            _counters.Scheduled(lane);
            return state;
        }
        catch
        {
            try
            {
                CancelReservation(reservation);
                CleanupParallelUnscheduled(work, stream, slots, group, preparedSlots);
                ReleaseAccessesIfRegistered(registration);
                if (state.Index != 0)
                    CancelUnscheduledState(state);
            }
            finally
            {
                submission.Rollback();
                if (ownsCategoryRequest)
                    options.Category!.CompleteRequest();
            }
            throw;
        }
        finally { submission.Release(); }
    }

    private JobPayloadLane BeginParallelSchedule<T>(JobScheduleOptions options, int batchSize)
        where T : struct, IJobParallelFor
    {
        if (batchSize <= 0 && batchSize != JobScheduleOptions.AutomaticBatchSize)
            throw new ArgumentOutOfRangeException(nameof(batchSize), InvalidBatchSizeMessage);

        ValidatePriority(options);
        EnsureCurrentScopeBelongsToThisRuntime();
        JobTraits.RequireSynchronousParallelJob<T>();
        JobPayloadLane lane = JobTraits.GetPayloadLane<T>();
        ApplyManagedPayloadPolicy<T>(lane);
        return lane;
    }

    private void ScheduleParallelWork<T>(
        in T job,
        JobHandle state,
        JobHandle dependency,
        ResourceAccessRegistration registration,
        ResourceAccessReservation reservation,
        JobScheduleOptions options,
        int length,
        int batchSize,
        int batchCount,
        int tokenCount,
        bool automaticBatch,
        bool hasScheduleDependencies,
        bool deferResourceActivation,
        ref WorkBatch work,
        ref int[]? slots,
        ref WorkStream<ScheduledParallelToken<T>>? stream,
        ref ParallelJobGroup<T>? group,
        ref int preparedSlots)
        where T : struct, IJobParallelFor
    {
        stream = GetWorkStream<ScheduledParallelToken<T>>(options.Category);
        group = ParallelJobGroup<T>.Rent(
            job,
            length,
            batchSize,
            batchCount,
            measureCost: automaticBatch);
        ScheduledParallelTokenSource<T> source = new(group, state);
        if (!hasScheduleDependencies)
        {
            try
            {
                _workQueue.EnqueueReadyMany(
                    stream,
                    tokenCount,
                    batchCount,
                    ref source,
                    options.Priority);
            }
            finally
            {
                group.ReleaseReference();
                group = null;
            }
            return;
        }

        slots = ArrayPool<int>.Shared.Rent(tokenCount);
        preparedSlots = stream.PrepareMany(slots.AsSpan(0, tokenCount), ref source);
        work = WorkBatch.CreateArray(
            stream,
            slots,
            preparedSlots,
            batchCount,
            pooledSlotsArray: true,
            options.Priority);
        slots = null;
        group.ReleaseReference();
        group = null;
        if (deferResourceActivation)
        {
            RegisterDeferredResourceScheduleDependency(
                state,
                dependency,
                reservation,
                work);
        }
        else if (RegisterScheduleDependencies(state, dependency, registration, work))
            _workQueue.Enqueue(work);
    }

    private static void CleanupParallelUnscheduled<T>(
        WorkBatch work,
        WorkStream<ScheduledParallelToken<T>>? stream,
        int[]? slots,
        ParallelJobGroup<T>? group,
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
        group?.ReleaseReference();
    }

}

public sealed partial class Scheduler
{
    public JobResource CreateResource(string? name = null)
    {
        using Submission submissionScope = EnterSubmission();
        EnsureCurrentScopeBelongsToThisRuntime();
        return _resources.CreateResource(name);
    }

    public JobResourceToken CreateResourceToken(string? name = null)
    {
        using Submission submissionScope = EnterSubmission();
        EnsureCurrentScopeBelongsToThisRuntime();
        return _resources.CreateToken(name);
    }

    public void ReleaseResource(JobResource resource)
    {
        _resources.Release(resource);
    }

    public void ReleaseResourceToken(JobResourceToken token)
    {
        _resources.Release(token);
        lock (_submissionObserverGate)
            _submissionObservers.Remove(
                new SubmissionResourceIdentity(ResourceKind.Token, token.Id, token.Version, token.Generation));
    }


    private static int CalculateParallelBatchCount(int length, int batchSize)
    {
        return ((length - 1) / batchSize) + 1;
    }

    private int CalculateParallelTokenCount(int batchCount)
    {
        return Math.Min(batchCount, Math.Max(1, _config.WorkerCount + 1));
    }

}



