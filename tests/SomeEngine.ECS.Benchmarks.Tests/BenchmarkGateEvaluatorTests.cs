using System.Text.Json;
using System.Text.Json.Nodes;

namespace SomeEngine.ECS.Benchmarks.Tests;

public sealed class BenchmarkGateEvaluatorTests
{
    private const string Scenario = "bundle-spawn-100k";

    public static TheoryData<string> InvalidBaselineDocuments => new()
    {
        { "{}" },
        { "{\"schemaVersion\":1,\"passed\":true}" },
        { "{\"schemaVersion\":2,\"passed\":true}" },
        {
            """
            {
              "schemaVersion": 3,
              "passed": true,
              "environment": {},
              "configuration": {},
              "results": [
                {
                  "scenario": "bundle-spawn-100k",
                  "sampleCount": 100,
                  "warmupCount": 3,
                  "freshWorldPerSample": true,
                  "elapsedMilliseconds": { "p50": 1 }
                }
              ]
            }
            """
        },
    };

    public static TheoryData<string> InvalidBudgetDocuments => new()
    {
        { "{}" },
        { "{\"schemaVersion\":2}" },
        { "{\"schemaVersion\":1,\"unexpected\":{}}" },
        { "{\"schemaVersion\":1,\"defaults\":{\"unexpected\":1}}" },
        { "{\"schemaVersion\":1,\"defaults\":{\"maxMilliseconds\":-1}}" },
    };

    [Theory]
    [MemberData(nameof(InvalidBaselineDocuments))]
    public void BaselineSchemaErrorsFailClosed(string json)
    {
        using var directory = new TemporaryDirectory();
        string path = directory.Write("baseline.json", json);

        Assert.ThrowsAny<InvalidDataException>(
            () => BenchmarkGateEvaluator.BaselineCatalog.Load(path));
    }

    [Theory]
    [MemberData(nameof(InvalidBudgetDocuments))]
    public void AbsoluteBudgetSchemaErrorsFailClosed(string json)
    {
        using var directory = new TemporaryDirectory();
        string path = directory.Write("budgets.json", json);

        Assert.ThrowsAny<InvalidDataException>(
            () => BenchmarkGateEvaluator.AbsoluteBudgetCatalog.Load(path));
    }

