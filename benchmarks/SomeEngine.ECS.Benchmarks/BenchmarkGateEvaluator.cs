using System.Globalization;
using System.Text.Json;

namespace SomeEngine.ECS.Benchmarks;

internal static class BenchmarkGateEvaluator
{
    internal static BenchmarkGateContext Prepare(
        BenchmarkOptions options,
        EcsBenchmarkEnvironment environment,
        IReadOnlyCollection<string> plannedScenarios,
        CertificationEvidenceBinding? certificationEvidence = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(plannedScenarios);

        CertificationEvidenceValidationState? evidenceState =
            certificationEvidence?.ValidationState;
        BaselineCatalog? baseline = evidenceState?.Baseline ?? (options.BaselinePath is null
            ? null
            : BaselineCatalog.Load(options.BaselinePath));
        AbsoluteBudgetCatalog? budgets = evidenceState?.Budgets ??
            (options.AbsoluteBudgetsPath is null
                ? null
                : AbsoluteBudgetCatalog.Load(options.AbsoluteBudgetsPath));

        if (options.Profile == BenchmarkProfile.Certification)
        {
            if (!string.Equals(environment.BuildConfiguration, "Release", StringComparison.Ordinal))
            {
                throw new BenchmarkConfigurationException(
                    "Certification requires a Release benchmark executable.");
            }

            baseline!.ValidateCertificationCompatibility(options, environment, plannedScenarios);
            budgets!.ValidateCertificationCompatibility(plannedScenarios);
        }

        return new BenchmarkGateContext(baseline, budgets);
    }

    internal static EcsBenchmarkGate Evaluate(
        BenchmarkOptions options,
        IReadOnlyList<EcsBenchmarkResult> results,
        BenchmarkGateContext context)
    {
        BaselineCatalog? baseline = context.Baseline;
        AbsoluteBudgetCatalog? budgets = context.Budgets;

        var evaluations = new List<EcsBenchmarkGateEvaluation>(results.Count);
        var allViolations = new List<string>();
        foreach (EcsBenchmarkResult result in results)
        {
            var violations = new List<string>();
            AbsoluteBudget? budget = budgets?.Resolve(result.Scenario);
            if (options.Profile == BenchmarkProfile.Certification)
            {
                if (budget?.MaxP50Milliseconds is null)
                    violations.Add("Certification requires an absolute maxP50Milliseconds budget.");
                if (budget?.MaxP95Milliseconds is null)
                    violations.Add("Certification requires an absolute maxP95Milliseconds budget.");
                if (budget?.MaxP99Milliseconds is null)
                    violations.Add("Certification requires an absolute maxP99Milliseconds budget.");
                if (budget?.MaxMilliseconds is null)
                    violations.Add("Certification requires an absolute maxMilliseconds budget.");
            }

            if (budget is not null)
                EvaluateAbsoluteBudget(result, budget, violations);

            BaselineComparison? comparison = null;
            if (baseline is not null)
            {
                if (!baseline.Results.TryGetValue(result.Scenario, out BaselineValues? baselineValues))
                {
                    violations.Add("The supplied baseline has no matching scenario.");
                }
                else
                {
                    comparison = CompareWithBaseline(options, result, baselineValues, violations);
                }
            }
            else if (options.Profile == BenchmarkProfile.Certification)
            {
                violations.Add("Certification requires a baseline report.");
            }

            string[] scenarioViolations = violations.ToArray();
            foreach (string violation in scenarioViolations)
                allViolations.Add($"{result.Scenario}: {violation}");
            evaluations.Add(
                new EcsBenchmarkGateEvaluation(
                    result.Scenario,
                    scenarioViolations.Length == 0,
                    budget,
                    comparison,
                    scenarioViolations));
        }

        return new EcsBenchmarkGate(
            allViolations.Count == 0,
            options.BaselinePath,
            options.AbsoluteBudgetsPath,
            options.MaximumP50RegressionPercent,
            options.MaximumP99RegressionPercent,
            evaluations.ToArray(),
            allViolations.ToArray());
    }

    private static void EvaluateAbsoluteBudget(
        EcsBenchmarkResult result,
        AbsoluteBudget budget,
        List<string> violations)
    {
        AddViolationIfExceeded(
            violations,
            "p50 elapsed milliseconds",
            result.ElapsedMilliseconds.P50,
            budget.MaxP50Milliseconds);
        AddViolationIfExceeded(
            violations,
            "p95 elapsed milliseconds",
            result.ElapsedMilliseconds.P95,
            budget.MaxP95Milliseconds);
        AddViolationIfExceeded(
            violations,
            "p99 elapsed milliseconds",
            result.ElapsedMilliseconds.P99,
            budget.MaxP99Milliseconds);
        AddViolationIfExceeded(
            violations,
            "maximum elapsed milliseconds",
            result.ElapsedMilliseconds.Max,
            budget.MaxMilliseconds);
        AddViolationIfExceeded(
            violations,
            "maximum current-thread allocated bytes",
            result.AllocatedBytes.Max,
            budget.MaxAllocatedBytesPerSample);
        AddViolationIfExceeded(
            violations,
            "maximum all-thread allocated bytes",
            result.TotalAllocatedBytes.Max,
            budget.MaxTotalAllocatedBytesPerSample);
        AddViolationIfExceeded(
            violations,
            "maximum working set bytes",
            result.WorkingSetBytes.Max,
            budget.MaxWorkingSetBytes);
        AddViolationIfExceeded(
            violations,
            "maximum working set delta bytes",
            result.WorkingSetDeltaBytes.Max,
            budget.MaxWorkingSetDeltaBytes);
    }

    private static BaselineComparison CompareWithBaseline(
        BenchmarkOptions options,
        EcsBenchmarkResult result,
        BaselineValues baseline,
        List<string> violations)
    {
        double p50Regression = RegressionPercent(result.ElapsedMilliseconds.P50, baseline.P50Milliseconds);
        double p99Regression = RegressionPercent(result.ElapsedMilliseconds.P99, baseline.P99Milliseconds);
        if (p50Regression > options.MaximumP50RegressionPercent)
        {
            violations.Add(
                $"p50 regression {Format(p50Regression)}% exceeded " +
                $"{Format(options.MaximumP50RegressionPercent)}%.");
        }
        if (p99Regression > options.MaximumP99RegressionPercent)
        {
            violations.Add(
                $"p99 regression {Format(p99Regression)}% exceeded " +
                $"{Format(options.MaximumP99RegressionPercent)}%.");
        }

        return new BaselineComparison(
            baseline.P50Milliseconds,
            baseline.P99Milliseconds,
            result.ElapsedMilliseconds.P50,
            result.ElapsedMilliseconds.P99,
            p50Regression,
            p99Regression);
    }

    private static double RegressionPercent(double current, double baseline) =>
        (current / baseline - 1.0) * 100.0;

    private static void AddViolationIfExceeded(
        List<string> violations,
        string metric,
        double actual,
        double? maximum)
    {
        if (maximum is not null && actual > maximum.Value)
        {
            violations.Add(
                $"{metric} {Format(actual)} exceeded absolute budget {Format(maximum.Value)}.");
        }
    }

