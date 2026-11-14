using SomeEngine.Job;
using SomeEngine.Core.Diagnostics;

namespace SomeEngine.Layout;

/// <summary>Runs detached element data through the same per-index job in both execution modes.</summary>
public static class ChunkJobs
{
    /// <summary>Completes the job for every index before returning: on the current scheduler in batches when parallel, otherwise on the caller.</summary>
    public static void Run<T>(T job, int count, bool parallel, int batchSize = 32) where T : struct, IJobParallelFor
    {
        DevelopmentCheck.Require(count >= 0, "Chunk jobs require a nonnegative element count.");
        DevelopmentCheck.Require(batchSize > 0, "Chunk jobs require a positive batch size.");
        if (parallel && count > batchSize)
            JobSystem.Current.ExecuteParallel(job, count, batchSize);
        else
            for (int index = 0; index < count; index++) job.Execute(index);
    }
}