    [Fact]
    public void BaselineSummaryMustMatchItsRawSamples()
    {
        using var directory = new TemporaryDirectory();
        string path = BenchmarkTestData.WriteBaseline(
            directory,
            BenchmarkTestData.ReleaseEnvironment(),
            BenchmarkTestData.CertificationConfiguration(),
            [Scenario],
            corruptRawSummary: true);

        InvalidDataException exception = Assert.Throws<InvalidDataException>(
            () => BenchmarkGateEvaluator.BaselineCatalog.Load(path));

        Assert.Contains("raw samples", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("elapsedMilliseconds", "p95", 5.0)]
    [InlineData("elapsedMilliseconds", "max", 405.0)]
    [InlineData("allocatedBytes", "max", 0.0000000005)]
    [InlineData("totalAllocatedBytes", "max", 1.0)]
    [InlineData("workingSetBytes", "max", 1.0)]
    [InlineData("workingSetDeltaBytes", "max", 1.0)]
    public void BaselinePrimaryDistributionsMustMatchRawSamples(
        string distributionName,
        string metricName,
        double replacement)
    {
        using var directory = new TemporaryDirectory();
        string validPath = BenchmarkTestData.WriteBaseline(
            directory,
            BenchmarkTestData.ReleaseEnvironment(),
            BenchmarkTestData.CertificationConfiguration(),
            [Scenario]);
        var root = Assert.IsType<JsonObject>(JsonNode.Parse(File.ReadAllText(validPath)));
        var result = Assert.IsType<JsonObject>(Assert.IsType<JsonArray>(root["results"])[0]);
        var distribution = Assert.IsType<JsonObject>(result[distributionName]);
        distribution[metricName] = replacement;
        string invalidPath = directory.Write(
            $"invalid-{distributionName}-{metricName}-baseline.json",
            root.ToJsonString());

        InvalidDataException exception = Assert.Throws<InvalidDataException>(
            () => BenchmarkGateEvaluator.BaselineCatalog.Load(invalidPath));

        Assert.Contains("raw samples", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BaselineWorkingSetDeltaMustMatchBeforeAndAfterValues()
    {
        using var directory = new TemporaryDirectory();
        string validPath = BenchmarkTestData.WriteBaseline(
            directory,
            BenchmarkTestData.ReleaseEnvironment(),
            BenchmarkTestData.CertificationConfiguration(),
            [Scenario]);
        var root = Assert.IsType<JsonObject>(JsonNode.Parse(File.ReadAllText(validPath)));
        var result = Assert.IsType<JsonObject>(Assert.IsType<JsonArray>(root["results"])[0]);
        var sample = Assert.IsType<JsonObject>(Assert.IsType<JsonArray>(result["samples"])[0]);
        sample["workingSetDeltaBytes"] = 1;
        string invalidPath = directory.Write("invalid-working-set-delta-baseline.json", root.ToJsonString());

        InvalidDataException exception = Assert.Throws<InvalidDataException>(
            () => BenchmarkGateEvaluator.BaselineCatalog.Load(invalidPath));

        Assert.Contains("memory", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BaselineGarbageCollectionAggregateMustMatchRawSamples()
    {
        using var directory = new TemporaryDirectory();
        string validPath = BenchmarkTestData.WriteBaseline(
            directory,
            BenchmarkTestData.ReleaseEnvironment(),
            BenchmarkTestData.CertificationConfiguration(),
            [Scenario]);
        var root = Assert.IsType<JsonObject>(JsonNode.Parse(File.ReadAllText(validPath)));
        var result = Assert.IsType<JsonObject>(Assert.IsType<JsonArray>(root["results"])[0]);
        var collections = Assert.IsType<JsonObject>(result["garbageCollections"]);
        collections["generation0"] = 1;
        string invalidPath = directory.Write("invalid-gc-baseline.json", root.ToJsonString());

        InvalidDataException exception = Assert.Throws<InvalidDataException>(
            () => BenchmarkGateEvaluator.BaselineCatalog.Load(invalidPath));

        Assert.Contains("raw samples", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("missing-aggregate")]
    [InlineData("missing-sample-field")]
    [InlineData("negative-sample")]
    [InlineData("aggregate-total-mismatch")]
    [InlineData("aggregate-maximum-mismatch")]
    [InlineData("unknown-sample-field")]
    [InlineData("overflowing-time-total")]
    public void BaselineStructuralEvidenceMustBeExactAndMatchRawSamples(string mutation)
    {
        using var directory = new TemporaryDirectory();
        string validPath = BenchmarkTestData.WriteBaseline(
            directory,
            BenchmarkTestData.ReleaseEnvironment(),
            BenchmarkTestData.CertificationConfiguration(),
            [Scenario]);
        var root = Assert.IsType<JsonObject>(JsonNode.Parse(File.ReadAllText(validPath)));
        var results = Assert.IsType<JsonArray>(root["results"]);
        var result = Assert.IsType<JsonObject>(results[0]);
        var aggregate = Assert.IsType<JsonObject>(result["structuralMetrics"]);
        var samples = Assert.IsType<JsonArray>(result["samples"]);
        var sample = Assert.IsType<JsonObject>(samples[0]);
        var sampleMetrics = Assert.IsType<JsonObject>(sample["structuralMetrics"]);

        switch (mutation)
        {
            case "missing-aggregate":
                result.Remove("structuralMetrics");
                break;
            case "missing-sample-field":
                sampleMetrics.Remove("clonedArchetypeShells");
                break;
            case "negative-sample":
                sampleMetrics["clonedChunkShells"] = -1;
                break;
            case "aggregate-total-mismatch":
                aggregate["clonedQueryMatches"] = 1;
                break;
            case "aggregate-maximum-mismatch":
                aggregate["worldMaximumClonedArchetypeShells"] = 1;
                break;
            case "unknown-sample-field":
                sampleMetrics["unexpected"] = 0;
                break;
            case "overflowing-time-total":
                foreach (JsonNode? sampleNode in samples)
                {
                    var sampleObject = Assert.IsType<JsonObject>(sampleNode);
                    var metricsObject = Assert.IsType<JsonObject>(sampleObject["structuralMetrics"]);
                    metricsObject["prepareMilliseconds"] = double.MaxValue;
                }
                aggregate["prepareMilliseconds"] = 0;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation), mutation, null);
        }

        string invalidPath = directory.Write(
            "invalid-structural-baseline.json",
            root.ToJsonString());
        InvalidDataException exception = Assert.Throws<InvalidDataException>(
            () => BenchmarkGateEvaluator.BaselineCatalog.Load(invalidPath));

        Assert.Contains("structural", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BaselineWorkloadMetricAggregateMustMatchRawSamples()
    {
        using var directory = new TemporaryDirectory();
        string validPath = BenchmarkTestData.WriteBaseline(
            directory,
            BenchmarkTestData.ReleaseEnvironment(),
            BenchmarkTestData.CertificationConfiguration(),
            [Scenario]);
        var root = Assert.IsType<JsonObject>(JsonNode.Parse(File.ReadAllText(validPath)));
        var result = Assert.IsType<JsonObject>(Assert.IsType<JsonArray>(root["results"])[0]);
        var sample = Assert.IsType<JsonObject>(Assert.IsType<JsonArray>(result["samples"])[0]);
        var workload = Assert.IsType<JsonObject>(sample["workloadMetrics"]);
        workload["payloadBytes"] = 1;
        string invalidPath = directory.Write("invalid-workload-baseline.json", root.ToJsonString());

        InvalidDataException exception = Assert.Throws<InvalidDataException>(
            () => BenchmarkGateEvaluator.BaselineCatalog.Load(invalidPath));

        Assert.Contains("workload metric", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("raw samples", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("failed")]
    [InlineData("violations")]
    [InlineData("scenario-mismatch")]
    public void BaselineGateEvidenceMustBeCompleteAndConsistent(string mutation)
    {
        using var directory = new TemporaryDirectory();
        string validPath = BenchmarkTestData.WriteBaseline(
            directory,
            BenchmarkTestData.ReleaseEnvironment(),
            BenchmarkTestData.CertificationConfiguration(),
            [Scenario]);
        var root = Assert.IsType<JsonObject>(JsonNode.Parse(File.ReadAllText(validPath)));
        var gate = Assert.IsType<JsonObject>(root["gate"]);
        switch (mutation)
        {
            case "missing":
                root.Remove("gate");
                break;
            case "failed":
                gate["passed"] = false;
                break;
            case "violations":
                gate["violations"] = new JsonArray(JsonValue.Create("edited gate"));
                break;
            case "scenario-mismatch":
                var evaluations = Assert.IsType<JsonArray>(gate["evaluations"]);
                var evaluation = Assert.IsType<JsonObject>(evaluations[0]);
                evaluation["scenario"] = "not-the-result-scenario";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation), mutation, null);
        }
        string invalidPath = directory.Write("invalid-gate-baseline.json", root.ToJsonString());

        Assert.Throws<InvalidDataException>(
            () => BenchmarkGateEvaluator.BaselineCatalog.Load(invalidPath));
    }

    [Fact]
    public void CertificationRejectsEnvironmentMismatch()
    {
        EcsBenchmarkEnvironment current = BenchmarkTestData.ReleaseEnvironment();
        EcsBenchmarkEnvironment approved = current with { MachineName = current.MachineName + "-other" };

        BenchmarkConfigurationException exception = PrepareCertification(
            approved,
            BenchmarkTestData.CertificationConfiguration(),
            [Scenario],
            [Scenario]);

        Assert.Contains("environment does not exactly match", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CertificationRejectsConfigurationMismatch()
    {
        EcsBenchmarkConfiguration incompatible =
            BenchmarkTestData.CertificationConfiguration() with { Samples = 99 };

        BenchmarkConfigurationException exception = PrepareCertification(
            BenchmarkTestData.ReleaseEnvironment(),
            incompatible,
            [Scenario],
            [Scenario]);

        Assert.Contains("workload is incompatible", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CertificationRejectsBaselineScenarioSetMismatch()
    {
        BenchmarkConfigurationException exception = PrepareCertification(
            BenchmarkTestData.ReleaseEnvironment(),
            BenchmarkTestData.CertificationConfiguration(),
            ["read-query-100k-x128"],
            [Scenario]);

        Assert.Contains("scenario set must exactly match", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CertificationRejectsUnknownBudgetScenario()
    {
        using var directory = new TemporaryDirectory();
        EcsBenchmarkEnvironment environment = BenchmarkTestData.ReleaseEnvironment();
        string baselinePath = BenchmarkTestData.WriteBaseline(
            directory,
            environment,
            BenchmarkTestData.CertificationConfiguration(),
            [Scenario]);
        string budgetPath = BenchmarkTestData.WriteBudget(
            directory,
            scenarioBudgets: new Dictionary<string, object?>
            {
                ["not-in-fixed-workload"] = new { maxMilliseconds = 1.0 },
            });
        BenchmarkOptions options = BenchmarkTestData.CertificationOptions(baselinePath, budgetPath);

        BenchmarkConfigurationException exception = Assert.Throws<BenchmarkConfigurationException>(
            () => BenchmarkGateEvaluator.Prepare(options, environment, [Scenario]));

        Assert.Contains("not in the fixed certification workload", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AbsoluteThresholdEqualityPasses()
    {
        using var directory = new TemporaryDirectory();
        string budgetPath = BenchmarkTestData.WriteBudget(
            directory,
            defaults: BenchmarkTestData.EqualityBudget());
        BenchmarkOptions options = BenchmarkTestData.SmokeOptions(absoluteBudgetsPath: budgetPath);
        BenchmarkGateContext context = BenchmarkGateEvaluator.Prepare(
            options,
            BenchmarkTestData.ReleaseEnvironment(),
            [Scenario]);

        EcsBenchmarkGate gate = BenchmarkGateEvaluator.Evaluate(
            options,
            [BenchmarkTestData.ResultAtAbsoluteThresholds(Scenario)],
            context);

        Assert.True(gate.Passed, string.Join(Environment.NewLine, gate.Violations));
        Assert.Empty(gate.Violations);
    }

    [Theory]
    [InlineData("p50")]
    [InlineData("p95")]
    [InlineData("p99")]
    [InlineData("maximum")]
    [InlineData("allocated")]
    [InlineData("total-allocated")]
    [InlineData("working-set")]
    [InlineData("working-set-delta")]
    public void AnyAbsoluteThresholdExcessFails(string metric)
    {
        using var directory = new TemporaryDirectory();
        string budgetPath = BenchmarkTestData.WriteBudget(
            directory,
            defaults: BenchmarkTestData.EqualityBudget());
        BenchmarkOptions options = BenchmarkTestData.SmokeOptions(absoluteBudgetsPath: budgetPath);
        BenchmarkGateContext context = BenchmarkGateEvaluator.Prepare(
            options,
            BenchmarkTestData.ReleaseEnvironment(),
            [Scenario]);
        EcsBenchmarkResult result = BenchmarkTestData.ExceedAbsoluteThreshold(
            BenchmarkTestData.ResultAtAbsoluteThresholds(Scenario),
            metric);

        EcsBenchmarkGate gate = BenchmarkGateEvaluator.Evaluate(options, [result], context);

        Assert.False(gate.Passed);
        Assert.NotEmpty(gate.Violations);
    }

    [Fact]
    public void RelativeThresholdEqualityPasses()
    {
        using var directory = new TemporaryDirectory();
        string baselinePath = BenchmarkTestData.WriteBaseline(
            directory,
            BenchmarkTestData.ReleaseEnvironment(),
            BenchmarkTestData.CertificationConfiguration(),
            [Scenario],
            p50: 4.0,
            p99: 8.0);
        BenchmarkOptions options = BenchmarkTestData.SmokeOptions(
            baselinePath: baselinePath,
            maximumP50RegressionPercent: 25.0,
            maximumP99RegressionPercent: 25.0);
        BenchmarkGateContext context = BenchmarkGateEvaluator.Prepare(
            options,
            BenchmarkTestData.ReleaseEnvironment(),
            [Scenario]);
        EcsBenchmarkResult result = BenchmarkTestData.Result(
            Scenario,
            elapsed: new MetricDistribution(5.0, 7.0, 10.0, 10.0));

        EcsBenchmarkGate gate = BenchmarkGateEvaluator.Evaluate(options, [result], context);

        Assert.True(gate.Passed, string.Join(Environment.NewLine, gate.Violations));
    }

    [Theory]
    [InlineData(5.01, 10.0)]
    [InlineData(5.0, 10.01)]
    public void RelativeThresholdExcessFails(double p50, double p99)
    {
        using var directory = new TemporaryDirectory();
        string baselinePath = BenchmarkTestData.WriteBaseline(
            directory,
            BenchmarkTestData.ReleaseEnvironment(),
            BenchmarkTestData.CertificationConfiguration(),
            [Scenario],
            p50: 4.0,
            p99: 8.0);
        BenchmarkOptions options = BenchmarkTestData.SmokeOptions(
            baselinePath: baselinePath,
            maximumP50RegressionPercent: 25.0,
            maximumP99RegressionPercent: 25.0);
        BenchmarkGateContext context = BenchmarkGateEvaluator.Prepare(
            options,
            BenchmarkTestData.ReleaseEnvironment(),
            [Scenario]);
        EcsBenchmarkResult result = BenchmarkTestData.Result(
            Scenario,
            elapsed: new MetricDistribution(p50, p50, p99, p99));

        EcsBenchmarkGate gate = BenchmarkGateEvaluator.Evaluate(options, [result], context);

        Assert.False(gate.Passed);
        Assert.NotEmpty(gate.Violations);
    }

    private static BenchmarkConfigurationException PrepareCertification(
        EcsBenchmarkEnvironment baselineEnvironment,
        EcsBenchmarkConfiguration baselineConfiguration,
        string[] baselineScenarios,
        string[] plannedScenarios)
    {
        using var directory = new TemporaryDirectory();
        string baselinePath = BenchmarkTestData.WriteBaseline(
            directory,
            baselineEnvironment,
            baselineConfiguration,
            baselineScenarios);
        string budgetPath = BenchmarkTestData.WriteBudget(directory);
        BenchmarkOptions options = BenchmarkTestData.CertificationOptions(baselinePath, budgetPath);

        return Assert.Throws<BenchmarkConfigurationException>(
            () => BenchmarkGateEvaluator.Prepare(
                options,
                BenchmarkTestData.ReleaseEnvironment(),
                plannedScenarios));
    }
}
