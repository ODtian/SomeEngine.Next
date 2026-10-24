using System.Buffers;
using SomeEngine.ECS.Queries;
using SomeEngine.Job;

namespace SomeEngine.ECS.Systems;

/// <summary>Reads two callback-scoped queries while executing one output partition.</summary>
public interface IReadOnlyQueryPairJob
{
    void Execute(int index, scoped QueryCursor first, scoped QueryCursor second);
}

public static class ReadOnlyQueryPairJobs
{
    /// <summary>Runs partitions inside the two surrounding query callbacks.</summary>
    public static void ExecuteInline<TJob>(scoped QueryCursor first, scoped QueryCursor second, in TJob job, int count)
        where TJob : struct, IReadOnlyQueryPairJob
    {
        for (int index = 0; index < count; index++)
        {
            TJob local = job;
            local.Execute(index, first, second);
        }
    }

    /// <summary>
    /// Submits one work item per partition with its external accesses, and joins all work before the
    /// borrowed queries return. ECS reads remain held by the surrounding callbacks.
    /// </summary>
    public static JobHandle ExecuteParallel<TJob>(scoped QueryCursor first, scoped QueryCursor second, in TJob job, int count,
        ReadOnlySpan<JobResourceAccess> accesses, int accessesPerWork, JobHandle dependency = default)
        where TJob : struct, IReadOnlyQueryPairJob
    {
        if (count == 0)
        {
            dependency.Complete();
            return dependency;
        }
        if (first.State.Definition.CanWrite || second.State.Definition.CanWrite)
            throw new InvalidOperationException("Parallel query-pair work requires read-only queries.");
        Scheduler scheduler = WorldStorageJobResources.RequireJobs(first.Owner).Scheduler;
        JobHandle[] work = ArrayPool<JobHandle>.Shared.Rent(count);
        int submitted = 0;
        JobHandle completed = dependency;
        try
        {
            var data = new PairJobData<TJob>(first.Data, second.Data, job);
            for (int index = 0; index < count; index++)
            {
                var adapter = new PairJob<TJob>(index, data);
                work[submitted] = scheduler.Schedule(adapter,
                    accesses.Slice(index * accessesPerWork, accessesPerWork), dependency);
                submitted++;
            }
        }
        finally
        {
            try
            {
                completed = scheduler.CombineDependencies(work.AsSpan(0, submitted));
                completed.Complete();
            }
            finally
            {
                Array.Clear(work, 0, submitted);
                ArrayPool<JobHandle>.Shared.Return(work);
            }
        }
        return completed;
    }

    private sealed class PairJobData<TJob>(QueryData first, QueryData second, TJob job)
        where TJob : struct, IReadOnlyQueryPairJob
    {
        private readonly QueryData _first = first;
        private readonly QueryData _second = second;
        private readonly TJob _job = job;

        public void Execute(int index)
        {
            TJob local = _job;
            local.Execute(index, new QueryCursor(in _first), new QueryCursor(in _second));
        }
    }

    private readonly struct PairJob<TJob>(int index, PairJobData<TJob> data) : IJob
        where TJob : struct, IReadOnlyQueryPairJob
    {
        public void Execute() => data.Execute(index);
    }
}
