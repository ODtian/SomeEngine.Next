namespace SomeEngine.Graphics.Benchmarks;

internal static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            BenchmarkOptions options = BenchmarkOptions.Parse(args);
            return options.Command switch
            {
                BenchmarkCommand.Warp => BenchmarkController.RunWarp(options),
                BenchmarkCommand.Diagnose => BenchmarkController.RunDiagnostic(options),
                BenchmarkCommand.Certify => BenchmarkController.RunCertification(options),
                BenchmarkCommand.Worker => BenchmarkWorker.Run(options),
                BenchmarkCommand.Evaluate => BenchmarkController.EvaluateExisting(options),
                _ => throw new ArgumentOutOfRangeException(nameof(options.Command)),
            };
        }
        catch (BenchmarkUsageException exception)
        {
            Console.Error.WriteLine(exception.Message);
            Console.Error.WriteLine(BenchmarkOptions.Usage);
            return 2;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }
}
