namespace SomeEngine.ECS.Benchmarks.Tests;

public sealed class BenchmarkOptionsTests
{




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



}
