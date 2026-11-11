using System.Diagnostics;
using System.Globalization;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Registry;

namespace SomeEngine.ECS.Benchmarks;

internal static partial class EcsBenchmarkSuite
{
    internal const int ReportSchemaVersion = 7;
    private const ulong FnvOffsetBasis = 14695981039346656037UL;
    private const ulong FnvPrime = 1099511628211UL;

    private static readonly int[] PositionComponents =
    [
        ComponentId<Position>.Value,
    ];

    internal static EcsBenchmarkReport Run(BenchmarkOptions options)
    {
        DateTimeOffset startedUtc = DateTimeOffset.UtcNow;
        long suiteStarted = Stopwatch.GetTimestamp();
        EcsBenchmarkEnvironment environment = CreateEnvironment();
        EcsBenchmarkSourceRevision sourceRevision = CreateSourceRevision();
        PrimeQueries();

        var results = new List<EcsBenchmarkResult>();
        for (int entityIndex = 0; entityIndex < options.EntityCounts.Length; entityIndex++)
        {
            int entityCount = options.EntityCounts[entityIndex];
            foreach (ScenarioDefinition scenario in CreateScenarios(
                         options,
                         entityCount,
                         includeDurablePersistence: entityIndex == 0))
            {
                Console.Error.WriteLine(
                    $"Running {scenario.Name}: {options.WarmupSamples} warm-up + " +
                    $"{options.Samples} measured fresh samples...");
                results.Add(MeasureScenario(scenario, options.WarmupSamples, options.Samples));
            }
        }

        DateTimeOffset completedUtc = DateTimeOffset.UtcNow;
        return new EcsBenchmarkReport(
            SchemaVersion: ReportSchemaVersion,
            StartedUtc: startedUtc,
            CompletedUtc: completedUtc,
            DurationMilliseconds: Stopwatch.GetElapsedTime(suiteStarted).TotalMilliseconds,
            Environment: environment,
            SourceRevision: sourceRevision,
            Configuration: new EcsBenchmarkConfiguration(
                options.ProfileName,
                options.EntityCounts,
                options.WarmupSamples,
                options.Samples,
                FreshWorldPerSample: true,
                options.QueryIterations,
                options.StructuralIterations,
                PercentileMethod: "R-7 linear interpolation over fresh samples",
                AllocatedBytesMetric: "current managed thread",
                TotalAllocatedBytesMetric: "all managed threads"),
            Results: results.ToArray());
    }

    private static EcsBenchmarkEnvironment CreateEnvironment() => new(
        MachineName: System.Environment.MachineName,
        Framework: RuntimeInformation.FrameworkDescription,
        OperatingSystem: RuntimeInformation.OSDescription,
        ProcessArchitecture: RuntimeInformation.ProcessArchitecture.ToString(),
        OperatingSystemArchitecture: RuntimeInformation.OSArchitecture.ToString(),
        ProcessorCount: Environment.ProcessorCount,
        TotalAvailableMemoryBytes: GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
        ServerGarbageCollection: GCSettings.IsServerGC,
        GarbageCollectorLatencyMode: GCSettings.LatencyMode.ToString(),
#if DEBUG
        BuildConfiguration: "Debug");
#else
        BuildConfiguration: "Release");
