using System.Buffers;

namespace SomeEngine.Job;

public sealed partial class Scheduler
{
    /// <summary>
    /// Adds partitioned work to the current grant. The caller supplies the partition proof;
    /// resources remain held until the original body and every added work item finish.
    /// </summary>
    internal void ScheduleParallelInCurrentScope<T>(in T job, int length, JobScheduleOptions options)
        where T : struct, IJobParallelFor
    {
        if (length == 0)
            return;
        JobPayloadLane lane = BeginParallelSchedule<T>(options, batchSize: 1);
        options = ResolveCategoryOptions(options);
        JobHandle state = GetCurrentScope();
        _workQueue.EnsureItemCapacity(length);
        int tokenCount = CalculateParallelTokenCount(length);
        if (options.Category is { } category)
            tokenCount = Math.Min(tokenCount, category.Config.MaxExecuting);
        WorkStream<ScheduledParallelToken<T>> stream = GetWorkStream<ScheduledParallelToken<T>>(options.Category);
        ParallelJobGroup<T> group = ParallelJobGroup<T>.Rent(job, length, 1, length, measureCost: false);
        int[] workIndices = ArrayPool<int>.Shared.Rent(tokenCount);
        WorkBatch work = default;
        int storedWorkItems = 0;
        bool added = false;
        try
        {
            var source = new ScheduledParallelTokenSource<T>(group, state);
            storedWorkItems = stream.PrepareMany(workIndices.AsSpan(0, tokenCount), ref source);
            work = WorkBatch.CreateArray(stream, workIndices, storedWorkItems, length, pooledSlotsArray: true, options.Priority);
            _execution.AddWorkItems(state, length);
            added = true;
            _workQueue.Enqueue(work);
            _counters.Scheduled(lane);
        }
        catch
        {
            if (work.HasValue)
                work.ReleaseJobs();
            else
            {
                for (int i = 0; i < storedWorkItems; i++)
                    stream.Cancel(workIndices[i]);
                ArrayPool<int>.Shared.Return(workIndices);
            }
            if (added)
                CompleteStreamItems(state, length, itemFault: null);
            throw;
        }
        finally
        {
            group.ReleaseReference();
        }
    }
}
