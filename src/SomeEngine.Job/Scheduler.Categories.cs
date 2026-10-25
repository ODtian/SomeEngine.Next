namespace SomeEngine.Job;

public sealed partial class Scheduler
{
    [ThreadStatic]
    private static JobCategory? s_currentCategory;

    public JobCategory CreateCategory(JobCategoryConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        using Submission submission = EnterSubmission();
        config.Validate();
        var category = new JobCategory(this, config);
        _workQueue.RegisterCategory(category);
        return category;
    }

    public ValueTask<JobHandle> ScheduleAsync<T>(
        in T job, JobScheduleOptions options, JobHandle dependency = default,
        CancellationToken cancellationToken = default)
        where T : struct, IJob =>
        ScheduleAsync(job, ReadOnlySpan<JobResourceAccess>.Empty, options, dependency, cancellationToken);

    public ValueTask<JobHandle> ScheduleAsync<T>(
        in T job, ReadOnlySpan<JobResourceAccess> accesses,
        JobScheduleOptions options, JobHandle dependency = default,
        CancellationToken cancellationToken = default)
        where T : struct, IJob
    {
        using Submission submissionScope = EnterSubmission();
        ValidateDependency(dependency);
        JobPayloadLane lane = BeginSchedule<T>(options);
        options = ResolveCategoryOptions(options);
        if (cancellationToken.IsCancellationRequested)
            return ValueTask.FromCanceled<JobHandle>(cancellationToken);
        JobCategory? category = options.Category;
        if (category is null || SharesCurrentCategoryRequest(category))
            return new(ScheduleCore(job, accesses, options, dependency, ignoreExplicitDependencyFault: false));

        JobSubmissionReservation submission = JobSubmissionTracker.Begin(this, accesses);
        JobHandle state = default;
        ResourceAccessReservation reservation = default;
        WorkBatch work = default;
        try
        {
            state = CreateState(pendingWork: 1, pendingDependencies: 0);
            reservation = ReserveAccesses<T>(accesses);
            AttachToCurrentScope(state);
            submission.Bind(state);
            WorkStream<ScheduledJob<T>> stream = GetWorkStream<ScheduledJob<T>>(category);
            int workIndex = stream.Prepare(new ScheduledJob<T>(job, state));
            work = WorkBatch.CreateSingle(stream, workIndex, options.Priority);
            var request = new CategoryWork(this, category, state, dependency, reservation, work, submission, cancellationToken);
            request.RegisterCancellation();
            category.QueueRequest(request);
            _counters.Scheduled(lane);
            return request.Result;
        }
        catch
        {
            CancelReservation(reservation);
            if (state.Index != 0)
                CleanupUnscheduled(state, ResourceAccessRegistration.Empty, work);
            submission.Rollback();
            submission.Release();
            throw;
        }
    }

    public ValueTask<JobHandle> ScheduleAsync<T>(in T job, JobResourceAccess access,
        JobScheduleOptions options, JobHandle dependency = default, CancellationToken cancellationToken = default)
        where T : struct, IJob => ScheduleAsync(job, [access], options, dependency, cancellationToken);

    private JobScheduleOptions ResolveCategoryOptions(JobScheduleOptions options)
    {
        JobCategory? category = options.Category;
        if (category is null && ReferenceEquals(s_executing, this) && JobExecutionContext.IsActive)
            category = s_currentCategory;
        if (category is not null && !ReferenceEquals(category.Scheduler, this))
            throw new ArgumentException("The work category must belong to this scheduler.", nameof(options));
        return options with { Category = category };
    }

    private bool SharesCurrentCategoryRequest(JobCategory category) =>
        ReferenceEquals(s_executing, this) && s_currentScope.Index != 0 &&
        ReferenceEquals(category, s_currentCategory);

    private bool StartCategoryRequest(JobCategory? category)
    {
        if (category is null || SharesCurrentCategoryRequest(category))
            return false;
        if (!category.TryStartRequest())
            throw new InvalidOperationException($"The in-flight limit for work category '{category.Name}' is reached.");
        return true;
    }

    private void BindCategoryRequest(JobHandle handle, JobCategory? category, bool ownsRequest)
    {
        if (ownsRequest)
            OnCompleted(handle, static (_, state) => ((JobCategory)state!).CompleteRequest(), category);
    }

