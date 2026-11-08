using System.Buffers;
using System.Diagnostics;
using System.Runtime.ExceptionServices;

namespace SomeEngine.Job;

public sealed partial class Scheduler
{
    [ThreadStatic] private static int s_synchronousParallelDepth;

    /// <summary>Executes index ranges on this scheduler and the calling thread, returning after every participant exits.</summary>
    public void ExecuteParallel<T>(in T job, int length, int batchSize = 1,
        JobScheduleOptions options = default) where T : struct, IJobParallelFor =>
        ExecuteParallel(job, length, batchSize, ReadOnlySpan<JobResourceAccess>.Empty, options);

    /// <summary>Holds the declared accesses through synchronous execution. Nested calls share the current scope's accesses.</summary>
    public void ExecuteParallel<T>(in T job, int length, int batchSize,
        ReadOnlySpan<JobResourceAccess> accesses, JobScheduleOptions options = default)
        where T : struct, IJobParallelFor
    {
        using Submission submission = EnterSubmission();
        JobPayloadLane lane = BeginParallelSchedule<T>(options, batchSize);
        if (length <= 0) return;
        options = ResolveCategoryOptions(options);
        using CategoryRequest category = EnterSynchronousCategoryRequest(options.Category);
        ParallelBatchSizer<T>? batchSizer = batchSize == JobScheduleOptions.AutomaticBatchSize
            ? GetParallelBatchSizer<T>() : null;
        int resolvedSize = batchSizer is not null
            ? batchSizer.Resolve(length, _config.WorkerCount, _config.AutoBatchTargetMicroseconds, _config.AutoBatchMaxTilesPerWorker)
            : batchSize;
        int count = CalculateParallelBatchCount(length, resolvedSize);
        SynchronousResourceAccess resources = default;
        JobHandle access = GetCurrentScope();
        if (JobThreads.IsActive)
        {
            foreach (JobResourceAccess declared in accesses) RequireCurrentAccess(declared, requireSingleWorkItem: false);
        }
        else
        {
            resources = accesses.IsEmpty ? default : AcquireSynchronousAccess(accesses);
            access = resources.Handle;
        }
        ParallelRange[] ranges = ArrayPool<ParallelRange>.Shared.Rent(count);
        ExceptionDispatchInfo? failure = null;
        try
        {
            for (int index = 0; index < count; index++)
            {
                int start = index * resolvedSize;
                ranges[index] = new(start, (int)Math.Min((long)start + resolvedSize, length));
            }
            ExecuteRangesCore(ranges, count, new ParallelRangeWork<T>(job, length, batchSizer), access, options, lane);
        }
        catch (Exception exception)
        {
            failure = ExceptionDispatchInfo.Capture(exception);
            if (resources.Handle.Index != 0) RecordFault(resources.Handle, failure);
        }
        finally
        {
            ArrayPool<ParallelRange>.Shared.Return(ranges);
            resources.Dispose();
        }
        if (resources.Handle.Index != 0)
        {
            try { Complete(resources.Handle); }
            catch (Exception exception) { failure ??= ExceptionDispatchInfo.Capture(exception); }
        }
        failure?.Throw();
    }

    private readonly record struct ParallelRange(int Start, int End);

    private readonly record struct ParallelRangeWork<T>(T Job, int Length, ParallelBatchSizer<T>? BatchSizer)
        : IRangeWork<ParallelRange> where T : struct, IJobParallelFor
    {
        public void Execute(in ParallelRange range)
        {
            T job = Job;
            long started = BatchSizer is not null ? Stopwatch.GetTimestamp() : 0;
            try
            {
                for (int index = range.Start; index < range.End; index++) job.Execute(index);
            }
            finally
            {
                BatchSizer?.Record(Length, Stopwatch.GetTimestamp() - started, range.End - range.Start);
            }
        }
    }
}
