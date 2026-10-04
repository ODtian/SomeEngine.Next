using SomeEngine.Job;
using SomeEngine.Core.Diagnostics;

namespace SomeEngine.Layout;

/// <summary>Runs detached element data through the same chunk callback in both execution modes.</summary>
public static class ChunkJobs
{
    /// <summary>Completes jobs over disjoint ranges of detached element data before returning.</summary>
    public static void Run<T>(T job, int count, bool parallel, int chunkSize = 32) where T : struct, IJobChunk
    {
        DevelopmentCheck.Require(count >= 0, "Chunk jobs require a nonnegative element count.");
        DevelopmentCheck.Require(chunkSize > 0, "Chunk jobs require a positive chunk size.");
        if (count == 0) return;
        var chunks = new Chunks(count, chunkSize);
        if (parallel && chunks.ChunkCount > 1)
            job.Schedule(chunks).Complete();
        else
            for (int index = 0; index < chunks.ChunkCount; index++) job.Execute(chunks.GetChunk(index));
    }

    private readonly struct Chunks(int count, int size) : IJobChunkSource
    {
        public int ChunkCount => count / size + (count % size == 0 ? 0 : 1);
        public JobChunk GetChunk(int index) => new(index, index * size, Math.Min(size, count - index * size));
    }
}
