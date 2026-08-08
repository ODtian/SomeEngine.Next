using System.Text.Json;

namespace SomeEngine.ECS.Benchmarks.Tests;

internal static class BenchmarkTestData
{
    internal static EcsBenchmarkEnvironment ReleaseEnvironment() => new(
        MachineName: "benchmark-host",
        Framework: ".NET test",
        OperatingSystem: "test-os",
        ProcessArchitecture: "X64",
        OperatingSystemArchitecture: "X64",
        ProcessorCount: 8,
        TotalAvailableMemoryBytes: 16L * 1024 * 1024 * 1024,
        ServerGarbageCollection: true,
        GarbageCollectorLatencyMode: "Interactive",
        BuildConfiguration: "Release");

    internal static EcsBenchmarkConfiguration CertificationConfiguration() => new(
        Profile: "certification",
        EntityCounts: [.. BenchmarkOptions.CertificationEntityCounts],
        WarmupSamples: BenchmarkOptions.CertificationWarmupSamples,
        Samples: BenchmarkOptions.CertificationSamples,
        FreshWorldPerSample: true,
        QueryIterations: BenchmarkOptions.CertificationQueryIterations,
        StructuralIterations: BenchmarkOptions.CertificationStructuralIterations,
        PercentileMethod: "R-7 linear interpolation over fresh samples",
        AllocatedBytesMetric: "current managed thread",
        TotalAllocatedBytesMetric: "all managed threads");

    internal static BenchmarkOptions CertificationOptions(
        string baselinePath,
        string budgetPath,
        string evidenceManifestPath = "evidence-manifest.json") => new(
        BenchmarkProfile.Certification,
        [.. BenchmarkOptions.CertificationEntityCounts],
        BenchmarkOptions.CertificationWarmupSamples,
        BenchmarkOptions.CertificationSamples,
        BenchmarkOptions.CertificationQueryIterations,
        BenchmarkOptions.CertificationStructuralIterations,
        OutputPath: null,
        BaselinePath: baselinePath,
        AbsoluteBudgetsPath: budgetPath,
        EvidenceManifestPath: evidenceManifestPath,
        BenchmarkOptions.DefaultMaximumP50RegressionPercent,
        BenchmarkOptions.DefaultMaximumP99RegressionPercent);

    internal static BenchmarkOptions SmokeOptions(
        string? baselinePath = null,
        string? absoluteBudgetsPath = null,
        double maximumP50RegressionPercent = BenchmarkOptions.DefaultMaximumP50RegressionPercent,
        double maximumP99RegressionPercent = BenchmarkOptions.DefaultMaximumP99RegressionPercent) => new(
            BenchmarkProfile.Smoke,
            [100_000],
            WarmupSamples: 1,
            Samples: 3,
            QueryIterations: 8,
            StructuralIterations: 8,
            OutputPath: null,
            BaselinePath: baselinePath,
            AbsoluteBudgetsPath: absoluteBudgetsPath,
            EvidenceManifestPath: null,
            maximumP50RegressionPercent,
            maximumP99RegressionPercent);

    internal static EcsBenchmarkSample Sample(double elapsedMilliseconds) => new(
        Sample: 1,
        ElapsedMilliseconds: elapsedMilliseconds,
        AllocatedBytes: 0,
        TotalAllocatedBytes: 0,
        GarbageCollections: new GarbageCollectionCounts(0, 0, 0),
        ManagedMemoryBeforeBytes: 0,
        ManagedMemoryAfterBytes: 0,
        WorkingSetBeforeBytes: 0,
        WorkingSetAfterBytes: 0,
        WorkingSetDeltaBytes: 0,
        StructuralMetrics: EmptyStructuralSample(),
        WorkloadMetrics: EmptyWorkloadSample(),
        Checksum: "test");

    internal static EcsBenchmarkResult Result(
        string scenario,
        MetricDistribution? elapsed = null,
        MetricDistribution? allocated = null,
        MetricDistribution? totalAllocated = null,
        MetricDistribution? workingSet = null,
        MetricDistribution? workingSetDelta = null) => new(
            Scenario: scenario,
            EntityCount: 100_000,
            OperationsPerSample: 100_000,
            SampleCount: BenchmarkOptions.CertificationSamples,
            WarmupCount: BenchmarkOptions.CertificationWarmupSamples,
            FreshWorldPerSample: true,
            ElapsedMilliseconds: elapsed ?? new MetricDistribution(1, 1, 1, 1),
            AllocatedBytes: allocated ?? new MetricDistribution(1, 1, 1, 1),
            TotalAllocatedBytes: totalAllocated ?? new MetricDistribution(1, 1, 1, 1),
            WorkingSetBytes: workingSet ?? new MetricDistribution(1, 1, 1, 1),
            WorkingSetDeltaBytes: workingSetDelta ?? new MetricDistribution(1, 1, 1, 1),
            GarbageCollections: new GarbageCollectionCounts(0, 0, 0),
            StructuralMetrics: EmptyStructuralAggregate(),
            WorkloadMetrics: EmptyWorkloadAggregate(),
            Checksum: "test",
            Samples: []);