#endif

    private static EcsBenchmarkSourceRevision CreateSourceRevision()
    {
        string? commitSha = TryRunGit("rev-parse", "--verify", "HEAD");
        string? status = TryRunGit("status", "--porcelain=v1", "--untracked-files=all");
        string normalizedSha = commitSha?.Trim().ToLowerInvariant() ?? string.Empty;
        return new EcsBenchmarkSourceRevision(
            normalizedSha,
            status is not null && status.Length == 0);
    }

    private static string? TryRunGit(params string[] arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo("git")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Environment.CurrentDirectory,
            };
            foreach (string argument in arguments)
                startInfo.ArgumentList.Add(argument);

            using Process? process = Process.Start(startInfo);
            if (process is null)
                return null;
            string output = process.StandardOutput.ReadToEnd();
            _ = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(10_000) || process.ExitCode != 0)
                return null;
            return output.Trim();
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static ScenarioDefinition[] CreateScenarios(
        BenchmarkOptions options,
        int entityCount,
        bool includeDurablePersistence)
    {
        string countLabel = FormatCount(entityCount);
        int topologyCount = Math.Min(entityCount, 4_096);
        int churnWidth = Math.Min(entityCount, 256);
        var scenarios = new List<ScenarioDefinition>
        {
            new ScenarioDefinition(
                $"bundle-spawn-{countLabel}",
                entityCount,
                OperationsPerSample: entityCount,
                () => new SpawnExecution(entityCount)),
            new ScenarioDefinition(
                $"read-query-{countLabel}-x{options.QueryIterations}",
                entityCount,
                OperationsPerSample: options.QueryIterations,
                () => new QueryExecution(entityCount, options.QueryIterations)),
            new ScenarioDefinition(
                $"structural-candidate-{countLabel}-x{options.StructuralIterations}",
                entityCount,
                OperationsPerSample: options.StructuralIterations,
                () => new StructuralExecution(entityCount, options.StructuralIterations)),
            new ScenarioDefinition(
                $"parallel-integrate-{countLabel}",
                entityCount,
                OperationsPerSample: entityCount,
                () => new ParallelIntegrateExecution(entityCount)),
            new ScenarioDefinition(
                $"changed-enabled-filter-{countLabel}-x{options.QueryIterations}",
                entityCount,
                OperationsPerSample: options.QueryIterations,
                () => new ChangedEnabledExecution(entityCount, options.QueryIterations)),
            new ScenarioDefinition(
                $"storage-owners-{countLabel}",
                entityCount,
                OperationsPerSample: entityCount,
                () => new StorageCollectionsExecution(entityCount)),
            new ScenarioDefinition(
                $"relation-maintenance-{countLabel}-fanout{topologyCount}",
                entityCount,
                OperationsPerSample: Math.Max(0, topologyCount - 1),
                () => new RelationMaintenanceExecution(topologyCount)),
            new ScenarioDefinition(
                $"hierarchy-maintenance-{countLabel}-depth{topologyCount}",
                entityCount,
                OperationsPerSample: Math.Max(0, topologyCount - 1),
                () => new HierarchyMaintenanceExecution(topologyCount)),
            new ScenarioDefinition(
                $"command-buffer-churn-{countLabel}-w{churnWidth}-x{options.StructuralIterations}",
                entityCount,
                OperationsPerSample: checked(churnWidth * options.StructuralIterations),
                () => new CommandBufferChurnExecution(
                    entityCount,
                    churnWidth,
                    options.StructuralIterations)),
            new ScenarioDefinition(
                $"snapshot-write-{countLabel}",
                entityCount,
                OperationsPerSample: entityCount,
                () => new CheckpointWriteExecution(entityCount)),
            new ScenarioDefinition(
                $"snapshot-read-{countLabel}",
                entityCount,
                OperationsPerSample: entityCount,
                () => new CheckpointReadExecution(entityCount)),
            new ScenarioDefinition(
                $"mixed-frame-update-snapshot-load-{countLabel}",
                entityCount,
                OperationsPerSample: checked(entityCount * 3),
                () => new MixedRenderGraphFrameState(entityCount)),
        };
        if (includeDurablePersistence)
        {
            scenarios.Add(
                new ScenarioDefinition(
                    $"durable-save-roundtrip-{countLabel}",
                    entityCount,
                    OperationsPerSample: checked(entityCount * 2),
                    () => new DurableSaveRoundTripExecution(entityCount)));
        }
        return scenarios.ToArray();
    }


    private static EcsBenchmarkResult MeasureScenario(
        ScenarioDefinition scenario,
        int warmupSamples,
        int measuredSamples)
    {
        for (int index = 0; index < warmupSamples; index++)
        {
            IBenchmarkExecution warmup = scenario.CreateExecution();
            try
            {
                warmup.Execute();
                _ = warmup.ValidateAndGetChecksum();
                GC.KeepAlive(warmup);
            }
            finally
            {
                (warmup as IDisposable)?.Dispose();
            }
        }

        var samples = new EcsBenchmarkSample[measuredSamples];
        string? expectedChecksum = null;
        for (int index = 0; index < measuredSamples; index++)
        {
            IBenchmarkExecution execution = scenario.CreateExecution();
            try
            {
                // Execution construction is setup, not workload. Settle its temporary objects so
                // setup garbage cannot trigger a collection inside the measured operation.
                CollectGarbage();
                samples[index] = MeasureSample(index + 1, execution);
                expectedChecksum ??= samples[index].Checksum;
                if (!string.Equals(expectedChecksum, samples[index].Checksum, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Scenario '{scenario.Name}' produced non-deterministic checksums: " +
                        $"expected {expectedChecksum}, sample {index + 1} produced {samples[index].Checksum}.");
                }
                GC.KeepAlive(execution);
            }
            finally
            {
                (execution as IDisposable)?.Dispose();
            }
        }

        return new EcsBenchmarkResult(
            scenario.Name,
            scenario.EntityCount,
            scenario.OperationsPerSample,
            measuredSamples,
            warmupSamples,
            FreshWorldPerSample: true,
            MetricDistribution.From(samples, static sample => sample.ElapsedMilliseconds),
            MetricDistribution.From(samples, static sample => sample.AllocatedBytes),
            MetricDistribution.From(samples, static sample => sample.TotalAllocatedBytes),
            MetricDistribution.From(samples, static sample => sample.WorkingSetAfterBytes),
            MetricDistribution.From(samples, static sample => sample.WorkingSetDeltaBytes),
            AggregateGarbageCollections(samples),
            BenchmarkWorkloadMetricAggregate.From(samples),
            expectedChecksum ?? throw new InvalidOperationException("A measured scenario had no samples."),
            samples);
    }

    private static EcsBenchmarkSample MeasureSample(int sample, IBenchmarkExecution execution)
    {
        long workingSetBefore = Environment.WorkingSet;
        long managedMemoryBefore = GC.GetTotalMemory(forceFullCollection: false);
        var collectionsBefore = new GarbageCollectionCounts(
            GC.CollectionCount(0),
            GC.CollectionCount(1),
            GC.CollectionCount(2));
        long totalAllocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();

        long started = Stopwatch.GetTimestamp();
        execution.Execute();
        TimeSpan elapsed = Stopwatch.GetElapsedTime(started);

        long allocatedAfter = GC.GetAllocatedBytesForCurrentThread();
        long totalAllocatedAfter = GC.GetTotalAllocatedBytes(precise: true);
        var collectionsAfter = new GarbageCollectionCounts(
            GC.CollectionCount(0),
            GC.CollectionCount(1),
            GC.CollectionCount(2));
        long managedMemoryAfter = GC.GetTotalMemory(forceFullCollection: false);
        long workingSetAfter = Environment.WorkingSet;
        BenchmarkWorkloadMetricSample workloadMetrics = execution.WorkloadMetrics;
        string checksum = execution.ValidateAndGetChecksum();

        return new EcsBenchmarkSample(
            sample,
            elapsed.TotalMilliseconds,
            checked(allocatedAfter - allocatedBefore),
            checked(totalAllocatedAfter - totalAllocatedBefore),
            GarbageCollectionCounts.Subtract(collectionsAfter, collectionsBefore),
            managedMemoryBefore,
            managedMemoryAfter,
            workingSetBefore,
            workingSetAfter,
            workingSetAfter - workingSetBefore,
            workloadMetrics,
            checksum);
    }

    private static GarbageCollectionCounts AggregateGarbageCollections(
        EcsBenchmarkSample[] samples)
    {
        int generation0 = 0;
        int generation1 = 0;
        int generation2 = 0;
        foreach (EcsBenchmarkSample sample in samples)
        {
            generation0 = checked(generation0 + sample.GarbageCollections.Generation0);
            generation1 = checked(generation1 + sample.GarbageCollections.Generation1);
            generation2 = checked(generation2 + sample.GarbageCollections.Generation2);
        }
        return new GarbageCollectionCounts(generation0, generation1, generation2);
    }

    private static void CollectGarbage()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static void PrimeQueries()
    {
        var world = new World();
        world.ExecuteBundleSpawn(PositionComponents, 0, new DefaultPositionWriter());
        Query query = world.Query(new QueryDescription().WithStored<Position>());
        int count = 0;
        using (QueryScope source = query.OpenRead())
            foreach (QueryChunk chunk in source)
            {
                for (int row = 0; row < chunk.Count; row++)
                {
                    count++;
                }
            }
        GC.KeepAlive(count);
    }

    private static World CreatePopulatedWorld(int entityCount)
    {
        var world = new World(entityCount);
        Populate(world, entityCount);
        return world;
    }

    private static void Populate(World world, int count)
    {
        var state = new PopulateState(world, count);
        state.Execute();
    }

    private static WorldChecksum ValidateWorld(
        World world,
        int initialEntityCount,
        int addedEntityCount)
    {
        var state = new WorldChecksumState(FnvOffsetBasis);
        Query query = world.Query(new QueryDescription().WithStored<Position>());
        using (QueryScope source = query.OpenRead())
            foreach (QueryChunk chunk in source)
            {
                ReadOnlySpan<Position> valuesPosition = chunk.Read<Position>();
                for (int row = 0; row < chunk.Count; row++)
                {
                    Position position = valuesPosition[row];
                    state.Count++;
                    state.SumX += position.X;
                    state.SumY += position.Y;
                    state.Hash = Mix(state.Hash, unchecked((uint)position.X));
                    state.Hash = Mix(state.Hash, unchecked((uint)position.Y));
                }
            }

        int expectedCount = checked(initialEntityCount + addedEntityCount);
        long initialSumX = (long)initialEntityCount * (initialEntityCount - 1) / 2;
        long initialSumY = (long)initialEntityCount * ((long)initialEntityCount + 1) / 2;
        long addedSumX =
            (long)addedEntityCount * initialEntityCount +
            (long)addedEntityCount * (addedEntityCount - 1) / 2;
        long expectedSumX = checked(initialSumX + addedSumX);
        long expectedSumY = checked(initialSumY - addedSumX);
        if (world.EntityCount != expectedCount ||
            state.Count != expectedCount ||
            state.SumX != expectedSumX ||
            state.SumY != expectedSumY)
        {
            throw new InvalidOperationException(
                "Benchmark correctness validation failed: " +
                $"entities={world.EntityCount}/{expectedCount}, rows={state.Count}/{expectedCount}, " +
                $"sumX={state.SumX}/{expectedSumX}, sumY={state.SumY}/{expectedSumY}.");
        }

        ulong hash = Mix(state.Hash, unchecked((ulong)state.Count));
        hash = Mix(hash, unchecked((ulong)state.SumX));
        hash = Mix(hash, unchecked((ulong)state.SumY));
        return new WorldChecksum(state.Count, state.SumX, state.SumY, hash);
    }

    private static ulong Mix(ulong hash, ulong value)
    {
        for (int shift = 0; shift < 64; shift += 8)
        {
            hash ^= (byte)(value >> shift);
            hash *= FnvPrime;
        }
        return hash;
    }

    private static string FormatChecksum(WorldChecksum checksum, long timedChecksum = 0)
    {
        ulong hash = Mix(checksum.Hash, unchecked((ulong)timedChecksum));
        return hash.ToString("X16", CultureInfo.InvariantCulture);
    }

    private static string FormatCount(int count)
    {
        if (count % 1_000_000 == 0)
            return $"{count / 1_000_000}m";
        if (count % 1_000 == 0)
            return $"{count / 1_000}k";
        return count.ToString(CultureInfo.InvariantCulture);
    }

    private interface IBenchmarkExecution
    {
        World World { get; }

        BenchmarkWorkloadMetricSample WorkloadMetrics => BenchmarkWorkloadMetricSample.Empty;

        void Execute();

        string ValidateAndGetChecksum();
    }

    private sealed class SpawnExecution : IBenchmarkExecution
    {
        private readonly int _entityCount;

        internal SpawnExecution(int entityCount)
        {
            _entityCount = entityCount;
            World = new World(entityCount);
        }

        public World World { get; }

        public void Execute() => Populate(World, _entityCount);

        public string ValidateAndGetChecksum() =>
            FormatChecksum(ValidateWorld(World, _entityCount, addedEntityCount: 0));
    }

    private sealed class QueryExecution : IBenchmarkExecution
    {
        private readonly int _entityCount;
        private readonly int _queryIterations;
        private readonly Query _query;
        private long _timedChecksum;

        internal QueryExecution(int entityCount, int queryIterations)
        {
            _entityCount = entityCount;
            _queryIterations = queryIterations;
            World = CreatePopulatedWorld(entityCount);
            _query = World.Query(new QueryDescription().WithStored<Position>());
        }

        public World World { get; }

        public void Execute()
        {
            long checksum = 0;
            for (int iteration = 0; iteration < _queryIterations; iteration++)
            {
                using (QueryScope source = _query.OpenRead())
            foreach (QueryChunk chunk in source)
            {
                ReadOnlySpan<Position> valuesPosition = chunk.Read<Position>();
                for (int row = 0; row < chunk.Count; row++)
                {
                    Position position = valuesPosition[row];
                    checksum += position.X;
                    checksum += position.Y;
                }
            }
            }
            _timedChecksum = checksum;
        }

        public string ValidateAndGetChecksum()
        {
            long expectedTimedChecksum = checked(
                (long)_entityCount * _entityCount * _queryIterations);
            if (_timedChecksum != expectedTimedChecksum)
            {
                throw new InvalidOperationException(
                    $"Query benchmark checksum {_timedChecksum} did not match {expectedTimedChecksum}.");
            }
            return FormatChecksum(
                ValidateWorld(World, _entityCount, addedEntityCount: 0),
                _timedChecksum);
        }
    }

    private sealed class StructuralExecution : IBenchmarkExecution
    {
        private readonly int _entityCount;
        private readonly int _structuralIterations;

        internal StructuralExecution(int entityCount, int structuralIterations)
        {
            _entityCount = entityCount;
            _structuralIterations = structuralIterations;
            World = CreatePopulatedWorld(entityCount);
            _ = World.Query(new QueryDescription().WithStored<Position>());
        }

        public World World { get; }

        public void Execute()
        {
            for (int iteration = 0; iteration < _structuralIterations; iteration++)
            {
                int value = _entityCount + iteration;
                World.ExecuteBundleSpawn(PositionComponents, value, new AddedPositionWriter());
            }
        }

        public string ValidateAndGetChecksum()
        {
            return FormatChecksum(
                ValidateWorld(World, _entityCount, _structuralIterations));
        }
    }

    private readonly struct PopulateState
    {
        private readonly World _world;
        private readonly int _count;

        internal PopulateState(World world, int count)
        {
            _world = world;
            _count = count;
        }

        internal void Execute()
        {
            _world.ReserveBundle(PositionComponents, _count);
            _world.ExecuteBundleSpawnBatch(PositionComponents, _count, 0, new SequentialPositionWriter());
        }
    }

    private struct WorldChecksumState
    {
        internal WorldChecksumState(ulong hash)
        {
            Hash = hash;
        }

        internal int Count;
        internal long SumX;
        internal long SumY;
        internal ulong Hash;
    }

    private readonly record struct WorldChecksum(int Count, long SumX, long SumY, ulong Hash);

    private sealed record ScenarioDefinition(
        string Name,
        int EntityCount,
        int OperationsPerSample,
        Func<IBenchmarkExecution> CreateExecution);

    private readonly record struct Position(int X, int Y) : IComponent;
}

