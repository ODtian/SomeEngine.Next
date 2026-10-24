using System.Diagnostics;
using System.Text.Json;

namespace SomeEngine.ECS.Benchmarks.Tests;

public sealed class BenchmarkCommandTests
{
    [Fact]
    public async Task SuccessfulRunReturnsAReportOnStandardOutput()
    {
        var result = await Run(TinySmokeArguments());

        AssertExitCode(0, result);
        using var report = JsonDocument.Parse(result.StandardOutput);
        Assert.NotEmpty(report.RootElement.GetProperty("results").EnumerateArray());
        Assert.Equal("smoke", report.RootElement.GetProperty("configuration").GetProperty("profile").GetString());
    }

    [Fact]
    public async Task SuccessfulRunReplacesRequestedOutputWithTheReportedData()
    {
        using var directory = new TemporaryDirectory();
        string outputPath = Path.Combine(directory.Path, "report.json");
        await File.WriteAllTextAsync(outputPath, "previous report");

        var result = await Run([.. TinySmokeArguments(), "--output", outputPath]);

        AssertExitCode(0, result);
        Assert.Equal(result.StandardOutput, await File.ReadAllTextAsync(outputPath));
        Assert.Equal([outputPath], Directory.GetFiles(directory.Path));
    }

    [Fact]
    public async Task ZeroSamplesReturnsAConfigurationError()
    {
        var result = await Run("--samples", "0");

        AssertExitCode(2, result);
        Assert.Empty(result.StandardOutput);
    }

    [Theory]
    [InlineData("2147483648")]
    [InlineData("2147483648k")]
    [InlineData("999999999999999999999999999999999999999999999999999999m")]
    public async Task OversizedEntityCountReturnsAConfigurationError(string entityCount)
    {
        var result = await Run("--entity-counts", entityCount);

        AssertExitCode(2, result);
        Assert.Empty(result.StandardOutput);
    }

    [Fact]
    public async Task ReportPercentilesInterpolateBetweenMeasuredSamples()
    {
        var result = await Run([.. TinySmokeArguments(), "--samples", "10"]);

        AssertExitCode(0, result);
        using var report = JsonDocument.Parse(result.StandardOutput);
        var scenarios = report.RootElement.GetProperty("results").EnumerateArray().ToArray();
        Assert.NotEmpty(scenarios);
        foreach (var scenario in scenarios)
        {
            double[] samples = scenario.GetProperty("samples").EnumerateArray()
                .Select(sample => sample.GetProperty("elapsedMilliseconds").GetDouble())
                .Order().ToArray();
            Assert.Equal(10, samples.Length);
            var distribution = scenario.GetProperty("elapsedMilliseconds");
            Assert.Equal((samples[4] + samples[5]) / 2, distribution.GetProperty("p50").GetDouble(), 10);
            Assert.Equal(samples[8] * 0.45 + samples[9] * 0.55, distribution.GetProperty("p95").GetDouble(), 10);
            Assert.Equal(samples[8] * 0.09 + samples[9] * 0.91, distribution.GetProperty("p99").GetDouble(), 10);
            Assert.Equal(samples[9], distribution.GetProperty("max").GetDouble());
        }
    }

    private static string[] TinySmokeArguments() =>
    [
        "--profile", "smoke",
        "--entity-counts", "1",
        "--warmup", "0",
        "--samples", "1",
        "--query-iterations", "1",
        "--structural-iterations", "1",
    ];

    private static async Task<CommandResult> Run(params string[] arguments)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "SomeEngine.ECS.Benchmarks.dll"));
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            await process.WaitForExitAsync(limit.Token);
            return new CommandResult(process.ExitCode, await output, await errors);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
    }

    private static void AssertExitCode(int expected, CommandResult result) =>
        Assert.True(result.ExitCode == expected,
            $"Expected exit code {expected}, received {result.ExitCode}." +
            $"{Environment.NewLine}Standard output:{Environment.NewLine}{result.StandardOutput}" +
            $"{Environment.NewLine}Standard error:{Environment.NewLine}{result.StandardError}");

    private sealed record CommandResult(int ExitCode, string StandardOutput, string StandardError);

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "SomeEngine.ECS.Benchmarks-" + Guid.NewGuid().ToString("N"));

        public TemporaryDirectory() => Directory.CreateDirectory(Path);

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
