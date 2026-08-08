namespace SomeEngine.ECS.Benchmarks.Tests;

public sealed class BenchmarkOptionsTests
{
    [Theory]
    [InlineData()]
    [InlineData("--baseline", "approved.json")]
    [InlineData("--absolute-budgets", "budgets.json")]
    [InlineData("--evidence-manifest", "evidence.json")]
    [InlineData("--baseline", "approved.json", "--absolute-budgets", "budgets.json")]
    public void CertificationRequiresAllPrerequisiteEvidenceInputs(params string[] additionalArguments)
    {
        string[] arguments = ["--profile", "certification", .. additionalArguments];

        bool parsed = BenchmarkOptions.TryParse(arguments, out BenchmarkOptions? options, out string? error);

        Assert.False(parsed);
        Assert.Null(options);
        Assert.Contains("requires --baseline, --absolute-budgets, and --evidence-manifest", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--entity-counts", "100k,500k")]
    [InlineData("--entity-counts", "1m,500k,100k")]
    [InlineData("--warmup", "2")]
    [InlineData("--samples", "99")]
    [InlineData("--query-iterations", "127")]
    [InlineData("--structural-iterations", "63")]
    public void CertificationFixedWorkloadCannotBeReducedOrReordered(string option, string value)
    {
        string[] arguments = CertificationArguments(option, value);

        bool parsed = BenchmarkOptions.TryParse(arguments, out BenchmarkOptions? options, out string? error);

        Assert.False(parsed);
        Assert.Null(options);
#if DEBUG
        Assert.Contains("Release configuration", error, StringComparison.Ordinal);
#else
        Assert.Contains("certification workload is fixed", error, StringComparison.OrdinalIgnoreCase);
#endif
    }

    [Theory]
    [InlineData("--max-p50-regression-percent", "5.0001")]
    [InlineData("--max-p99-regression-percent", "10.0001")]
    public void CertificationRegressionLimitsCannotBeRelaxed(string option, string value)
    {
        string[] arguments = CertificationArguments(option, value);

        bool parsed = BenchmarkOptions.TryParse(arguments, out BenchmarkOptions? options, out string? error);

        Assert.False(parsed);
        Assert.Null(options);
#if DEBUG
        Assert.Contains("Release configuration", error, StringComparison.Ordinal);
#else
        Assert.Contains("may be tightened but not relaxed", error, StringComparison.Ordinal);
#endif
    }

    [Theory]
    [InlineData("2147483648")]
    [InlineData("2147483648k")]
    [InlineData("999999999999999999999999999999999999999999999999999999m")]
    public void OversizedEntityCountFailsConfigurationWithoutThrowing(string entityCount)
    {
        bool parsed = true;
        BenchmarkOptions? options = null;
        string? error = null;

        Exception? exception = Record.Exception(
            () => parsed = BenchmarkOptions.TryParse(
                ["--entity-counts", entityCount],
                out options,
                out error));

        Assert.Null(exception);
        Assert.False(parsed);
        Assert.Null(options);
        Assert.Contains("not a positive whole number", error, StringComparison.Ordinal);
    }

    [Fact]
    public void CertificationOutputCannotOverwriteEvidenceManifest()
    {
        string[] arguments = CertificationArguments("--output", "evidence.json");

        bool parsed = BenchmarkOptions.TryParse(
            arguments,
            out BenchmarkOptions? options,
            out string? error);

        Assert.False(parsed);
        Assert.Null(options);
#if DEBUG
        Assert.Contains("Release configuration", error, StringComparison.Ordinal);
#else
        Assert.Contains("must not overwrite", error, StringComparison.OrdinalIgnoreCase);
#endif
    }

    private static string[] CertificationArguments(string option, string value) =>
    [
        "--profile", "certification",
        "--baseline", "approved.json",
        "--absolute-budgets", "budgets.json",
        "--evidence-manifest", "evidence.json",
        option, value,
    ];
}