    private static string Format(double value) =>
        value.ToString("0.###", CultureInfo.InvariantCulture);

    internal sealed class BaselineCatalog
    {
        private static readonly HashSet<string> StructuralMetricProperties = new(
            StringComparer.Ordinal)
        {
            "started",
            "published",
            "aborted",
            "prepareMilliseconds",
            "commitMilliseconds",
            "lifetimeMilliseconds",
            "worldMaximumPrepareMilliseconds",
            "worldMaximumCommitMilliseconds",
            "worldMaximumLifetimeMilliseconds",
            "clonedArchetypeShells",
            "worldMaximumClonedArchetypeShells",
            "clonedChunkShells",
            "worldMaximumClonedChunkShells",
            "clonedQueryMatches",
            "worldMaximumClonedQueryMatches",
        };

        private static readonly HashSet<string> WorkloadMetricProperties = new(
            StringComparer.Ordinal)
        {
            "payloadBytes",
            "updateMilliseconds",
            "snapshotWriteMilliseconds",
            "loadMilliseconds",
            "durableCommitMilliseconds",
            "durableLoadMilliseconds",
        };

        private static readonly HashSet<string> DistributionProperties = new(
            StringComparer.Ordinal)
        {
            "p50",
            "p95",
            "p99",
            "max",
        };

        private static readonly HashSet<string> ReportRootProperties = new(StringComparer.Ordinal)
        {
            "schemaVersion", "passed", "startedUtc", "completedUtc", "durationMilliseconds",
            "environment", "sourceRevision", "certificationEvidence", "configuration",
            "results", "gate",
        };

        private static readonly HashSet<string> RequiredReportRootProperties = new(StringComparer.Ordinal)
        {
            "schemaVersion", "passed", "startedUtc", "completedUtc", "durationMilliseconds",
            "environment", "sourceRevision", "configuration", "results", "gate",
        };

        private static readonly HashSet<string> EnvironmentProperties = new(StringComparer.Ordinal)
        {
            "machineName", "framework", "operatingSystem", "processArchitecture",
            "operatingSystemArchitecture", "processorCount", "totalAvailableMemoryBytes",
            "serverGarbageCollection", "garbageCollectorLatencyMode", "buildConfiguration",
        };

        private static readonly HashSet<string> ConfigurationProperties = new(StringComparer.Ordinal)
        {
            "profile", "entityCounts", "warmupSamples", "samples", "freshWorldPerSample",
            "queryIterations", "structuralIterations", "percentileMethod",
            "allocatedBytesMetric", "totalAllocatedBytesMetric",
        };

        private static readonly HashSet<string> ResultProperties = new(StringComparer.Ordinal)
        {
            "scenario", "entityCount", "operationsPerSample", "sampleCount", "warmupCount",
            "freshWorldPerSample", "elapsedMilliseconds", "allocatedBytes",
            "totalAllocatedBytes", "workingSetBytes", "workingSetDeltaBytes",
            "garbageCollections", "structuralMetrics", "workloadMetrics", "checksum", "samples",
        };

        private static readonly HashSet<string> SampleProperties = new(StringComparer.Ordinal)
        {
            "sample", "elapsedMilliseconds", "allocatedBytes", "totalAllocatedBytes",
            "garbageCollections", "managedMemoryBeforeBytes", "managedMemoryAfterBytes",
            "workingSetBeforeBytes", "workingSetAfterBytes", "workingSetDeltaBytes",
            "structuralMetrics", "workloadMetrics", "checksum",
        };

        private static readonly HashSet<string> GarbageCollectionProperties = new(StringComparer.Ordinal)
        {
            "generation0", "generation1", "generation2",
        };

        private BaselineCatalog(
            EcsBenchmarkEnvironment environment,
            EcsBenchmarkConfiguration configuration,
            Dictionary<string, BaselineValues> results)
        {
            Environment = environment;
            Configuration = configuration;
            Results = results;
        }

        internal EcsBenchmarkEnvironment Environment { get; }
        internal EcsBenchmarkConfiguration Configuration { get; }
        internal IReadOnlyDictionary<string, BaselineValues> Results { get; }

        internal static BaselineCatalog Load(string path)
        {
            string fullPath = Path.GetFullPath(path);
            return Load(File.ReadAllBytes(fullPath), fullPath);
        }

        internal static BaselineCatalog Load(ReadOnlyMemory<byte> json, string path)
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException($"Baseline '{path}' must contain an object.");
            EnsureRequiredProperties(
                root,
                ReportRootProperties,
                RequiredReportRootProperties,
                $"Baseline '{path}' root");
            if (!root.TryGetProperty("startedUtc", out JsonElement startedUtc) ||
                !startedUtc.TryGetDateTimeOffset(out _) ||
                !root.TryGetProperty("completedUtc", out JsonElement completedUtc) ||
                !completedUtc.TryGetDateTimeOffset(out _) ||
                !root.TryGetProperty("durationMilliseconds", out JsonElement duration) ||
                !duration.TryGetDouble(out double durationMilliseconds) ||
                !double.IsFinite(durationMilliseconds) ||
                durationMilliseconds <= 0)
            {
                throw new InvalidDataException(
                    $"Baseline '{path}' must contain valid timestamps and a positive finite duration.");
            }
            if (!root.TryGetProperty("schemaVersion", out JsonElement schemaElement) ||
                !schemaElement.TryGetInt32(out int schemaVersion) ||
                schemaVersion != EcsBenchmarkSuite.ReportSchemaVersion)
            {
                throw new InvalidDataException(
                    $"Baseline '{path}' must use benchmark report schemaVersion " +
                    $"{EcsBenchmarkSuite.ReportSchemaVersion}.");
            }
            if (!root.TryGetProperty("passed", out JsonElement passedElement) ||
                passedElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                !passedElement.GetBoolean())
            {
                throw new InvalidDataException(
                    $"Baseline '{path}' must be a report whose own gate passed.");
            }
            HashSet<string> gateScenarios = ValidatePassedGate(root, path);

            EcsBenchmarkEnvironment environment = DeserializeRequired<EcsBenchmarkEnvironment>(
                root,
                "environment",
                path);
            EnsureExactProperties(
                root.GetProperty("environment"),
                EnvironmentProperties,
                path,
                "environment");
            EcsBenchmarkConfiguration configuration = DeserializeRequired<EcsBenchmarkConfiguration>(
                root,
                "configuration",
                path);
            EnsureExactProperties(
                root.GetProperty("configuration"),
                ConfigurationProperties,
                path,
                "configuration");
            EcsBenchmarkSourceRevision sourceRevision =
                DeserializeRequired<EcsBenchmarkSourceRevision>(root, "sourceRevision", path);
            EnsureExactProperties(
                root.GetProperty("sourceRevision"),
                new HashSet<string>(StringComparer.Ordinal)
                {
                    "gitCommitSha", "gitWorkingTreeClean",
                },
                path,
                "sourceRevision");
            if (!sourceRevision.IsCleanCommit)
            {
                throw new InvalidDataException(
                    $"Baseline '{path}' must identify a clean Git worktree at a full commit SHA.");
            }
            if (!root.TryGetProperty("results", out JsonElement resultsElement) ||
                resultsElement.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException($"Baseline '{path}' has no results array.");
            }

