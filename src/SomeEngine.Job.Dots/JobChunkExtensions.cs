namespace SomeEngine.Job;

public static class JobChunkExtensions
{
    public static JobHandle Schedule<TJob, TSource>(
        this TJob job,
        in TSource source,
        int batchSize = 1,
        JobHandle dependency = default)
        where TJob : struct, IJobChunk
        where TSource : IJobChunkSource
    {
        var chunkCount = GetChunkCount(source);
        return JobSystem.ScheduleParallel(
            new ChunkJobParallelAdapter<TJob, TSource>(job, source),
            chunkCount,
            batchSize,
            dependency);
    }

    public static JobHandle Schedule<TJob, TSource>(
        this TJob job,
        in TSource source,
        int batchSize,
        JobResourceAccess access,
        JobHandle dependency = default)
        where TJob : struct, IJobChunk
        where TSource : IJobChunkSource
    {
        var chunkCount = GetChunkCount(source);
        return JobSystem.ScheduleParallel(
            new ChunkJobParallelAdapter<TJob, TSource>(job, source),
            chunkCount,
            batchSize,
            access,
            dependency);
    }

    public static JobHandle Schedule<TJob, TSource>(
        this TJob job,
        in TSource source,
        int batchSize,
        ReadOnlySpan<JobResourceAccess> accesses,
        JobHandle dependency = default)
        where TJob : struct, IJobChunk
        where TSource : IJobChunkSource
    {
        var chunkCount = GetChunkCount(source);
        return JobSystem.ScheduleParallel(
            new ChunkJobParallelAdapter<TJob, TSource>(job, source),
            chunkCount,
            batchSize,
            accesses,
            dependency);
    }

    private static int GetChunkCount<TSource>(in TSource source)
        where TSource : IJobChunkSource
    {
        var chunkCount = source.ChunkCount;
        if (chunkCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(source), "Chunk count must be non-negative.");
        }

        return chunkCount;
    }
}