internal sealed record EcsBenchmarkReport(
    int SchemaVersion,
    DateTimeOffset StartedUtc,
    DateTimeOffset CompletedUtc,
    double DurationMilliseconds,
    EcsBenchmarkEnvironment Environment,
    EcsBenchmarkSourceRevision SourceRevision,
    EcsBenchmarkConfiguration Configuration,
    EcsBenchmarkResult[] Results)
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

internal sealed record EcsBenchmarkSourceRevision(
    string GitCommitSha,
    bool GitWorkingTreeClean);

internal sealed record EcsBenchmarkEnvironment(
    string MachineName,
    string Framework,
    string OperatingSystem,
    string ProcessArchitecture,
    string OperatingSystemArchitecture,
    int ProcessorCount,
    long TotalAvailableMemoryBytes,
    bool ServerGarbageCollection,
    string GarbageCollectorLatencyMode,
    string BuildConfiguration);

internal sealed record EcsBenchmarkConfiguration(
    string Profile,
    int[] EntityCounts,
    int WarmupSamples,
    int Samples,
    bool FreshWorldPerSample,
    int QueryIterations,
    int StructuralIterations,
    string PercentileMethod,
    string AllocatedBytesMetric,
    string TotalAllocatedBytesMetric);

internal sealed record EcsBenchmarkResult(
    string Scenario,
    int EntityCount,
    int OperationsPerSample,
    int SampleCount,
    int WarmupCount,
    bool FreshWorldPerSample,
    MetricDistribution ElapsedMilliseconds,
    MetricDistribution AllocatedBytes,
    MetricDistribution TotalAllocatedBytes,
    MetricDistribution WorkingSetBytes,
    MetricDistribution WorkingSetDeltaBytes,
    GarbageCollectionCounts GarbageCollections,
    BenchmarkWorkloadMetricAggregate WorkloadMetrics,
    string Checksum,
    EcsBenchmarkSample[] Samples);