    internal static object EqualityBudget() => new
    {
        maxP50Milliseconds = 10.0,
        maxP95Milliseconds = 11.0,
        maxP99Milliseconds = 12.0,
        maxMilliseconds = 13.0,
        maxAllocatedBytesPerSample = 14.0,
        maxTotalAllocatedBytesPerSample = 15.0,
        maxWorkingSetBytes = 16.0,
        maxWorkingSetDeltaBytes = 17.0,
    };

    internal static EcsBenchmarkResult ResultAtAbsoluteThresholds(string scenario) => Result(
        scenario,
        elapsed: new MetricDistribution(10, 11, 12, 13),
        allocated: new MetricDistribution(1, 1, 1, 14),
        totalAllocated: new MetricDistribution(1, 1, 1, 15),
        workingSet: new MetricDistribution(1, 1, 1, 16),
        workingSetDelta: new MetricDistribution(1, 1, 1, 17));

    internal static EcsBenchmarkResult ExceedAbsoluteThreshold(
        EcsBenchmarkResult result,
        string metric) => metric switch
        {
            "p50" => result with
            {
                ElapsedMilliseconds = result.ElapsedMilliseconds with { P50 = 10.001 },
            },
            "p95" => result with
            {
                ElapsedMilliseconds = result.ElapsedMilliseconds with { P95 = 11.001 },
            },
            "p99" => result with
            {
                ElapsedMilliseconds = result.ElapsedMilliseconds with { P99 = 12.001 },
            },
            "maximum" => result with
            {
                ElapsedMilliseconds = result.ElapsedMilliseconds with { Max = 13.001 },
            },
            "allocated" => result with
            {
                AllocatedBytes = result.AllocatedBytes with { Max = 14.001 },
            },
            "total-allocated" => result with
            {
                TotalAllocatedBytes = result.TotalAllocatedBytes with { Max = 15.001 },
            },
            "working-set" => result with
            {
                WorkingSetBytes = result.WorkingSetBytes with { Max = 16.001 },
            },
            "working-set-delta" => result with
            {
                WorkingSetDeltaBytes = result.WorkingSetDeltaBytes with { Max = 17.001 },
            },
            _ => throw new ArgumentOutOfRangeException(nameof(metric), metric, null),
        };

    internal static string WriteBaseline(
        TemporaryDirectory directory,
        EcsBenchmarkEnvironment environment,
        EcsBenchmarkConfiguration configuration,
        IEnumerable<string> scenarios,
        double p50 = 4.0,
        double p99 = 8.0,
        bool corruptRawSummary = false)
    {
        double reportedLastSample = p50 + 100.0 * (p99 - p50);
        double[] reportedElapsedSamples = Enumerable
            .Repeat(p50, BenchmarkOptions.CertificationSamples)
            .ToArray();
        reportedElapsedSamples[^1] = reportedLastSample;
        MetricDistribution elapsedDistribution = Distribution(reportedElapsedSamples);
        double rawLastSample = corruptRawSummary
            ? reportedLastSample + 1.0
            : reportedLastSample;
        string[] scenarioNames = scenarios.ToArray();
        object[] samples = Enumerable.Range(0, BenchmarkOptions.CertificationSamples)
            .Select(index => (object)new
            {
                sample = index + 1,
                elapsedMilliseconds = index == BenchmarkOptions.CertificationSamples - 1
                    ? rawLastSample
                    : p50,
                allocatedBytes = 0,
                totalAllocatedBytes = 0,
                garbageCollections = new
                {
                    generation0 = 0,
                    generation1 = 0,
                    generation2 = 0,
                },
                managedMemoryBeforeBytes = 0,
                managedMemoryAfterBytes = 0,
                workingSetBeforeBytes = 0,
                workingSetAfterBytes = 0,
                workingSetDeltaBytes = 0,
                structuralMetrics = EmptyStructuralSample(),
                workloadMetrics = EmptyWorkloadSample(),
                checksum = "test",
            })
            .ToArray();
        object[] results = scenarioNames
            .Select(scenario => (object)new
            {
                scenario,
                entityCount = 100_000,
                operationsPerSample = 100_000,
                sampleCount = BenchmarkOptions.CertificationSamples,
                warmupCount = BenchmarkOptions.CertificationWarmupSamples,
                freshWorldPerSample = true,
                elapsedMilliseconds = elapsedDistribution,
                allocatedBytes = new { p50 = 0, p95 = 0, p99 = 0, max = 0 },
                totalAllocatedBytes = new { p50 = 0, p95 = 0, p99 = 0, max = 0 },
                workingSetBytes = new { p50 = 0, p95 = 0, p99 = 0, max = 0 },
                workingSetDeltaBytes = new { p50 = 0, p95 = 0, p99 = 0, max = 0 },
                garbageCollections = new
                {
                    generation0 = 0,
                    generation1 = 0,
                    generation2 = 0,
                },
                structuralMetrics = EmptyStructuralAggregate(),
                workloadMetrics = EmptyWorkloadAggregate(),
                checksum = "test",
                samples,
            })
            .ToArray();
        string json = JsonSerializer.Serialize(
            new
            {
                schemaVersion = EcsBenchmarkSuite.ReportSchemaVersion,
                passed = true,
                startedUtc = DateTimeOffset.UtcNow.AddSeconds(-1),
                completedUtc = DateTimeOffset.UtcNow,
                durationMilliseconds = 1.0,
                environment,
                sourceRevision = new
                {
                    gitCommitSha = new string('a', 40),
                    gitWorkingTreeClean = true,
                },
                configuration,
                results,
                gate = new
                {
                    passed = true,
                    maximumP50RegressionPercent =
                        BenchmarkOptions.DefaultMaximumP50RegressionPercent,
                    maximumP99RegressionPercent =
                        BenchmarkOptions.DefaultMaximumP99RegressionPercent,
                    evaluations = scenarioNames.Select(scenario => new
                    {
                        scenario,
                        passed = true,
                        violations = Array.Empty<string>(),
                    }),
                    violations = Array.Empty<string>(),
                },
            },
            EcsBenchmarkReport.JsonOptions);
        return directory.Write("baseline.json", json);
    }

