using System.Diagnostics;
using System.Globalization;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Registry;

namespace SomeEngine.ECS.Benchmarks;

internal static partial class EcsBenchmarkSuite
{
    internal const int ReportSchemaVersion = 5;
    private const ulong FnvOffsetBasis = 14695981039346656037UL;
    private const ulong FnvPrime = 1099511628211UL;

    private static readonly int[] PositionComponents =
    [
        ComponentMetadata<Position>.Id,
    ];

    internal static EcsBenchmarkReport Run(BenchmarkOptions options)
    {
        DateTimeOffset startedUtc = DateTimeOffset.UtcNow;
        long suiteStarted = Stopwatch.GetTimestamp();
        EcsBenchmarkEnvironment environment = CreateEnvironment();
        EcsBenchmarkSourceRevision sourceRevision = CreateSourceRevision();
        ValidateCertificationSourceRevision(options.Profile, sourceRevision, sourceRevision);
        CertificationEvidenceBinding? certificationEvidence =
            CertificationEvidenceManifest.Validate(options, sourceRevision);
        string[] plannedScenarios = CreatePlannedScenarioNames(options);
        BenchmarkGateContext gateContext = BenchmarkGateEvaluator.Prepare(
            options,
            environment,
            plannedScenarios,
            certificationEvidence);
        PrimeRuntime();

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

        EcsBenchmarkGate gate = BenchmarkGateEvaluator.Evaluate(options, results, gateContext);
        EcsBenchmarkSourceRevision completedSourceRevision = CreateSourceRevision();
        ValidateCertificationSourceRevision(
            options.Profile,
            sourceRevision,
            completedSourceRevision);
        certificationEvidence?.ValidationState?.VerifyUnchanged();
        DateTimeOffset completedUtc = DateTimeOffset.UtcNow;
        return new EcsBenchmarkReport(
            SchemaVersion: ReportSchemaVersion,
            Passed: gate.Passed,
            StartedUtc: startedUtc,
            CompletedUtc: completedUtc,
            DurationMilliseconds: Stopwatch.GetElapsedTime(suiteStarted).TotalMilliseconds,
            Environment: environment,
            SourceRevision: sourceRevision,
            CertificationEvidence: certificationEvidence,
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
            Results: results.ToArray(),
            Gate: gate);
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

    internal static void ValidateCertificationSourceRevision(
        BenchmarkProfile profile,
        EcsBenchmarkSourceRevision initial,
        EcsBenchmarkSourceRevision completed)
    {
        if (profile != BenchmarkProfile.Certification)
            return;
        if (!initial.IsCleanCommit)
        {
            throw new BenchmarkConfigurationException(
                "Certification requires a clean Git worktree at a full commit SHA before " +
                "collecting evidence. Commit or remove tracked and untracked changes first.");
        }
        if (!completed.IsCleanCommit || completed != initial)
        {
            throw new BenchmarkConfigurationException(
                "The Git worktree or HEAD changed while certification was running; no report " +
                "may be emitted for mixed source revisions.");
        }
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
                () => new StorageOwnersExecution(entityCount)),
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
                () => new SnapshotWriteExecution(entityCount)),
            new ScenarioDefinition(
                $"snapshot-read-{countLabel}",
                entityCount,
                OperationsPerSample: entityCount,
                () => new SnapshotReadExecution(entityCount)),
            new ScenarioDefinition(
                $"mixed-frame-update-snapshot-load-{countLabel}",
                entityCount,
                OperationsPerSample: checked(entityCount * 3),
                () => new MixedFrameExecution(entityCount)),
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