    internal JobCategory? SetCurrentCategory(JobCategory? category)
    {
        JobCategory? previous = s_currentCategory;
        s_currentCategory = category;
        return previous;
    }

    internal void CategoryExecutionFinished() => _workQueue.CategoryExecutionFinished();

    private CategoryRequest EnterSynchronousCategoryRequest(JobCategory? category)
    {
        bool inherited = category is not null && ReferenceEquals(s_executing, this) &&
            JobExecutionContext.IsActive && ReferenceEquals(category, s_currentCategory);
        return new CategoryRequest(category, !inherited && StartCategoryRequest(category));
    }

    private CategoryExecution EnterCategoryExecution(JobCategory? category)
    {
        JobCategory? previous = s_currentCategory;
        bool acquire = category is not null && !(ReferenceEquals(s_executing, this) &&
            JobExecutionContext.IsActive && ReferenceEquals(previous, category));
        if (acquire)
        {
            while (true)
            {
                long wake = _workQueue.WakeVersion;
                if (category!.TryStartExecution())
                    break;
                if (!_workQueue.TryExecuteOne(wait: false))
                    _workQueue.WaitAndExecuteOne(wake);
            }
        }
        s_currentCategory = category;
        return new CategoryExecution(this, category, previous, acquire);
    }

    private readonly struct CategoryRequest(JobCategory? category, bool ownsRequest) : IDisposable
    {
        public void Dispose()
        {
            if (ownsRequest)
                category!.CompleteRequest();
        }
    }

    private readonly struct CategoryExecution(
        Scheduler scheduler, JobCategory? category, JobCategory? previous, bool acquired) : IDisposable
    {
        public void Dispose()
        {
            scheduler.SetCurrentCategory(previous);
            if (acquired)
                category!.CompleteExecution();
        }
    }

    private void StartCategoryWork(CategoryWork request)
    {
        try
        {
            RegisterDeferredResourceScheduleDependency(
                request.Handle, request.Dependency, request.Resources, request.Work);
            BindCategoryRequest(request.Handle, request.Category, ownsRequest: true);
            request.Admit();
        }
        catch (Exception exception)
        {
            CancelReservation(request.Resources);
            CleanupUnscheduled(request.Handle, ResourceAccessRegistration.Empty, request.Work);
            request.Submission.Rollback();
            request.Category.CompleteRequest();
            request.Fail(exception);
        }
        finally { request.Submission.Release(); }
    }

    private void CancelCategoryWork(CategoryWork request)
    {
        try
        {
            CancelReservation(request.Resources);
            CleanupUnscheduled(request.Handle, ResourceAccessRegistration.Empty, request.Work);
            request.Submission.Rollback();
        }
        finally { request.Submission.Release(); }
    }

    internal sealed class CategoryWork(
        Scheduler scheduler, JobCategory category, JobHandle handle, JobHandle dependency,
        ResourceAccessReservation resources, WorkBatch work, JobSubmissionReservation submission,
        CancellationToken cancellationToken)
    {
        private readonly TaskCompletionSource<JobHandle> _admitted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private CancellationTokenRegistration _cancellation;

        internal JobCategory Category { get; } = category;
        internal JobHandle Handle { get; } = handle;
        internal JobHandle Dependency { get; } = dependency;
        internal ResourceAccessReservation Resources { get; } = resources;
        internal WorkBatch Work { get; } = work;
        internal JobSubmissionReservation Submission { get; } = submission;
        internal CancellationToken CancellationToken { get; } = cancellationToken;
        internal LinkedListNode<CategoryWork>? WaitingNode { get; set; }
        internal ValueTask<JobHandle> Result => new(_admitted.Task);
        internal void RegisterCancellation() => _cancellation = CancellationToken.UnsafeRegister(
            static state => ((CategoryWork)state!).Cancel(), this);
        internal void Start()
        {
            _cancellation.Unregister();
            scheduler.StartCategoryWork(this);
        }
        private void Cancel()
        {
            if (Category.CancelRequest(this))
                CancelWaiting();
        }
        internal void CancelWaiting()
        {
            _cancellation.Unregister();
            scheduler.CancelCategoryWork(this);
            _admitted.TrySetCanceled(CancellationToken);
        }
        internal void Admit() => _admitted.TrySetResult(Handle);
        internal void Fail(Exception exception) => _admitted.TrySetException(exception);
    }
}