            var results = new Dictionary<string, BaselineValues>(StringComparer.Ordinal);
            foreach (JsonElement result in resultsElement.EnumerateArray())
            {
                EnsureExactProperties(result, ResultProperties, path, "result");
                if (!result.TryGetProperty("scenario", out JsonElement scenarioElement) ||
                    scenarioElement.ValueKind != JsonValueKind.String)
                {
                    throw new InvalidDataException($"Baseline '{path}' contains a result without a scenario.");
                }
                string scenario = scenarioElement.GetString()!;
                if (string.IsNullOrWhiteSpace(scenario) ||
                    !result.TryGetProperty("entityCount", out JsonElement entityCountElement) ||
                    !entityCountElement.TryGetInt32(out int entityCount) || entityCount <= 0 ||
                    !result.TryGetProperty("operationsPerSample", out JsonElement operationsElement) ||
                    !operationsElement.TryGetInt32(out int operations) || operations <= 0 ||
                    !result.TryGetProperty("checksum", out JsonElement checksumElement) ||
                    checksumElement.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(checksumElement.GetString()))
                {
                    throw new InvalidDataException(
                        $"Baseline scenario '{scenario}' has invalid identity, workload size, or checksum fields.");
                }
                MetricDistribution elapsedDistribution =
                    ValidateDistribution(result, "elapsedMilliseconds", scenario);
                MetricDistribution allocatedDistribution =
                    ValidateDistribution(result, "allocatedBytes", scenario);
                MetricDistribution totalAllocatedDistribution =
                    ValidateDistribution(result, "totalAllocatedBytes", scenario);
                MetricDistribution workingSetDistribution =
                    ValidateDistribution(result, "workingSetBytes", scenario);
                MetricDistribution workingSetDeltaDistribution = ValidateDistribution(
                    result,
                    "workingSetDeltaBytes",
                    scenario,
                    allowNegative: true);
                GarbageCollectionCounts garbageCollections =
                    ValidateGarbageCollections(result, scenario, "aggregate");
                if (!result.TryGetProperty("elapsedMilliseconds", out JsonElement elapsedElement))
                {
                    throw new InvalidDataException(
                        $"Baseline scenario '{scenario}' has no elapsedMilliseconds metric.");
                }

                if (elapsedElement.ValueKind == JsonValueKind.Object &&
                    elapsedElement.TryGetProperty("p50", out JsonElement p50Element) &&
                    elapsedElement.TryGetProperty("p99", out JsonElement p99Element) &&
                    result.TryGetProperty("sampleCount", out JsonElement sampleCountElement) &&
                    sampleCountElement.TryGetInt32(out int sampleCount) &&
                    result.TryGetProperty("warmupCount", out JsonElement warmupCountElement) &&
                    warmupCountElement.TryGetInt32(out int warmupCount) &&
                    result.TryGetProperty("freshWorldPerSample", out JsonElement freshElement) &&
                    freshElement.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    double p50 = ReadPositiveFiniteDouble(
                        p50Element,
                        "elapsedMilliseconds.p50",
                        scenario);
                    double p99 = ReadPositiveFiniteDouble(
                        p99Element,
                        "elapsedMilliseconds.p99",
                        scenario);
                    StructuralMetricAggregate structuralMetrics = ReadStructuralMetrics(
                        result,
                        "structuralMetrics",
                        scenario,
                        "aggregate");
                    BenchmarkWorkloadMetricAggregate workloadMetrics = ReadWorkloadMetricAggregate(
                        result,
                        scenario);
                    ValidateRawSamples(
                        result,
                        scenario,
                        sampleCount,
                        elapsedDistribution,
                        allocatedDistribution,
                        totalAllocatedDistribution,
                        workingSetDistribution,
                        workingSetDeltaDistribution,
                        garbageCollections,
                        structuralMetrics,
                        workloadMetrics,
                        checksumElement.GetString()!);
                    var values = new BaselineValues(
                        p50,
                        p99,
                        sampleCount,
                        warmupCount,
                        freshElement.GetBoolean());
                    if (!results.TryAdd(scenario, values))
                        throw new InvalidDataException($"Baseline '{path}' repeats scenario '{scenario}'.");
                }
                else
                {
                    throw new InvalidDataException(
                        $"Baseline scenario '{scenario}' is missing schema-5 sample metadata or p50/p99 metrics.");
                }
            }

            if (!gateScenarios.SetEquals(results.Keys))
            {
                throw new InvalidDataException(
                    $"Baseline '{path}' gate evaluations do not exactly match its result scenarios.");
            }

            return new BaselineCatalog(environment, configuration, results);
        }

        private static HashSet<string> ValidatePassedGate(JsonElement root, string path)
        {
            if (!root.TryGetProperty("gate", out JsonElement gate) ||
                gate.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException($"Baseline '{path}' has no gate object.");
            }
            if (!gate.TryGetProperty("passed", out JsonElement passed) ||
                passed.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                !passed.GetBoolean())
            {
                throw new InvalidDataException(
                    $"Baseline '{path}' gate must agree that the report passed.");
            }
            if (!gate.TryGetProperty("violations", out JsonElement violations) ||
                violations.ValueKind != JsonValueKind.Array ||
                violations.GetArrayLength() != 0)
            {
                throw new InvalidDataException(
                    $"Baseline '{path}' gate must contain an empty violations array.");
            }
            if (!gate.TryGetProperty("evaluations", out JsonElement evaluations) ||
                evaluations.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException(
                    $"Baseline '{path}' gate has no evaluations array.");
            }

            var scenarios = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonElement evaluation in evaluations.EnumerateArray())
            {
                if (evaluation.ValueKind != JsonValueKind.Object ||
                    !evaluation.TryGetProperty("scenario", out JsonElement scenarioElement) ||
                    scenarioElement.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(scenarioElement.GetString()))
                {
                    throw new InvalidDataException(
                        $"Baseline '{path}' contains a gate evaluation without a scenario.");
                }
                string scenario = scenarioElement.GetString()!;
                if (!evaluation.TryGetProperty("passed", out JsonElement evaluationPassed) ||
                    evaluationPassed.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                    !evaluationPassed.GetBoolean() ||
                    !evaluation.TryGetProperty("violations", out JsonElement evaluationViolations) ||
                    evaluationViolations.ValueKind != JsonValueKind.Array ||
                    evaluationViolations.GetArrayLength() != 0)
                {
                    throw new InvalidDataException(
                        $"Baseline gate evaluation '{scenario}' must be passed with no violations.");
                }
                if (!scenarios.Add(scenario))
                {
                    throw new InvalidDataException(
                        $"Baseline '{path}' repeats gate evaluation '{scenario}'.");
                }
            }

