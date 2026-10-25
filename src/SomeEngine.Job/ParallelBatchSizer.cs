using System.Diagnostics;
using System.Numerics;

namespace SomeEngine.Job;

/// <summary>
/// Scheduler-local automatic costs by job type and power-of-two work size. Cold work uses up to
/// four tiles per worker within the configured density; learned work targets callback time while
/// keeping that density as its lower bound and the configured density as its upper bound.
/// </summary>
internal sealed class ParallelBatchSizer<T>
    where T : struct, IJobParallelFor
{
    private const long SampleScale = 1_000_000;
    private const int ColdTilesPerWorker = 4;
    private readonly long[] _scaledTicksPerItemBySize = new long[32];

    internal int Resolve(
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
        int minimumTiles = Math.Min(maxTiles, Math.Max(1,
            Math.Min(length, workers * ColdTilesPerWorker)));
        int maxBatchSize = DivideRoundUp(length, minimumTiles);

        long cost = Volatile.Read(ref _scaledTicksPerItemBySize[BitOperations.Log2((uint)length)]);
        if (cost <= 0)
        {
            return maxBatchSize;
        }

        long targetTicks = Math.Max(
            1,
            Stopwatch.Frequency * (long)targetMicroseconds / 1_000_000L);
        long candidate = targetTicks > long.MaxValue / SampleScale
            ? long.MaxValue
            : targetTicks * SampleScale / cost;
        return (int)Math.Clamp(candidate, minBatchSize, maxBatchSize);
    }

    internal void Record(int length, long elapsedTicks, int itemCount)
    {
        if (elapsedTicks <= 0 || itemCount <= 0)
            return;

        long sample = elapsedTicks > long.MaxValue / SampleScale
            ? long.MaxValue
            : Math.Max(1, elapsedTicks * SampleScale / itemCount);
        ref long estimate = ref _scaledTicksPerItemBySize[BitOperations.Log2((uint)length)];
        while (true)
        {
            long current = Volatile.Read(ref estimate);
            long updated = current <= 0
                ? sample
                : current + ((sample - current) / 4);
            if (Interlocked.CompareExchange(
                    ref estimate,
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
