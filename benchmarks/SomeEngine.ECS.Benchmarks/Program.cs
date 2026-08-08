using System.Text;
using System.Text.Json;

namespace SomeEngine.ECS.Benchmarks;

internal static class Program
{
    private const int ConfigurationErrorExitCode = 2;
    private const int GateFailureExitCode = 3;

    public static int Main(string[] args)
    {
        BenchmarkOptions parsedOptions;
        try
        {
            if (!BenchmarkOptions.TryParse(args, out BenchmarkOptions? options, out string? error))
            {
                if (error is not null)
                    Console.Error.WriteLine(error);
                Console.Error.WriteLine(BenchmarkOptions.HelpText);
                return error is null ? 0 : ConfigurationErrorExitCode;
            }
            parsedOptions = options!;
        }
        catch (Exception exception) when (
            exception is ArgumentException or IOException or NotSupportedException or OverflowException)
        {
            Console.Error.WriteLine($"Invalid benchmark configuration: {exception.Message}");
            return ConfigurationErrorExitCode;
        }

        try
        {
            EcsBenchmarkReport report = EcsBenchmarkSuite.Run(parsedOptions);
            string json = JsonSerializer.Serialize(report, EcsBenchmarkReport.JsonOptions);
            report.CertificationEvidence?.ValidationState?.VerifyUnchanged();
            Console.WriteLine(json);

            if (parsedOptions.OutputPath is not null)
            {
                WriteReportAtomically(parsedOptions.OutputPath, json);
                Console.Error.WriteLine($"Benchmark report written to {parsedOptions.OutputPath}");
            }

            return report.Passed ? 0 : GateFailureExitCode;
        }
        catch (BenchmarkConfigurationException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return ConfigurationErrorExitCode;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static void WriteReportAtomically(string path, string json)
    {
        string fullPath = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(fullPath)
            ?? throw new IOException("Benchmark output path has no parent directory.");
        Directory.CreateDirectory(directory);
        string temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(fullPath)}.tmp.{Guid.NewGuid():N}");
        try
        {
            File.WriteAllText(
                temporaryPath,
                json + Environment.NewLine,
                new UTF8Encoding(false));
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }
}
