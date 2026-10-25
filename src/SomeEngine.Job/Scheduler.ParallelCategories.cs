using System.Buffers;

namespace SomeEngine.Job;

public sealed partial class Scheduler
{
    public ValueTask<JobHandle> ScheduleParallelAsync<T>(in T job, int length, int batchSize,
        JobScheduleOptions options, JobHandle dependency = default, CancellationToken cancellationToken = default)
        where T : struct, IJobParallelFor =>
        ScheduleParallelAsync(job, length, batchSize, ReadOnlySpan<JobResourceAccess>.Empty,
            options, dependency, cancellationToken);

    public ValueTask<JobHandle> ScheduleParallelAsync<T>(in T job, int length, int batchSize,
        JobResourceAccess access, JobScheduleOptions options, JobHandle dependency = default,
        CancellationToken cancellationToken = default) where T : struct, IJobParallelFor =>
        ScheduleParallelAsync(job, length, batchSize, [access], options, dependency, cancellationToken);

    public ValueTask<JobHandle> ScheduleParallelAsync<T>(in T job, int length, int batchSize,
        ReadOnlySpan<JobResourceAccess> accesses, JobScheduleOptions options,
        JobHandle dependency = default, CancellationToken cancellationToken = default)
        where T : struct, IJobParallelFor
    {
        using Submission submissionScope = EnterSubmission();
        ValidateDependency(dependency);
        JobPayloadLane lane = BeginParallelSchedule<T>(options, batchSize);
        options = ResolveCategoryOptions(options);
        if (cancellationToken.IsCancellationRequested)
            return ValueTask.FromCanceled<JobHandle>(cancellationToken);
        if (length <= 0)
            return new(dependency);
        JobCategory? category = options.Category;
        if (category is null || SharesCurrentCategoryRequest(category))
            return new(ScheduleParallel(job, length, batchSize, accesses, options, dependency));

        bool automaticBatch = batchSize == JobScheduleOptions.AutomaticBatchSize;
        int resolvedSize = automaticBatch
            ? ParallelBatchSizer<T>.Resolve(length, _config.WorkerCount,
                _config.AutoBatchTargetMicroseconds, _config.AutoBatchMaxTilesPerWorker)
            : batchSize;
        int batchCount = CalculateParallelBatchCount(length, resolvedSize);
        _workQueue.EnsureItemCapacity(batchCount);
        int tokenCount = Math.Min(CalculateParallelTokenCount(batchCount), category.Config.MaxExecuting);
        JobSubmissionReservation submission = JobSubmissionTracker.Begin(this, accesses);
        JobHandle state = default;
        ResourceAccessReservation reservation = default;
        WorkBatch work = default;
        int[]? workIndices = null;
        WorkStream<ScheduledParallelToken<T>>? stream = null;
        ParallelJobGroup<T>? group = null;
        int prepared = 0;
        try
        {
            state = CreateState(batchCount + tokenCount, pendingDependencies: 0);
            reservation = ReserveAccesses<T>(accesses);
            AttachToCurrentScope(state);
            submission.Bind(state);
            stream = GetWorkStream<ScheduledParallelToken<T>>(category);
            group = ParallelJobGroup<T>.Rent(job, length, resolvedSize, batchCount, automaticBatch);
            var source = new ScheduledParallelTokenSource<T>(group, state);
            workIndices = ArrayPool<int>.Shared.Rent(tokenCount);
            prepared = stream.PrepareMany(workIndices.AsSpan(0, tokenCount), ref source);
            work = WorkBatch.CreateArray(stream, workIndices, prepared, batchCount,
                pooledSlotsArray: true, options.Priority);
            workIndices = null;
            group.ReleaseReference();
            group = null;
            var request = new CategoryWork(this, category, state, dependency,
                reservation, work, submission, cancellationToken);
            request.RegisterCancellation();
            category.QueueRequest(request);
            _counters.Scheduled(lane);
            return request.Result;
        }
        catch
        {
            CancelReservation(reservation);
            CleanupParallelUnscheduled(work, stream, workIndices, group, prepared);
            if (state.Index != 0)
                CancelUnscheduledState(state);
            submission.Rollback();
            _resources.ReleaseReservationReference(reservation);
            submission.Release();
            throw;
        }
    }
}
