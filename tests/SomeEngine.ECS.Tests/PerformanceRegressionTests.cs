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
    private const int RelativeEntityCount = 1_000;
    private const double RequiredBatchSpeedupOverScalar = 2.0;
    private const string BundleBatchBudgetVariable = "SOMEECS_PERF_GUARD_BUNDLE_BATCH_100K_MS";

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
    public void ExecuteBundleSpawnBatch_ReservedOneComponent_KeepsChunkShape()
    {
        var world = new World();
        world.ReserveBundle(OneComponentIds, BulkEntityCount);

        world.ExecuteBundleSpawnBatch(
            OneComponentIds,
            BulkEntityCount,
            static view =>
            {
                var position = new Position { X = view.Index, Y = view.Index + 1 };
                view.Write(in position);
            });

        Assert.Equal(BulkEntityCount, world.EntityCount);

        var archetype = Assert.Single(
            world.AllArchetypes.ToArray(),
            static candidate => candidate.HasComponent(ComponentMetadata<Position>.Id));
        Assert.Equal(ExpectedChunkCount(archetype.MaxChunkRows, BulkEntityCount), archetype.Chunks.Length);
    }

    [Fact]
    [Trait("Category", "Performance")]
    public void ExecuteBundleSpawnBatch_ReservedThreeComponents_KeepsChunkShape()
    {
        var world = new World();
        world.ReserveBundle(ThreeComponentIds, BulkEntityCount);

        world.ExecuteBundleSpawnBatch(
            ThreeComponentIds,
            BulkEntityCount,
            static view =>
            {
                int index = view.Index;
                var position = new Position { X = index, Y = index + 1 };
                var velocity = new Velocity { X = index + 2, Y = index + 3 };
                var health = new Health { Value = index + 4 };
                view.Write(in position);
                view.Write(in velocity);
                view.Write(in health);
            });

        Assert.Equal(BulkEntityCount, world.EntityCount);

        var archetype = Assert.Single(
            world.AllArchetypes.ToArray(),
            static candidate =>
                candidate.HasComponent(ComponentMetadata<Position>.Id) &&
                candidate.HasComponent(ComponentMetadata<Velocity>.Id) &&
                candidate.HasComponent(ComponentMetadata<Health>.Id));
        Assert.Equal(ExpectedChunkCount(archetype.MaxChunkRows, BulkEntityCount), archetype.Chunks.Length);
    }

    [Fact]
    [Trait("Category", "Performance")]
    public void ExecuteBundleSpawnBatch_AmortizesStructuralPublicationAgainstScalarCreate()
    {
        PrimeBundleRuntime();
        AssertBatchAndScalarTransactionsWriteEquivalentRows(count: 32);

        long batchTicks = MeasureBestOf(3, () => ExecuteBundleBatchCreateOnce(RelativeEntityCount));
        long scalarTicks = MeasureBestOf(3, () => ExecuteBundleScalarCreateOnce(RelativeEntityCount));

        Assert.True(
            batchTicks * RequiredBatchSpeedupOverScalar < scalarTicks,
            $"ExecuteBundleSpawnBatch should stay at least {RequiredBatchSpeedupOverScalar:0.#}x faster than transactional scalar bundle create. " +
            $"batch={FormatMilliseconds(batchTicks)}, scalar={FormatMilliseconds(scalarTicks)}.");
    }

    [Fact]
    [Trait("Category", "Performance")]
    public void ExecuteBundleSpawnBatch_ReservedOneComponent_RespectsOptInLocalBudget()
    {
        string? budgetText = Environment.GetEnvironmentVariable(BundleBatchBudgetVariable);
        if (string.IsNullOrWhiteSpace(budgetText))
            return;

#if DEBUG
        return;
#else
        double budgetMilliseconds = double.Parse(budgetText, CultureInfo.InvariantCulture);
        long elapsedTicks = MeasureBestOf(5, () => ExecuteBundleBatchCreateOnce(BulkEntityCount));
        double elapsedMilliseconds = TicksToMilliseconds(elapsedTicks);

        Assert.True(
            elapsedMilliseconds <= budgetMilliseconds,
            $"ExecuteBundleSpawnBatch 100k create exceeded {BundleBatchBudgetVariable}={budgetMilliseconds:0.###}ms. " +
            $"best={elapsedMilliseconds:0.###}ms.");
#endif
    }

    private static void PrimeBundleRuntime()
    {
        var world = new World();
        world.ExecuteBundleSpawnBatch(
            OneComponentIds,
            1,
            static view =>
            {
                var position = default(Position);
                view.Write(in position);
            });
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

    private static int ExecuteBundleBatchCreateOnce(int count)
    {
        var world = new World();
        world.ReserveBundle(OneComponentIds, count);

        int value = 0;
        world.ExecuteBundleSpawnBatch(
            OneComponentIds,
            count,
            ref value,
            static (BundleWriteView view, ref int nextValue) =>
            {
                var position = new Position { X = nextValue++, Y = nextValue };
                view.Write(in position);
            });

        return world.EntityCount;
    }

    private static int ExecuteBundleScalarCreateOnce(int count)
    {
        var world = new World();
        world.ReserveBundle(OneComponentIds, count);

        int value = 0;
        for (int index = 0; index < count; index++)
        {
            world.ExecuteBundleSpawn(
                OneComponentIds,
                ref value,
                static (BundleWriteView view, ref int nextValue) =>
                {
                    var position = new Position { X = nextValue++, Y = nextValue };
                    view.Write(in position);
                });
        }

        return world.EntityCount;
    }

    private static void AssertBatchAndScalarTransactionsWriteEquivalentRows(int count)
    {
        var batch = new World();
        var scalar = new World();
        batch.ReserveBundle(OneComponentIds, count);
        scalar.ReserveBundle(OneComponentIds, count);

        int batchCallbackCount = 0;
        batch.ExecuteBundleSpawnBatch(
            OneComponentIds,
            count,
            ref batchCallbackCount,
            static (BundleWriteView view, ref int nextValue) =>
            {
                var position = new Position { X = nextValue++, Y = nextValue };
                view.Write(in position);
            });

        int scalarCallbackCount = 0;
        for (int index = 0; index < count; index++)
        {
            scalar.ExecuteBundleSpawn(
                OneComponentIds,
                ref scalarCallbackCount,
                static (BundleWriteView view, ref int nextValue) =>
                {
                    var position = new Position { X = nextValue++, Y = nextValue };
                    view.Write(in position);
                });
        }

        Assert.Equal(count, batchCallbackCount);
        Assert.Equal(count, scalarCallbackCount);
        Assert.Equal(count, batch.EntityCount);
        Assert.Equal(count, scalar.EntityCount);
        Assert.Equal(1, batch.PublishedStructureEpoch);
        Assert.Equal(count, scalar.PublishedStructureEpoch);
        Assert.Equal(ReadPositions(batch), ReadPositions(scalar));
    }

    private static Position[] ReadPositions(World world)
    {
        var values = new List<Position>();
        var query = world.Query(world.QueryDefinition().Read<Position>());
        world.ExecuteQuery(query, cursor =>
        {
            foreach (var row in cursor.Rows)
                values.Add(row.Read<Position>());
        });
        values.Sort(static (left, right) => left.X.CompareTo(right.X));
        return values.ToArray();
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