internal sealed record EcsBenchmarkSample(
    int Sample,
    double ElapsedMilliseconds,
    long AllocatedBytes,
    long TotalAllocatedBytes,
    GarbageCollectionCounts GarbageCollections,
    long ManagedMemoryBeforeBytes,
    long ManagedMemoryAfterBytes,
    long WorkingSetBeforeBytes,
    long WorkingSetAfterBytes,
    long WorkingSetDeltaBytes,
    BenchmarkWorkloadMetricSample WorkloadMetrics,
    string Checksum);

internal sealed record MetricDistribution(double P50, double P95, double P99, double Max)
{
    internal static MetricDistribution From(
        EcsBenchmarkSample[] samples,
        Func<EcsBenchmarkSample, double> selector)
    {
        double[] sorted = samples.Select(selector).Order().ToArray();
        return new MetricDistribution(
            Percentile(sorted, 0.50),
            Percentile(sorted, 0.95),
            Percentile(sorted, 0.99),
            sorted[^1]);
    }

    private static double Percentile(double[] sorted, double percentile)
    {
        double position = (sorted.Length - 1) * percentile;
        int lower = (int)Math.Floor(position);
        int upper = (int)Math.Ceiling(position);
        if (lower == upper)
            return sorted[lower];
        double fraction = position - lower;
        return sorted[lower] + (sorted[upper] - sorted[lower]) * fraction;
    }
}

