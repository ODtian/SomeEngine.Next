using System.Globalization;
using System.Text.Json;

namespace SomeEngine.ECS.Benchmarks.Tests;

public sealed class ProgramTests
{
    [Fact]
    public void SuccessfulRunReturnsZero()
    {
        ProgramResult result = InvokeProgram(TinySmokeArguments());

        AssertExitCode(0, result);
        AssertStructuralCloneEvidence(result.StandardOutput);
        AssertSerializationWorkloadEvidence(result.StandardOutput);
    }

    [Fact]
    public void SuccessfulRunAtomicallyReplacesRequestedOutput()
    {
        using var directory = new TemporaryDirectory();
        string outputPath = directory.Write("report.json", "previous report");
        string[] arguments = [.. TinySmokeArguments(), "--output", outputPath];

        ProgramResult result = InvokeProgram(arguments);

        AssertExitCode(0, result);
        using JsonDocument writtenReport = JsonDocument.Parse(File.ReadAllText(outputPath));
        Assert.Equal(
            EcsBenchmarkSuite.ReportSchemaVersion,
            writtenReport.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Empty(Directory.GetFiles(
            Path.GetDirectoryName(outputPath)!,
            $".{Path.GetFileName(outputPath)}.tmp.*"));
    }

    [Fact]
    public void InvalidConfigurationReturnsTwo()
    {
        ProgramResult result = InvokeProgram("--profile", "certification");

        AssertExitCode(2, result);
    }

    [Fact]
    public void GateFailureReturnsThree()
    {
        using var directory = new TemporaryDirectory();
        string budgetPath = BenchmarkTestData.WriteBudget(
            directory,
            defaults: new
            {
                maxP50Milliseconds = 0.0,
                maxAllocatedBytesPerSample = 0.0,
                maxTotalAllocatedBytesPerSample = 0.0,
            });
        string[] arguments = [.. TinySmokeArguments(), "--absolute-budgets", budgetPath];

        ProgramResult result = InvokeProgram(arguments);

        AssertExitCode(3, result);
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

    private static ProgramResult InvokeProgram(params string[] arguments)
    {
        TextWriter originalOutput = Console.Out;
        TextWriter originalError = Console.Error;
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var error = new StringWriter(CultureInfo.InvariantCulture);
        try
        {
            Console.SetOut(output);
            Console.SetError(error);
            int exitCode = Program.Main(arguments);
            return new ProgramResult(exitCode, output.ToString(), error.ToString());
        }
        finally
        {
            Console.SetOut(originalOutput);
            Console.SetError(originalError);
        }
    }

    private static void AssertExitCode(int expected, ProgramResult result) =>
        Assert.True(
            result.ExitCode == expected,
            $"Expected exit code {expected}, but received {result.ExitCode}." +
            $"{Environment.NewLine}Standard output:{Environment.NewLine}{result.StandardOutput}" +
            $"{Environment.NewLine}Standard error:{Environment.NewLine}{result.StandardError}");

    private static void AssertStructuralCloneEvidence(string standardOutput)
    {
        using JsonDocument report = JsonDocument.Parse(standardOutput);
        JsonElement structuralResult = report.RootElement
            .GetProperty("results")
            .EnumerateArray()
            .Single(static result =>
                result.GetProperty("scenario").GetString() == "structural-candidate-1-x1");

        AssertPositiveCloneMetrics(structuralResult.GetProperty("structuralMetrics"));
        JsonElement sample = structuralResult.GetProperty("samples").EnumerateArray().Single();
        AssertPositiveCloneMetrics(sample.GetProperty("structuralMetrics"));
    }

    private static void AssertPositiveCloneMetrics(JsonElement metrics)
    {
        Assert.True(metrics.GetProperty("clonedArchetypeShells").GetInt64() > 0);
        Assert.True(metrics.GetProperty("worldMaximumClonedArchetypeShells").GetInt64() > 0);
        Assert.True(metrics.GetProperty("clonedChunkShells").GetInt64() > 0);
        Assert.True(metrics.GetProperty("worldMaximumClonedChunkShells").GetInt64() > 0);
        Assert.True(metrics.GetProperty("clonedQueryMatches").GetInt64() > 0);
        Assert.True(metrics.GetProperty("worldMaximumClonedQueryMatches").GetInt64() > 0);
    }

    private static void AssertSerializationWorkloadEvidence(string standardOutput)
    {
        using JsonDocument report = JsonDocument.Parse(standardOutput);
        Dictionary<string, JsonElement> results = report.RootElement
            .GetProperty("results")
            .EnumerateArray()
            .ToDictionary(
                static result => result.GetProperty("scenario").GetString()!,
                static result => result);

        Assert.True(results["snapshot-write-1"]
            .GetProperty("workloadMetrics")
            .GetProperty("snapshotWriteMilliseconds")
            .GetProperty("max")
            .GetDouble() > 0);
        Assert.True(results["snapshot-read-1"]
            .GetProperty("workloadMetrics")
            .GetProperty("loadMilliseconds")
            .GetProperty("max")
            .GetDouble() > 0);
        Assert.True(results["durable-save-roundtrip-1"]
            .GetProperty("workloadMetrics")
            .GetProperty("durableCommitMilliseconds")
            .GetProperty("max")
            .GetDouble() > 0);
        Assert.True(results["mixed-frame-update-snapshot-load-1"]
            .GetProperty("workloadMetrics")
            .GetProperty("payloadBytes")
            .GetProperty("max")
            .GetDouble() > 0);
    }

    private sealed record ProgramResult(
        int ExitCode,
        string StandardOutput,
        string StandardError);
}