    internal static string[] CreatePlannedScenarioNames(BenchmarkOptions options) =>
        options.EntityCounts
            .SelectMany((entityCount, index) => CreateScenarios(
                options,
                entityCount,
                includeDurablePersistence: index == 0))
            .Select(static scenario => scenario.Name)
            .ToArray();

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
            AggregateStructuralMetrics(samples),
            BenchmarkWorkloadMetricAggregate.From(samples),
            expectedChecksum ?? throw new InvalidOperationException("A measured scenario had no samples."),
            samples);
    }

    private static EcsBenchmarkSample MeasureSample(int sample, IBenchmarkExecution execution)
    {
        WorldStructuralMetrics structuralBefore = execution.World.GetStructuralMetrics();
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
        WorldStructuralMetrics structuralAfter = execution.World.GetStructuralMetrics();
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
            StructuralMetricSample.Between(structuralBefore, structuralAfter),
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

    private static StructuralMetricAggregate AggregateStructuralMetrics(
        EcsBenchmarkSample[] samples)
    {
        long started = 0;
        long published = 0;
        long aborted = 0;
        double prepareMilliseconds = 0;
        double commitMilliseconds = 0;
        double lifetimeMilliseconds = 0;
        double worldMaximumPrepareMilliseconds = 0;
        double worldMaximumCommitMilliseconds = 0;
        double worldMaximumLifetimeMilliseconds = 0;
        long clonedArchetypeShells = 0;
        long worldMaximumClonedArchetypeShells = 0;
        long clonedChunkShells = 0;
        long worldMaximumClonedChunkShells = 0;
        long clonedQueryMatches = 0;
        long worldMaximumClonedQueryMatches = 0;
        foreach (EcsBenchmarkSample sample in samples)
        {
            StructuralMetricSample metrics = sample.StructuralMetrics;
            started = checked(started + metrics.Started);
            published = checked(published + metrics.Published);
            aborted = checked(aborted + metrics.Aborted);
            prepareMilliseconds += metrics.PrepareMilliseconds;
            commitMilliseconds += metrics.CommitMilliseconds;
            lifetimeMilliseconds += metrics.LifetimeMilliseconds;
            worldMaximumPrepareMilliseconds = Math.Max(
                worldMaximumPrepareMilliseconds,
                metrics.WorldMaximumPrepareMilliseconds);
            worldMaximumCommitMilliseconds = Math.Max(
                worldMaximumCommitMilliseconds,
                metrics.WorldMaximumCommitMilliseconds);
            worldMaximumLifetimeMilliseconds = Math.Max(
                worldMaximumLifetimeMilliseconds,
                metrics.WorldMaximumLifetimeMilliseconds);
            clonedArchetypeShells = checked(
                clonedArchetypeShells + metrics.ClonedArchetypeShells);
            worldMaximumClonedArchetypeShells = Math.Max(
                worldMaximumClonedArchetypeShells,
                metrics.WorldMaximumClonedArchetypeShells);
            clonedChunkShells = checked(clonedChunkShells + metrics.ClonedChunkShells);
            worldMaximumClonedChunkShells = Math.Max(
                worldMaximumClonedChunkShells,
                metrics.WorldMaximumClonedChunkShells);
            clonedQueryMatches = checked(clonedQueryMatches + metrics.ClonedQueryMatches);
            worldMaximumClonedQueryMatches = Math.Max(
                worldMaximumClonedQueryMatches,
                metrics.WorldMaximumClonedQueryMatches);
        }

        return new StructuralMetricAggregate(
            started,
            published,
            aborted,
            prepareMilliseconds,
            commitMilliseconds,
            lifetimeMilliseconds,
            worldMaximumPrepareMilliseconds,
            worldMaximumCommitMilliseconds,
            worldMaximumLifetimeMilliseconds,
            clonedArchetypeShells,
            worldMaximumClonedArchetypeShells,
            clonedChunkShells,
            worldMaximumClonedChunkShells,
            clonedQueryMatches,
            worldMaximumClonedQueryMatches);
    }

    private static void CollectGarbage()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static void PrimeRuntime()
    {
        var world = new World();
        world.ExecuteBundleSpawn(
            PositionComponents,
            static view =>
            {
                var position = default(Position);
                view.Write(in position);
            });
        QueryHandle query = world.Query(world.QueryDefinition().Read<Position>());
        int count = 0;
        world.ExecuteQuery(
            query,
            ref count,
            static (QueryCursor cursor, ref int rows) =>
            {
                foreach (var _ in cursor.Rows)
                    rows++;
            });
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
        QueryHandle query = world.Query(world.QueryDefinition().Read<Position>());
        world.ExecuteQuery(
            query,
            ref state,
            static (QueryCursor cursor, ref WorldChecksumState checksum) =>
            {
                foreach (var row in cursor.Rows)
                {
                    Position position = row.Read<Position>();
                    checksum.Count++;
                    checksum.SumX += position.X;
                    checksum.SumY += position.Y;
                    checksum.Hash = Mix(checksum.Hash, unchecked((uint)position.X));
                    checksum.Hash = Mix(checksum.Hash, unchecked((uint)position.Y));
                }
            });

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
        private readonly QueryHandle _query;
        private long _timedChecksum;

        internal QueryExecution(int entityCount, int queryIterations)
        {
            _entityCount = entityCount;
            _queryIterations = queryIterations;
            World = CreatePopulatedWorld(entityCount);
            _query = World.Query(World.QueryDefinition().Read<Position>());
        }

        public World World { get; }

        public void Execute()
        {
            long checksum = 0;
            for (int iteration = 0; iteration < _queryIterations; iteration++)
            {
                World.ExecuteQuery(
                    _query,
                    ref checksum,
                    static (QueryCursor cursor, ref long sum) =>
                    {
                        foreach (var row in cursor.Rows)
                        {
                            Position position = row.Read<Position>();
                            sum += position.X;
                            sum += position.Y;
                        }
                    });
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
        private readonly WorldStructuralMetrics _before;

        internal StructuralExecution(int entityCount, int structuralIterations)
        {
            _entityCount = entityCount;
            _structuralIterations = structuralIterations;
            World = CreatePopulatedWorld(entityCount);
            _ = World.Query(World.QueryDefinition().Read<Position>());
            _before = World.GetStructuralMetrics();
        }

        public World World { get; }

        public void Execute()
        {
            for (int iteration = 0; iteration < _structuralIterations; iteration++)
            {
                int value = _entityCount + iteration;
                World.ExecuteBundleSpawn(
                    PositionComponents,
                    ref value,
                    static (BundleWriteView view, ref int x) =>
                    {
                        var position = new Position(x, -x);
                        view.Write(in position);
                    });
            }
        }

        public string ValidateAndGetChecksum()
        {
            WorldStructuralMetrics metrics = World.GetStructuralMetrics();
            if (metrics.Published - _before.Published != _structuralIterations ||
                metrics.Started - _before.Started != _structuralIterations ||
                metrics.Aborted != _before.Aborted)
            {
                throw new InvalidOperationException(
                    "Every benchmark structural candidate must start and publish exactly once " +
                    "without an abort.");
            }
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
            _world.ExecuteBundleSpawnBatch(
                PositionComponents,
                _count,
                static view =>
                {
                    var position = new Position(view.Index, view.Index + 1);
                    view.Write(in position);
                });
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
    bool Passed,
    DateTimeOffset StartedUtc,
    DateTimeOffset CompletedUtc,
    double DurationMilliseconds,
    EcsBenchmarkEnvironment Environment,
    EcsBenchmarkSourceRevision SourceRevision,
    CertificationEvidenceBinding? CertificationEvidence,
    EcsBenchmarkConfiguration Configuration,
    EcsBenchmarkResult[] Results,
    EcsBenchmarkGate Gate)
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
    bool GitWorkingTreeClean)
{
    internal bool IsCleanCommit =>
        GitWorkingTreeClean &&
        GitCommitSha.Length is 40 or 64 &&
        GitCommitSha.All(static character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');
}

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
    StructuralMetricAggregate StructuralMetrics,
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
    StructuralMetricSample StructuralMetrics,
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
    double SnapshotWriteMilliseconds,
    double LoadMilliseconds,
    double DurableCommitMilliseconds,
    double DurableLoadMilliseconds)
{
    internal static readonly BenchmarkWorkloadMetricSample Empty = new(0, 0, 0, 0, 0, 0);
}

internal sealed record BenchmarkWorkloadMetricAggregate(
    MetricDistribution PayloadBytes,
    MetricDistribution UpdateMilliseconds,
    MetricDistribution SnapshotWriteMilliseconds,
    MetricDistribution LoadMilliseconds,
    MetricDistribution DurableCommitMilliseconds,
    MetricDistribution DurableLoadMilliseconds)
{
    internal static BenchmarkWorkloadMetricAggregate From(EcsBenchmarkSample[] samples) => new(
        MetricDistribution.From(samples, static sample => sample.WorkloadMetrics.PayloadBytes),
        MetricDistribution.From(samples, static sample => sample.WorkloadMetrics.UpdateMilliseconds),
        MetricDistribution.From(
            samples,
            static sample => sample.WorkloadMetrics.SnapshotWriteMilliseconds),
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

internal sealed record StructuralMetricSample(
    long Started,
    long Published,
    long Aborted,
    double PrepareMilliseconds,
    double CommitMilliseconds,
    double LifetimeMilliseconds,
    double WorldMaximumPrepareMilliseconds,
    double WorldMaximumCommitMilliseconds,
    double WorldMaximumLifetimeMilliseconds,
    long ClonedArchetypeShells,
    long WorldMaximumClonedArchetypeShells,
    long ClonedChunkShells,
    long WorldMaximumClonedChunkShells,
    long ClonedQueryMatches,
    long WorldMaximumClonedQueryMatches)
{
    internal static StructuralMetricSample Between(
        WorldStructuralMetrics before,
        WorldStructuralMetrics after) =>
        new(
            after.Started - before.Started,
            after.Published - before.Published,
            after.Aborted - before.Aborted,
            (after.PrepareTime - before.PrepareTime).TotalMilliseconds,
            (after.CommitTime - before.CommitTime).TotalMilliseconds,
            (after.Lifetime - before.Lifetime).TotalMilliseconds,
            after.MaximumPrepareTime.TotalMilliseconds,
            after.MaximumCommitTime.TotalMilliseconds,
            after.MaximumLifetime.TotalMilliseconds,
            after.ClonedArchetypeShells - before.ClonedArchetypeShells,
            after.MaximumClonedArchetypeShells,
            after.ClonedChunkShells - before.ClonedChunkShells,
            after.MaximumClonedChunkShells,
            after.ClonedQueryMatches - before.ClonedQueryMatches,
            after.MaximumClonedQueryMatches);
}

internal sealed record StructuralMetricAggregate(
    long Started,
    long Published,
    long Aborted,
    double PrepareMilliseconds,
    double CommitMilliseconds,
    double LifetimeMilliseconds,
    double WorldMaximumPrepareMilliseconds,
    double WorldMaximumCommitMilliseconds,
    double WorldMaximumLifetimeMilliseconds,
    long ClonedArchetypeShells,
    long WorldMaximumClonedArchetypeShells,
    long ClonedChunkShells,
    long WorldMaximumClonedChunkShells,
    long ClonedQueryMatches,
    long WorldMaximumClonedQueryMatches);