            return scenarios;
        }

        internal void ValidateCertificationCompatibility(
            BenchmarkOptions options,
            EcsBenchmarkEnvironment currentEnvironment,
            IReadOnlyCollection<string> plannedScenarios)
        {
            if (Environment != currentEnvironment)
            {
                throw new BenchmarkConfigurationException(
                    "The certification baseline environment does not exactly match the current " +
                    "machine, runtime, OS/architecture, processor count, memory limit, GC mode, " +
                    "latency mode, and Release build configuration.");
            }
            if (Configuration.EntityCounts is null ||
                !Configuration.EntityCounts.SequenceEqual(options.EntityCounts) ||
                Configuration.WarmupSamples < BenchmarkOptions.CertificationWarmupSamples ||
                Configuration.Samples < BenchmarkOptions.CertificationSamples ||
                !Configuration.FreshWorldPerSample ||
                Configuration.QueryIterations != BenchmarkOptions.CertificationQueryIterations ||
                Configuration.StructuralIterations != BenchmarkOptions.CertificationStructuralIterations ||
                !string.Equals(
                    Configuration.PercentileMethod,
                    "R-7 linear interpolation over fresh samples",
                    StringComparison.Ordinal) ||
                !string.Equals(
                    Configuration.AllocatedBytesMetric,
                    "current managed thread",
                    StringComparison.Ordinal) ||
                !string.Equals(
                    Configuration.TotalAllocatedBytesMetric,
                    "all managed threads",
                    StringComparison.Ordinal))
            {
                throw new BenchmarkConfigurationException(
                    "The certification baseline workload is incompatible with the fixed " +
                    "certification configuration.");
            }

            var expected = new HashSet<string>(plannedScenarios, StringComparer.Ordinal);
            if (!expected.SetEquals(Results.Keys))
            {
                throw new BenchmarkConfigurationException(
                    "The certification baseline scenario set must exactly match the fixed workload.");
            }
            foreach ((string scenario, BaselineValues value) in Results)
            {
                if (value.SampleCount < BenchmarkOptions.CertificationSamples ||
                    value.WarmupCount < BenchmarkOptions.CertificationWarmupSamples ||
                    value.SampleCount != Configuration.Samples ||
                    value.WarmupCount != Configuration.WarmupSamples ||
                    !value.FreshWorldPerSample)
                {
                    throw new BenchmarkConfigurationException(
                        $"Baseline scenario '{scenario}' lacks the required fresh sample evidence.");
                }
            }
        }

        private static T DeserializeRequired<T>(JsonElement root, string property, string path)
        {
            if (!root.TryGetProperty(property, out JsonElement element) ||
                element.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException($"Baseline '{path}' has no {property} object.");
            }
            return JsonSerializer.Deserialize<T>(element.GetRawText(), EcsBenchmarkReport.JsonOptions)
                ?? throw new InvalidDataException(
                    $"Baseline '{path}' contains an invalid {property} object.");
        }

        private static double ReadPositiveFiniteDouble(
            JsonElement element,
            string property,
            string scenario)
        {
            if (!element.TryGetDouble(out double value) || !double.IsFinite(value) || value <= 0)
            {
                throw new InvalidDataException(
                    $"Baseline scenario '{scenario}' property '{property}' must be positive and finite.");
            }
            return value;
        }

        private static void ValidateRawSamples(
            JsonElement result,
            string scenario,
            int declaredSampleCount,
            MetricDistribution reportedElapsed,
            MetricDistribution reportedAllocated,
            MetricDistribution reportedTotalAllocated,
            MetricDistribution reportedWorkingSet,
            MetricDistribution reportedWorkingSetDelta,
            GarbageCollectionCounts reportedGarbageCollections,
            StructuralMetricAggregate reportedStructuralMetrics,
            BenchmarkWorkloadMetricAggregate reportedWorkloadMetrics,
            string reportedChecksum)
        {
            if (declaredSampleCount <= 0 ||
                !result.TryGetProperty("samples", out JsonElement samplesElement) ||
                samplesElement.ValueKind != JsonValueKind.Array ||
                samplesElement.GetArrayLength() != declaredSampleCount)
            {
                throw new InvalidDataException(
                    $"Baseline scenario '{scenario}' raw sample count does not match sampleCount.");
            }

            var elapsed = new double[declaredSampleCount];
            var allocatedBytes = new double[declaredSampleCount];
            var totalAllocatedBytes = new double[declaredSampleCount];
            var workingSetBytes = new double[declaredSampleCount];
            var workingSetDeltaBytes = new double[declaredSampleCount];
            var garbageCollections = new GarbageCollectionCounts[declaredSampleCount];
            var structuralMetrics = new StructuralMetricAggregate[declaredSampleCount];
            var workloadMetrics = new BenchmarkWorkloadMetricSample[declaredSampleCount];
            int index = 0;
            foreach (JsonElement sample in samplesElement.EnumerateArray())
            {
                EnsureExactProperties(sample, SampleProperties, scenario, $"sample {index + 1}");
                if (!sample.TryGetProperty("sample", out JsonElement ordinalElement) ||
                    !ordinalElement.TryGetInt32(out int ordinal) || ordinal != index + 1 ||
                    !sample.TryGetProperty("allocatedBytes", out JsonElement allocatedElement) ||
                    !allocatedElement.TryGetInt64(out long allocated) || allocated < 0 ||
                    !sample.TryGetProperty("totalAllocatedBytes", out JsonElement totalAllocatedElement) ||
                    !totalAllocatedElement.TryGetInt64(out long totalAllocated) || totalAllocated < 0 ||
                    !ReadNonNegativeInt64(sample, "managedMemoryBeforeBytes") ||
                    !ReadNonNegativeInt64(sample, "managedMemoryAfterBytes") ||
                    !sample.TryGetProperty("workingSetBeforeBytes", out JsonElement workingSetBeforeElement) ||
                    !workingSetBeforeElement.TryGetInt64(out long workingSetBefore) || workingSetBefore < 0 ||
                    !sample.TryGetProperty("workingSetAfterBytes", out JsonElement workingSetAfterElement) ||
                    !workingSetAfterElement.TryGetInt64(out long workingSetAfter) || workingSetAfter < 0 ||
                    !sample.TryGetProperty("workingSetDeltaBytes", out JsonElement workingSetDelta) ||
                    !workingSetDelta.TryGetInt64(out long workingSetDeltaValue) ||
                    workingSetDeltaValue != workingSetAfter - workingSetBefore ||
                    !sample.TryGetProperty("checksum", out JsonElement checksumElement) ||
                    checksumElement.ValueKind != JsonValueKind.String ||
                    !string.Equals(
                        checksumElement.GetString(),
                        reportedChecksum,
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"Baseline scenario '{scenario}' sample {index + 1} has invalid schema-5 " +
                        "ordinal, memory, allocation, or checksum fields.");
                }
                garbageCollections[index] =
                    ValidateGarbageCollections(sample, scenario, $"sample {index + 1}");
                if (!sample.TryGetProperty("elapsedMilliseconds", out JsonElement elapsedElement) ||
                    !elapsedElement.TryGetDouble(out double value) ||
                    !double.IsFinite(value) ||
                    value < 0)
                {
                    throw new InvalidDataException(
                        $"Baseline scenario '{scenario}' contains an invalid raw elapsed sample.");
                }
                elapsed[index] = value;
                allocatedBytes[index] = allocated;
                totalAllocatedBytes[index] = totalAllocated;
                workingSetBytes[index] = workingSetAfter;
                workingSetDeltaBytes[index] = workingSetDeltaValue;
                structuralMetrics[index] = ReadStructuralMetrics(
                    sample,
                    "structuralMetrics",
                    scenario,
                    $"sample {index + 1}");
                workloadMetrics[index] = ReadWorkloadMetricSample(
                    sample,
                    scenario,
                    $"sample {index + 1}");
                index++;
            }

            ValidateMetricDistribution(
                scenario,
                "elapsedMilliseconds",
                reportedElapsed,
                elapsed);
            ValidateMetricDistribution(
                scenario,
                "allocatedBytes",
                reportedAllocated,
                allocatedBytes);
            ValidateMetricDistribution(
                scenario,
                "totalAllocatedBytes",
                reportedTotalAllocated,
                totalAllocatedBytes);
            ValidateMetricDistribution(
                scenario,
                "workingSetBytes",
                reportedWorkingSet,
                workingSetBytes);
            ValidateMetricDistribution(
                scenario,
                "workingSetDeltaBytes",
                reportedWorkingSetDelta,
                workingSetDeltaBytes);
            ValidateGarbageCollectionAggregate(
                scenario,
                reportedGarbageCollections,
                garbageCollections);

            ValidateStructuralAggregate(
                scenario,
                reportedStructuralMetrics,
                structuralMetrics);
            ValidateWorkloadAggregate(
                scenario,
                reportedWorkloadMetrics,
                workloadMetrics);
        }

        private static MetricDistribution ValidateDistribution(
            JsonElement parent,
            string property,
            string scenario,
            bool allowNegative = false)
        {
            if (!parent.TryGetProperty(property, out JsonElement distribution) ||
                distribution.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException(
                    $"Baseline scenario '{scenario}' has no {property} distribution.");
            }
            EnsureExactProperties(distribution, DistributionProperties, scenario, property);
            double p50 = ReadDistributionValue(distribution, property, "p50", scenario, allowNegative);
            double p95 = ReadDistributionValue(distribution, property, "p95", scenario, allowNegative);
            double p99 = ReadDistributionValue(distribution, property, "p99", scenario, allowNegative);
            double max = ReadDistributionValue(distribution, property, "max", scenario, allowNegative);
            if (p50 > p95 || p95 > p99 || p99 > max)
            {
                throw new InvalidDataException(
                    $"Baseline scenario '{scenario}' {property} distribution is not monotonic.");
            }
            return new MetricDistribution(p50, p95, p99, max);
        }

        private static double ReadDistributionValue(
            JsonElement distribution,
            string property,
            string metric,
            string scenario,
            bool allowNegative)
        {
            if (!distribution.TryGetProperty(metric, out JsonElement element) ||
                !element.TryGetDouble(out double value) ||
                !double.IsFinite(value) ||
                (!allowNegative && value < 0))
            {
                throw new InvalidDataException(
                    $"Baseline scenario '{scenario}' {property}.{metric} is invalid.");
            }
            return value;
        }

        private static GarbageCollectionCounts ValidateGarbageCollections(
            JsonElement parent,
            string scenario,
            string location)
        {
            if (!parent.TryGetProperty("garbageCollections", out JsonElement collections) ||
                collections.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException(
                    $"Baseline scenario '{scenario}' {location} has no garbageCollections object.");
            }
            EnsureExactProperties(
                collections,
                GarbageCollectionProperties,
                scenario,
                $"{location} garbageCollections");
            int generation0 = ReadGarbageCollectionCount(
                collections, "generation0", scenario, location);
            int generation1 = ReadGarbageCollectionCount(
                collections, "generation1", scenario, location);
            int generation2 = ReadGarbageCollectionCount(
                collections, "generation2", scenario, location);
            return new GarbageCollectionCounts(generation0, generation1, generation2);
        }

        private static int ReadGarbageCollectionCount(
            JsonElement collections,
            string generation,
            string scenario,
            string location)
        {
            if (!collections.TryGetProperty(generation, out JsonElement value) ||
                !value.TryGetInt32(out int count) || count < 0)
            {
                throw new InvalidDataException(
                    $"Baseline scenario '{scenario}' {location} {generation} is invalid.");
            }
            return count;
        }

        private static void ValidateGarbageCollectionAggregate(
            string scenario,
            GarbageCollectionCounts reported,
            GarbageCollectionCounts[] samples)
        {
            long generation0 = 0;
            long generation1 = 0;
            long generation2 = 0;
            foreach (GarbageCollectionCounts sample in samples)
            {
                generation0 += sample.Generation0;
                generation1 += sample.Generation1;
                generation2 += sample.Generation2;
            }

            if (generation0 != reported.Generation0 ||
                generation1 != reported.Generation1 ||
                generation2 != reported.Generation2)
            {
                throw new InvalidDataException(
                    $"Baseline scenario '{scenario}' aggregate garbageCollections " +
                    "do not match its raw samples.");
            }
        }

        private static bool ReadNonNegativeInt64(JsonElement parent, string property) =>
            parent.TryGetProperty(property, out JsonElement element) &&
            element.TryGetInt64(out long value) &&
            value >= 0;

        private static StructuralMetricAggregate ReadStructuralMetrics(
            JsonElement parent,
            string property,
            string scenario,
            string location)
        {
            if (!parent.TryGetProperty(property, out JsonElement metrics) ||
                metrics.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException(
                    $"Baseline scenario '{scenario}' {location} has no structuralMetrics object.");
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty metric in metrics.EnumerateObject())
            {
                if (!StructuralMetricProperties.Contains(metric.Name) || !seen.Add(metric.Name))
                {
                    throw new InvalidDataException(
                        $"Baseline scenario '{scenario}' {location} structuralMetrics has an " +
                        $"unknown or duplicate property '{metric.Name}'.");
                }
            }
            if (!seen.SetEquals(StructuralMetricProperties))
            {
                throw new InvalidDataException(
                    $"Baseline scenario '{scenario}' {location} structuralMetrics does not " +
                    "contain the exact schema-5 structural metric set.");
            }

            return new StructuralMetricAggregate(
                ReadNonNegativeInt64(metrics, "started", scenario, location),
                ReadNonNegativeInt64(metrics, "published", scenario, location),
                ReadNonNegativeInt64(metrics, "aborted", scenario, location),
                ReadNonNegativeFiniteDouble(metrics, "prepareMilliseconds", scenario, location),
                ReadNonNegativeFiniteDouble(metrics, "commitMilliseconds", scenario, location),
                ReadNonNegativeFiniteDouble(metrics, "lifetimeMilliseconds", scenario, location),
                ReadNonNegativeFiniteDouble(metrics, "worldMaximumPrepareMilliseconds", scenario, location),
                ReadNonNegativeFiniteDouble(metrics, "worldMaximumCommitMilliseconds", scenario, location),
                ReadNonNegativeFiniteDouble(metrics, "worldMaximumLifetimeMilliseconds", scenario, location),
                ReadNonNegativeInt64(metrics, "clonedArchetypeShells", scenario, location),
                ReadNonNegativeInt64(metrics, "worldMaximumClonedArchetypeShells", scenario, location),
                ReadNonNegativeInt64(metrics, "clonedChunkShells", scenario, location),
                ReadNonNegativeInt64(metrics, "worldMaximumClonedChunkShells", scenario, location),
                ReadNonNegativeInt64(metrics, "clonedQueryMatches", scenario, location),
                ReadNonNegativeInt64(metrics, "worldMaximumClonedQueryMatches", scenario, location));
        }

        private static BenchmarkWorkloadMetricAggregate ReadWorkloadMetricAggregate(
            JsonElement parent,
            string scenario)
        {
            if (!parent.TryGetProperty("workloadMetrics", out JsonElement metrics) ||
                metrics.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException(
                    $"Baseline scenario '{scenario}' aggregate has no workloadMetrics object.");
            }
            EnsureExactProperties(metrics, WorkloadMetricProperties, scenario, "aggregate workloadMetrics");
            return new BenchmarkWorkloadMetricAggregate(
                ReadMetricDistribution(metrics, "payloadBytes", scenario),
                ReadMetricDistribution(metrics, "updateMilliseconds", scenario),
                ReadMetricDistribution(metrics, "snapshotWriteMilliseconds", scenario),
                ReadMetricDistribution(metrics, "loadMilliseconds", scenario),
                ReadMetricDistribution(metrics, "durableCommitMilliseconds", scenario),
                ReadMetricDistribution(metrics, "durableLoadMilliseconds", scenario));
        }

        private static BenchmarkWorkloadMetricSample ReadWorkloadMetricSample(
            JsonElement parent,
            string scenario,
            string location)
        {
            if (!parent.TryGetProperty("workloadMetrics", out JsonElement metrics) ||
                metrics.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException(
                    $"Baseline scenario '{scenario}' {location} has no workloadMetrics object.");
            }
            EnsureExactProperties(metrics, WorkloadMetricProperties, scenario, $"{location} workloadMetrics");
            return new BenchmarkWorkloadMetricSample(
                ReadNonNegativeInt64(metrics, "payloadBytes", scenario, location),
                ReadNonNegativeFiniteDouble(metrics, "updateMilliseconds", scenario, location),
                ReadNonNegativeFiniteDouble(metrics, "snapshotWriteMilliseconds", scenario, location),
                ReadNonNegativeFiniteDouble(metrics, "loadMilliseconds", scenario, location),
                ReadNonNegativeFiniteDouble(metrics, "durableCommitMilliseconds", scenario, location),
                ReadNonNegativeFiniteDouble(metrics, "durableLoadMilliseconds", scenario, location));
        }

        private static MetricDistribution ReadMetricDistribution(
            JsonElement metrics,
            string property,
            string scenario)
        {
            if (!metrics.TryGetProperty(property, out JsonElement distribution) ||
                distribution.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException(
                    $"Baseline scenario '{scenario}' workload metric '{property}' has no distribution.");
            }
            EnsureExactProperties(
                distribution,
                DistributionProperties,
                scenario,
                $"workloadMetrics.{property}");
            return new MetricDistribution(
                ReadNonNegativeFiniteDouble(distribution, "p50", scenario, property),
                ReadNonNegativeFiniteDouble(distribution, "p95", scenario, property),
                ReadNonNegativeFiniteDouble(distribution, "p99", scenario, property),
                ReadNonNegativeFiniteDouble(distribution, "max", scenario, property));
        }

        private static void EnsureExactProperties(
            JsonElement element,
            HashSet<string> expected,
            string scenario,
            string location)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (!expected.Contains(property.Name) || !seen.Add(property.Name))
                {
                    throw new InvalidDataException(
                        $"Baseline scenario '{scenario}' {location} has an unknown or duplicate " +
                        $"property '{property.Name}'.");
                }
            }
            if (!seen.SetEquals(expected))
            {
                throw new InvalidDataException(
                    $"Baseline scenario '{scenario}' {location} does not contain the exact schema-5 property set.");
            }
        }

        private static void EnsureRequiredProperties(
            JsonElement element,
            HashSet<string> allowed,
            HashSet<string> required,
            string description)
        {
            if (element.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException($"{description} must contain an object.");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (!allowed.Contains(property.Name) || !seen.Add(property.Name))
                {
                    throw new InvalidDataException(
                        $"{description} has unknown or duplicate property '{property.Name}'.");
                }
            }
            if (!required.IsSubsetOf(seen))
                throw new InvalidDataException($"{description} is missing required schema-5 properties.");
        }

        private static long ReadNonNegativeInt64(
            JsonElement metrics,
            string property,
            string scenario,
            string location)
        {
            if (!metrics.TryGetProperty(property, out JsonElement element) ||
                !element.TryGetInt64(out long value) ||
                value < 0)
            {
                throw new InvalidDataException(
                    $"Baseline scenario '{scenario}' {location} structural metric '{property}' " +
                    "must be a non-negative Int64.");
            }
            return value;
        }

        private static double ReadNonNegativeFiniteDouble(
            JsonElement metrics,
            string property,
            string scenario,
            string location)
        {
            if (!metrics.TryGetProperty(property, out JsonElement element) ||
                !element.TryGetDouble(out double value) ||
                !double.IsFinite(value) ||
                value < 0)
            {
                throw new InvalidDataException(
                    $"Baseline scenario '{scenario}' {location} structural metric '{property}' " +
                    "must be non-negative and finite.");
            }
            return value;
        }

        private static void ValidateStructuralAggregate(
            string scenario,
            StructuralMetricAggregate reported,
            IReadOnlyList<StructuralMetricAggregate> samples)
        {
            long started = 0;
            long published = 0;
            long aborted = 0;
            double prepareMilliseconds = 0;
            double commitMilliseconds = 0;
            double lifetimeMilliseconds = 0;
            double maximumPrepareMilliseconds = 0;
            double maximumCommitMilliseconds = 0;
            double maximumLifetimeMilliseconds = 0;
            long clonedArchetypeShells = 0;
            long maximumClonedArchetypeShells = 0;
            long clonedChunkShells = 0;
            long maximumClonedChunkShells = 0;
            long clonedQueryMatches = 0;
            long maximumClonedQueryMatches = 0;

            try
            {
                checked
                {
                    for (int i = 0; i < samples.Count; i++)
                    {
                        StructuralMetricAggregate sample = samples[i];
                        started += sample.Started;
                        published += sample.Published;
                        aborted += sample.Aborted;
                        prepareMilliseconds += sample.PrepareMilliseconds;
                        commitMilliseconds += sample.CommitMilliseconds;
                        lifetimeMilliseconds += sample.LifetimeMilliseconds;
                        maximumPrepareMilliseconds = Math.Max(
                            maximumPrepareMilliseconds,
                            sample.WorldMaximumPrepareMilliseconds);
                        maximumCommitMilliseconds = Math.Max(
                            maximumCommitMilliseconds,
                            sample.WorldMaximumCommitMilliseconds);
                        maximumLifetimeMilliseconds = Math.Max(
                            maximumLifetimeMilliseconds,
                            sample.WorldMaximumLifetimeMilliseconds);
                        clonedArchetypeShells += sample.ClonedArchetypeShells;
                        maximumClonedArchetypeShells = Math.Max(
                            maximumClonedArchetypeShells,
                            sample.WorldMaximumClonedArchetypeShells);
                        clonedChunkShells += sample.ClonedChunkShells;
                        maximumClonedChunkShells = Math.Max(
                            maximumClonedChunkShells,
                            sample.WorldMaximumClonedChunkShells);
                        clonedQueryMatches += sample.ClonedQueryMatches;
                        maximumClonedQueryMatches = Math.Max(
                            maximumClonedQueryMatches,
                            sample.WorldMaximumClonedQueryMatches);
                    }
                }
            }
            catch (OverflowException exception)
            {
                throw new InvalidDataException(
                    $"Baseline scenario '{scenario}' structural metric totals overflow Int64.",
                    exception);
            }

            if (!double.IsFinite(prepareMilliseconds) ||
                !double.IsFinite(commitMilliseconds) ||
                !double.IsFinite(lifetimeMilliseconds))
            {
                throw new InvalidDataException(
                    $"Baseline scenario '{scenario}' structural metric time totals must remain finite.");
            }

            bool matches =
                reported.Started == started &&
                reported.Published == published &&
                reported.Aborted == aborted &&
                NearlyEqual(reported.PrepareMilliseconds, prepareMilliseconds) &&
                NearlyEqual(reported.CommitMilliseconds, commitMilliseconds) &&
                NearlyEqual(reported.LifetimeMilliseconds, lifetimeMilliseconds) &&
                NearlyEqual(reported.WorldMaximumPrepareMilliseconds, maximumPrepareMilliseconds) &&
                NearlyEqual(reported.WorldMaximumCommitMilliseconds, maximumCommitMilliseconds) &&
                NearlyEqual(reported.WorldMaximumLifetimeMilliseconds, maximumLifetimeMilliseconds) &&
                reported.ClonedArchetypeShells == clonedArchetypeShells &&
                reported.WorldMaximumClonedArchetypeShells == maximumClonedArchetypeShells &&
                reported.ClonedChunkShells == clonedChunkShells &&
                reported.WorldMaximumClonedChunkShells == maximumClonedChunkShells &&
                reported.ClonedQueryMatches == clonedQueryMatches &&
                reported.WorldMaximumClonedQueryMatches == maximumClonedQueryMatches;
            if (!matches)
            {
                throw new InvalidDataException(
                    $"Baseline scenario '{scenario}' aggregate structuralMetrics do not match " +
                    "the sums and world maxima of its raw samples.");
            }
        }

        private static void ValidateWorkloadAggregate(
            string scenario,
            BenchmarkWorkloadMetricAggregate reported,
            IReadOnlyList<BenchmarkWorkloadMetricSample> samples)
        {
            ValidateMetricDistribution(
                scenario,
                "payloadBytes",
                reported.PayloadBytes,
                samples.Select(static sample => (double)sample.PayloadBytes).ToArray(),
                "workload metric");
            ValidateMetricDistribution(
                scenario,
                "updateMilliseconds",
                reported.UpdateMilliseconds,
                samples.Select(static sample => sample.UpdateMilliseconds).ToArray(),
                "workload metric");
            ValidateMetricDistribution(
                scenario,
                "snapshotWriteMilliseconds",
                reported.SnapshotWriteMilliseconds,
                samples.Select(static sample => sample.SnapshotWriteMilliseconds).ToArray(),
                "workload metric");
            ValidateMetricDistribution(
                scenario,
                "loadMilliseconds",
                reported.LoadMilliseconds,
                samples.Select(static sample => sample.LoadMilliseconds).ToArray(),
                "workload metric");
            ValidateMetricDistribution(
                scenario,
                "durableCommitMilliseconds",
                reported.DurableCommitMilliseconds,
                samples.Select(static sample => sample.DurableCommitMilliseconds).ToArray(),
                "workload metric");
            ValidateMetricDistribution(
                scenario,
                "durableLoadMilliseconds",
                reported.DurableLoadMilliseconds,
                samples.Select(static sample => sample.DurableLoadMilliseconds).ToArray(),
                "workload metric");
        }

        private static void ValidateMetricDistribution(
            string scenario,
            string metric,
            MetricDistribution reported,
            double[] values,
            string category = "distribution")
        {
            Array.Sort(values);
            if (!NearlyEqual(reported.P50, Percentile(values, 0.50)) ||
                !NearlyEqual(reported.P95, Percentile(values, 0.95)) ||
                !NearlyEqual(reported.P99, Percentile(values, 0.99)) ||
                !NearlyEqual(reported.Max, values[^1]))
            {
                throw new InvalidDataException(
                    $"Baseline scenario '{scenario}' aggregate {category} '{metric}' " +
                    "does not match its raw samples.");
            }
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

        private static bool NearlyEqual(double left, double right)
        {
            if (!double.IsFinite(left) || !double.IsFinite(right))
                return false;
            return left.Equals(right);
        }
    }

    internal sealed record BaselineValues(
        double P50Milliseconds,
        double P99Milliseconds,
        int SampleCount,
        int WarmupCount,
        bool FreshWorldPerSample);

    internal sealed class AbsoluteBudgetCatalog
    {
        private static readonly HashSet<string> BudgetProperties = new(StringComparer.Ordinal)
        {
            "maxP50Milliseconds",
            "maxP95Milliseconds",
            "maxP99Milliseconds",
            "maxMilliseconds",
            "maxAllocatedBytesPerSample",
            "maxTotalAllocatedBytesPerSample",
            "maxWorkingSetBytes",
            "maxWorkingSetDeltaBytes",
        };

        private readonly AbsoluteBudget? _defaults;
        private readonly IReadOnlyDictionary<string, AbsoluteBudget> _scenarios;

        private AbsoluteBudgetCatalog(
            AbsoluteBudget? defaults,
            IReadOnlyDictionary<string, AbsoluteBudget> scenarios)
        {
            _defaults = defaults;
            _scenarios = scenarios;
        }

        internal AbsoluteBudget? Resolve(string scenario)
        {
            _scenarios.TryGetValue(scenario, out AbsoluteBudget? specific);
            AbsoluteBudget? resolved = AbsoluteBudget.Merge(_defaults, specific);
            return resolved is { HasAnyValue: true } ? resolved : null;
        }

        internal static AbsoluteBudgetCatalog Load(string path)
        {
            string fullPath = Path.GetFullPath(path);
            return Load(File.ReadAllBytes(fullPath), fullPath);
        }

        internal static AbsoluteBudgetCatalog Load(ReadOnlyMemory<byte> json, string path)
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException($"Absolute budget file '{path}' must contain an object.");
            if (!root.TryGetProperty("schemaVersion", out JsonElement schemaElement) ||
                !schemaElement.TryGetInt32(out int schemaVersion) ||
                schemaVersion != 1)
            {
                throw new InvalidDataException(
                    $"Absolute budget file '{path}' must use schemaVersion 1.");
            }
            foreach (JsonProperty property in root.EnumerateObject())
            {
                if (property.Name is not ("schemaVersion" or "defaults" or "scenarios"))
                {
                    throw new InvalidDataException(
                        $"Absolute budget file '{path}' has unknown property '{property.Name}'.");
                }
            }

            AbsoluteBudget? defaults = null;
            if (root.TryGetProperty("defaults", out JsonElement defaultsElement))
                defaults = ReadBudget(defaultsElement, "defaults");

            var scenarios = new Dictionary<string, AbsoluteBudget>(StringComparer.Ordinal);
            if (root.TryGetProperty("scenarios", out JsonElement scenariosElement))
            {
                if (scenariosElement.ValueKind != JsonValueKind.Object)
                    throw new InvalidDataException("Absolute budget 'scenarios' must be an object.");
                foreach (JsonProperty scenario in scenariosElement.EnumerateObject())
                    scenarios.Add(scenario.Name, ReadBudget(scenario.Value, $"scenarios.{scenario.Name}"));
            }

            return new AbsoluteBudgetCatalog(defaults, scenarios);
        }

        internal void ValidateCertificationCompatibility(
            IReadOnlyCollection<string> plannedScenarios)
        {
            var expected = new HashSet<string>(plannedScenarios, StringComparer.Ordinal);
            foreach (string scenario in _scenarios.Keys)
            {
                if (!expected.Contains(scenario))
                {
                    throw new BenchmarkConfigurationException(
                        $"Absolute budget scenario '{scenario}' is not in the fixed certification workload.");
                }
            }

            foreach (string scenario in expected)
            {
                AbsoluteBudget? budget = Resolve(scenario);
                if (budget?.MaxP50Milliseconds is null ||
                    budget.MaxP95Milliseconds is null ||
                    budget.MaxP99Milliseconds is null ||
                    budget.MaxMilliseconds is null)
                {
                    throw new BenchmarkConfigurationException(
                        $"Certification scenario '{scenario}' requires maxP50Milliseconds, " +
                        "maxP95Milliseconds, maxP99Milliseconds, and maxMilliseconds budgets.");
                }
            }
        }

        private static AbsoluteBudget ReadBudget(JsonElement element, string location)
        {
            if (element.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException($"Absolute budget '{location}' must be an object.");
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (!BudgetProperties.Contains(property.Name))
                {
                    throw new InvalidDataException(
                        $"Absolute budget '{location}' has unknown property '{property.Name}'.");
                }
            }

            return new AbsoluteBudget(
                ReadOptionalDouble(element, "maxP50Milliseconds", location),
                ReadOptionalDouble(element, "maxP95Milliseconds", location),
                ReadOptionalDouble(element, "maxP99Milliseconds", location),
                ReadOptionalDouble(element, "maxMilliseconds", location),
                ReadOptionalDouble(element, "maxAllocatedBytesPerSample", location),
                ReadOptionalDouble(element, "maxTotalAllocatedBytesPerSample", location),
                ReadOptionalDouble(element, "maxWorkingSetBytes", location),
                ReadOptionalDouble(element, "maxWorkingSetDeltaBytes", location));
        }

        private static double? ReadOptionalDouble(
            JsonElement element,
            string propertyName,
            string location)
        {
            if (!element.TryGetProperty(propertyName, out JsonElement property))
                return null;
            if (!property.TryGetDouble(out double value) || !double.IsFinite(value) || value < 0)
            {
                throw new InvalidDataException(
                    $"Absolute budget '{location}.{propertyName}' must be finite and non-negative.");
            }
            return value;
        }
    }
}

