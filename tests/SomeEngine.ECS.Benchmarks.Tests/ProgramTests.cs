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
        using JsonDocument report = JsonDocument.Parse(result.StandardOutput);
        Assert.NotEmpty(report.RootElement.GetProperty("results").EnumerateArray());
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
        ProgramResult result = InvokeProgram("--samples", "0");

        AssertExitCode(2, result);
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




    private sealed record ProgramResult(
        int ExitCode,
        string StandardOutput,
        string StandardError);
}
