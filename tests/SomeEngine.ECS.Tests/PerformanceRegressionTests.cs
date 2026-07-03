using System.Diagnostics;
using System.Globalization;
using SomeEngine.ECS;
using SomeEngine.ECS.Registry;
using Xunit;

namespace SomeEngine.ECS.Tests;

[CollectionDefinition("Performance regression", DisableParallelization = true)]
public sealed class PerformanceRegressionCollection
{
    public const string CollectionName = "Performance regression";
}

[Collection(PerformanceRegressionCollection.CollectionName)]
public class PerformanceRegressionTests
{
    private const int BulkEntityCount = 100_000;
    private const int RelativeEntityCount = 30_000;
    private const double RequiredBatchSpeedupOverScalar = 2.0;
    private const string SpawnBatchBudgetVariable = "SOMEECS_PERF_GUARD_SPAWN_BATCH_100K_MS";

    private static readonly int[] OneComponentIds =
    [
        ComponentMetadata<Position>.Id,
    ];

    private static readonly int[] ThreeComponentIds =
    [
        ComponentMetadata<Position>.Id,
        ComponentMetadata<Velocity>.Id,
        ComponentMetadata<Health>.Id,
    ];

    [Fact]
    [Trait("Category", "Performance")]
    public void SpawnBatch_ReservedOneComponentHotPath_KeepsAllocationAndChunkShape()
    {
        PrimeSpawnBatchPools();

        var world = new World();
        using var journalSuppression = world.Journal.Suppress();
        world.ReserveBundle(OneComponentIds, BulkEntityCount);

        long before = GC.GetAllocatedBytesForCurrentThread();
        int chunkCount;
        using (var batch = world.SpawnBatch<Position>(BulkEntityCount))
        {
            chunkCount = batch.Chunks.Length;
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.Equal(BulkEntityCount, world.EntityCount);

        var archetype = Assert.Single(world.CreateQuery().With<Position>().Build().Archetypes);
        Assert.Equal(ExpectedChunkCount(archetype.MaxChunkRows, BulkEntityCount), chunkCount);
        Assert.Equal(chunkCount, archetype.Chunks.Count);
    }

    [Fact]
    [Trait("Category", "Performance")]
    public void SpawnBatch_ReservedThreeComponentHotPath_KeepsAllocationAndChunkShape()
    {
        PrimeSpawnBatchPools();

        var world = new World();
        using var journalSuppression = world.Journal.Suppress();
        world.ReserveBundle(ThreeComponentIds, BulkEntityCount);

        long before = GC.GetAllocatedBytesForCurrentThread();
        int chunkCount;
        using (var batch = world.SpawnBatch(ThreeComponentIds, BulkEntityCount))
        {
            chunkCount = batch.Chunks.Length;
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.Equal(BulkEntityCount, world.EntityCount);

        var archetype = Assert.Single(world.CreateQuery().With<Position>().With<Velocity>().With<Health>().Build().Archetypes);
        Assert.Equal(ExpectedChunkCount(archetype.MaxChunkRows, BulkEntityCount), chunkCount);
        Assert.Equal(chunkCount, archetype.Chunks.Count);
    }

    [Fact]
    [Trait("Category", "Performance")]
    public void SpawnBatch_WithColumnWrites_RemainsFasterThanScalarCreate()
    {
        PrimeSpawnBatchPools();

        long batchTicks = MeasureBestOf(3, () => SpawnBatchCreateAndWriteOnce(RelativeEntityCount));
        long scalarTicks = MeasureBestOf(3, () => ScalarCreateAndWriteOnce(RelativeEntityCount));

        Assert.True(
            batchTicks * RequiredBatchSpeedupOverScalar < scalarTicks,
            $"SpawnBatch should stay at least {RequiredBatchSpeedupOverScalar:0.#}x faster than scalar create. " +
            $"batch={FormatMilliseconds(batchTicks)}, scalar={FormatMilliseconds(scalarTicks)}.");
    }

    [Fact]
    [Trait("Category", "Performance")]
    public void SpawnBatch_ReservedOneComponent_RespectsOptInLocalBudget()
    {
        string? budgetText = Environment.GetEnvironmentVariable(SpawnBatchBudgetVariable);
        if (string.IsNullOrWhiteSpace(budgetText))
            return;

#if DEBUG
        return;
#else
        double budgetMilliseconds = double.Parse(budgetText, CultureInfo.InvariantCulture);
        long elapsedTicks = MeasureBestOf(5, () => SpawnBatchCreateOnlyOnce(BulkEntityCount));
        double elapsedMilliseconds = TicksToMilliseconds(elapsedTicks);

        Assert.True(
            elapsedMilliseconds <= budgetMilliseconds,
            $"SpawnBatch 100k create exceeded {SpawnBatchBudgetVariable}={budgetMilliseconds:0.###}ms. " +
            $"best={elapsedMilliseconds:0.###}ms.");
#endif
    }

    private static void PrimeSpawnBatchPools()
    {
        PrimeSpawnBatchPool(OneComponentIds, 1);
        PrimeSpawnBatchPositionPool(BulkEntityCount);
        PrimeSpawnBatchPool(ThreeComponentIds, BulkEntityCount);
    }

    private static void PrimeSpawnBatchPositionPool(int count)
    {
        var world = new World();
        using var journalSuppression = world.Journal.Suppress();
        world.ReserveBundle(OneComponentIds, count);

        using var batch = world.SpawnBatch<Position>(count);
    }

    private static void PrimeSpawnBatchPool(int[] componentIds, int count)
    {
        var world = new World();
        using var journalSuppression = world.Journal.Suppress();
        world.ReserveBundle(componentIds, count);

        using var batch = world.SpawnBatch(componentIds, count);
    }

    private static long MeasureBestOf(int iterations, Func<int> action)
    {
        long best = long.MaxValue;
        for (int i = 0; i < iterations; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            var stopwatch = Stopwatch.StartNew();
            int result = action();
            stopwatch.Stop();

            GC.KeepAlive(result);
            best = Math.Min(best, stopwatch.ElapsedTicks);
        }

        return best;
    }

    private static int SpawnBatchCreateOnlyOnce(int count)
    {
        var world = new World();
        using var journalSuppression = world.Journal.Suppress();
        world.ReserveBundle(OneComponentIds, count);

        using (var batch = world.SpawnBatch<Position>(count))
        {
            GC.KeepAlive(batch.Count);
        }

        return world.EntityCount;
    }

    private static int SpawnBatchCreateAndWriteOnce(int count)
    {
        var world = new World();
        using var journalSuppression = world.Journal.Suppress();
        world.ReserveBundle(OneComponentIds, count);

        int value = 0;
        using (var batch = world.SpawnBatch<Position>(count))
        {
            foreach (var chunk in batch.Chunks)
            {
                Span<Position> positions = chunk.Write<Position>();
                for (int i = 0; i < positions.Length; i++)
                    positions[i] = new Position { X = value++, Y = value };
            }
        }

        return world.EntityCount;
    }

    private static int ScalarCreateAndWriteOnce(int count)
    {
        var world = new World();
        using var journalSuppression = world.Journal.Suppress();
        world.ReserveBundle(OneComponentIds, count);

        for (int i = 0; i < count; i++)
            world.CreateEntity(new Position { X = i, Y = i + 1 });

        return world.EntityCount;
    }

    private static string FormatMilliseconds(long ticks)
    {
        return $"{TicksToMilliseconds(ticks):0.###}ms";
    }

    private static int ExpectedChunkCount(int rowsPerChunk, int rowCount)
    {
        return (rowCount + rowsPerChunk - 1) / rowsPerChunk;
    }

    private static double TicksToMilliseconds(long ticks)
    {
        return ticks * 1000.0 / Stopwatch.Frequency;
    }
}