internal sealed record BenchmarkGateContext(
    BenchmarkGateEvaluator.BaselineCatalog? Baseline,
    BenchmarkGateEvaluator.AbsoluteBudgetCatalog? Budgets);

internal sealed class BenchmarkConfigurationException : Exception
{
    internal BenchmarkConfigurationException(string message)
        : base(message)
    {
    }
}

internal sealed record EcsBenchmarkGate(
    bool Passed,
    string? BaselinePath,
    string? AbsoluteBudgetsPath,
    double MaximumP50RegressionPercent,
    double MaximumP99RegressionPercent,
    EcsBenchmarkGateEvaluation[] Evaluations,
    string[] Violations);

internal sealed record EcsBenchmarkGateEvaluation(
    string Scenario,
    bool Passed,
    AbsoluteBudget? AbsoluteBudget,
    BaselineComparison? BaselineComparison,
    string[] Violations);

internal sealed record BaselineComparison(
    double BaselineP50Milliseconds,
    double BaselineP99Milliseconds,
    double CurrentP50Milliseconds,
    double CurrentP99Milliseconds,
    double P50RegressionPercent,
    double P99RegressionPercent);

internal sealed record AbsoluteBudget(
    double? MaxP50Milliseconds,
    double? MaxP95Milliseconds,
    double? MaxP99Milliseconds,
    double? MaxMilliseconds,
    double? MaxAllocatedBytesPerSample,
    double? MaxTotalAllocatedBytesPerSample,
    double? MaxWorkingSetBytes,
    double? MaxWorkingSetDeltaBytes)
{
    internal bool HasAnyValue =>
        MaxP50Milliseconds is not null ||
        MaxP95Milliseconds is not null ||
        MaxP99Milliseconds is not null ||
        MaxMilliseconds is not null ||
        MaxAllocatedBytesPerSample is not null ||
        MaxTotalAllocatedBytesPerSample is not null ||
        MaxWorkingSetBytes is not null ||
        MaxWorkingSetDeltaBytes is not null;

    internal static AbsoluteBudget? Merge(AbsoluteBudget? defaults, AbsoluteBudget? specific)
    {
        if (defaults is null)
            return specific;
        if (specific is null)
            return defaults;
        return new AbsoluteBudget(
            specific.MaxP50Milliseconds ?? defaults.MaxP50Milliseconds,
            specific.MaxP95Milliseconds ?? defaults.MaxP95Milliseconds,
            specific.MaxP99Milliseconds ?? defaults.MaxP99Milliseconds,
            specific.MaxMilliseconds ?? defaults.MaxMilliseconds,
            specific.MaxAllocatedBytesPerSample ?? defaults.MaxAllocatedBytesPerSample,
            specific.MaxTotalAllocatedBytesPerSample ?? defaults.MaxTotalAllocatedBytesPerSample,
            specific.MaxWorkingSetBytes ?? defaults.MaxWorkingSetBytes,
            specific.MaxWorkingSetDeltaBytes ?? defaults.MaxWorkingSetDeltaBytes);
    }
}
