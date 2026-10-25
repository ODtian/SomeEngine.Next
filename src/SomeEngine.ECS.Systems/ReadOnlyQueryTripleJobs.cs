using SomeEngine.ECS.Queries;
using SomeEngine.Job;

namespace SomeEngine.ECS.Systems;

/// <summary>Reads an access query and two participant queries for one synchronous output range.</summary>
public interface IReadOnlyQueryTripleJob
{
    void Execute(int index, scoped QueryCursor access, scoped QueryCursor first, scoped QueryCursor second);
}

public static class ReadOnlyQueryTripleJobs
{
    public static void ExecuteInline<TJob>(scoped QueryCursor access, scoped QueryCursor first,
        scoped QueryCursor second, in TJob job, int count)
        where TJob : struct, IReadOnlyQueryTripleJob
    {
        for (int index = 0; index < count; index++)
        {
            TJob local = job;
            local.Execute(index, access, first, second);
        }
    }

    /// <summary>Joins all ranges before the enclosing query callbacks return.</summary>
    public static void ExecuteParallel<TJob>(scoped QueryCursor access, scoped QueryCursor first,
        scoped QueryCursor second, in TJob job, int count, ReadOnlySpan<JobResourceAccess> accesses)
        where TJob : struct, IReadOnlyQueryTripleJob
    {
        if (access.State.Definition.CanWrite || first.State.Definition.CanWrite || second.State.Definition.CanWrite)
            throw new InvalidOperationException("Parallel query work requires read-only queries.");
        Scheduler scheduler = WorldStorageJobResources.RequireJobs(access.Owner).Scheduler;
        scheduler.ExecuteParallel(new TripleRanges<TJob>(access.Data, first.Data, second.Data, job),
            count, 1, accesses);
    }

    private readonly struct TripleRanges<TJob>(QueryData access, QueryData first, QueryData second, TJob job)
        : IJobParallelFor where TJob : struct, IReadOnlyQueryTripleJob
    {
        public void Execute(int index)
        {
            TJob local = job;
            local.Execute(index, new QueryCursor(in access), new QueryCursor(in first), new QueryCursor(in second));
        }
    }
}
