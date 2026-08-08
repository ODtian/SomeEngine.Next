namespace SomeEngine.ECS.Benchmarks.Tests;

public sealed class ScenarioCatalogTests
{
    [Fact]
    public void SmokeScenarioCatalog_IsExactAndVersioned()
    {
        string[] scenarios = EcsBenchmarkSuite.CreatePlannedScenarioNames(
            BenchmarkTestData.SmokeOptions());

        Assert.Equal(5, EcsBenchmarkSuite.ReportSchemaVersion);
        Assert.Equal(
        [
            "bundle-spawn-100k",
            "read-query-100k-x8",
            "structural-candidate-100k-x8",
            "parallel-integrate-100k",
            "changed-enabled-filter-100k-x8",
            "storage-owners-100k",
            "relation-maintenance-100k-fanout4096",
            "hierarchy-maintenance-100k-depth4096",
            "command-buffer-churn-100k-w256-x8",
            "snapshot-write-100k",
            "snapshot-read-100k",
            "mixed-frame-update-snapshot-load-100k",
            "durable-save-roundtrip-100k",
        ], scenarios);
    }

    [Fact]
    public void CertificationScenarioCatalog_CannotSilentlyDropAWorkloadOrEntityScale()
    {
        using var directory = new TemporaryDirectory();
        BenchmarkOptions options = BenchmarkTestData.CertificationOptions(
            directory.Write("baseline.json", "{}"),
            directory.Write("budgets.json", "{}"));

        string[] scenarios = EcsBenchmarkSuite.CreatePlannedScenarioNames(options);

        Assert.Equal(37, scenarios.Length);
        Assert.Equal(37, scenarios.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(13, scenarios.Count(name => name.Contains("100k", StringComparison.Ordinal)));
        Assert.Equal(12, scenarios.Count(name => name.Contains("500k", StringComparison.Ordinal)));
        Assert.Equal(12, scenarios.Count(name => name.Contains("1m", StringComparison.Ordinal)));
        Assert.Single(scenarios, static name => name.StartsWith("durable-save-roundtrip-", StringComparison.Ordinal));
    }
}
