using System.Diagnostics;

namespace SomeEngine.Job;

/// <summary>
/// Per-job-type automatic batch sizing. Explicit batch sizes never enter this path. A cold job
/// starts with four tiles per worker; warmed jobs target a bounded amount of callback time per
/// batch while retaining at least one tile per worker and at most the configured tile density.
/// </summary>
internal static class ParallelBatchSizer<T>
    where T : struct, IJobParallelFor
{
    private const long SampleScale = 1_000_000;
    private const int ColdTilesPerWorker = 4;
    private static long s_ewmaTicksPerItemScaled;

    internal static int Resolve(
        int length,
        int workerCount,
        int targetMicroseconds,
        int maxTilesPerWorker)
    {
        int workers = Math.Max(1, workerCount);
        int maxTiles = (int)Math.Max(
            1L,
            Math.Min((long)length, (long)workers * maxTilesPerWorker));
        int minBatchSize = DivideRoundUp(length, maxTiles);
        // Keep at least the cold-start tile density. Collapsing to one tile per worker lets an OS
        // preemption pin the final tile and produces frame-sized p99 spikes even when the average
        // cost estimate is accurate.
        int minimumTiles = Math.Max(
            1,
            Math.Min(length, workers * ColdTilesPerWorker));
        int maxBatchSize = DivideRoundUp(length, minimumTiles);

        long cost = Volatile.Read(ref s_ewmaTicksPerItemScaled);
        if (cost <= 0)
        {
            int coldTiles = Math.Max(1, Math.Min(length, workers * ColdTilesPerWorker));
            return DivideRoundUp(length, coldTiles);
        }

        long targetTicks = Math.Max(
            1,
            Stopwatch.Frequency * (long)targetMicroseconds / 1_000_000L);
        long candidate = targetTicks > long.MaxValue / SampleScale
            ? long.MaxValue
            : targetTicks * SampleScale / cost;
        return (int)Math.Clamp(candidate, minBatchSize, maxBatchSize);
    }

    internal static void Record(long elapsedTicks, int itemCount)
    {
        if (elapsedTicks <= 0 || itemCount <= 0)
            return;

        long sample = elapsedTicks > long.MaxValue / SampleScale
            ? long.MaxValue
            : Math.Max(1, elapsedTicks * SampleScale / itemCount);
        while (true)
        {
            long current = Volatile.Read(ref s_ewmaTicksPerItemScaled);
            long updated = current <= 0
                ? sample
                : current + ((sample - current) / 4);
            if (Interlocked.CompareExchange(
                    ref s_ewmaTicksPerItemScaled,
                    updated,
                    current) == current)
            {
                return;
            }
        }
    }

    private static int DivideRoundUp(int value, int divisor) =>
        ((value - 1) / divisor) + 1;
}