    internal static string WriteBudget(
        TemporaryDirectory directory,
        object? defaults = null,
        IReadOnlyDictionary<string, object?>? scenarioBudgets = null)
    {
        defaults ??= new
        {
            maxP50Milliseconds = 1000.0,
            maxP95Milliseconds = 1000.0,
            maxP99Milliseconds = 1000.0,
            maxMilliseconds = 1000.0,
        };
        string json = JsonSerializer.Serialize(
            new
            {
                schemaVersion = 1,
                defaults,
                scenarios = scenarioBudgets,
            },
            EcsBenchmarkReport.JsonOptions);
        return directory.Write("budgets.json", json);
    }

    private static StructuralMetricSample EmptyStructuralSample() => new(
        Started: 0,
        Published: 0,
        Aborted: 0,
        PrepareMilliseconds: 0,
        CommitMilliseconds: 0,
        LifetimeMilliseconds: 0,
        WorldMaximumPrepareMilliseconds: 0,
        WorldMaximumCommitMilliseconds: 0,
        WorldMaximumLifetimeMilliseconds: 0,
        ClonedArchetypeShells: 0,
        WorldMaximumClonedArchetypeShells: 0,
        ClonedChunkShells: 0,
        WorldMaximumClonedChunkShells: 0,
        ClonedQueryMatches: 0,
        WorldMaximumClonedQueryMatches: 0);

    private static StructuralMetricAggregate EmptyStructuralAggregate() => new(
        Started: 0,
        Published: 0,
        Aborted: 0,
        PrepareMilliseconds: 0,
        CommitMilliseconds: 0,
        LifetimeMilliseconds: 0,
        WorldMaximumPrepareMilliseconds: 0,
        WorldMaximumCommitMilliseconds: 0,
        WorldMaximumLifetimeMilliseconds: 0,
        ClonedArchetypeShells: 0,
        WorldMaximumClonedArchetypeShells: 0,
        ClonedChunkShells: 0,
        WorldMaximumClonedChunkShells: 0,
        ClonedQueryMatches: 0,
        WorldMaximumClonedQueryMatches: 0);

    private static BenchmarkWorkloadMetricSample EmptyWorkloadSample() =>
        BenchmarkWorkloadMetricSample.Empty;

    private static BenchmarkWorkloadMetricAggregate EmptyWorkloadAggregate()
    {
        var empty = new MetricDistribution(0, 0, 0, 0);
        return new BenchmarkWorkloadMetricAggregate(empty, empty, empty, empty, empty, empty);
    }

    private static MetricDistribution Distribution(double[] values)
    {
        double[] sorted = [.. values];
        Array.Sort(sorted);
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

internal sealed class TemporaryDirectory : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(),
        "SomeEngine.ECS.Benchmarks.Tests",
        Guid.NewGuid().ToString("N"));

    internal TemporaryDirectory()
    {
        Directory.CreateDirectory(_path);
    }

    internal string Write(string fileName, string contents)
    {
        string path = Path.Combine(_path, fileName);
        File.WriteAllText(path, contents);
        return path;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