internal sealed record BenchmarkWorkloadMetricSample(
    long PayloadBytes,
    double UpdateMilliseconds,
    double CheckpointWriteMilliseconds,
    double LoadMilliseconds,
    double DurableCommitMilliseconds,
    double DurableLoadMilliseconds)
{
    internal static readonly BenchmarkWorkloadMetricSample Empty = new(0, 0, 0, 0, 0, 0);
}

internal sealed record BenchmarkWorkloadMetricAggregate(
    MetricDistribution PayloadBytes,
    MetricDistribution UpdateMilliseconds,
    MetricDistribution CheckpointWriteMilliseconds,
    MetricDistribution LoadMilliseconds,
    MetricDistribution DurableCommitMilliseconds,
    MetricDistribution DurableLoadMilliseconds)
{
    internal static BenchmarkWorkloadMetricAggregate From(EcsBenchmarkSample[] samples) => new(
        MetricDistribution.From(samples, static sample => sample.WorkloadMetrics.PayloadBytes),
        MetricDistribution.From(samples, static sample => sample.WorkloadMetrics.UpdateMilliseconds),
        MetricDistribution.From(
            samples,
            static sample => sample.WorkloadMetrics.CheckpointWriteMilliseconds),
        MetricDistribution.From(samples, static sample => sample.WorkloadMetrics.LoadMilliseconds),
        MetricDistribution.From(samples, static sample => sample.WorkloadMetrics.DurableCommitMilliseconds),
        MetricDistribution.From(samples, static sample => sample.WorkloadMetrics.DurableLoadMilliseconds));
}

internal sealed record GarbageCollectionCounts(int Generation0, int Generation1, int Generation2)
{
    internal static GarbageCollectionCounts Subtract(
        GarbageCollectionCounts after,
        GarbageCollectionCounts before) =>
        new(
            after.Generation0 - before.Generation0,
            after.Generation1 - before.Generation1,
            after.Generation2 - before.Generation2);
}
