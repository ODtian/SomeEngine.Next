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
    /// Submits one owner per partition with its external accesses, and joins all work before the
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
            QueryData firstRead = first.Data;
            QueryData secondRead = second.Data;
            for (int index = 0; index < count; index++)
            {
                var adapter = new PairJob<TJob>(index, firstRead, secondRead, job);
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

    private readonly struct PairJob<TJob>(int index, QueryData first, QueryData second, TJob job) : IJob
        where TJob : struct, IReadOnlyQueryPairJob
    {
        public void Execute()
        {
            TJob local = job;
            local.Execute(index, new QueryCursor(in first), new QueryCursor(in second));
        }
    }
}
