namespace SomeEngine.Graphics.Benchmarks;

internal static class BenchmarkWorker
{
    internal static int Run(BenchmarkOptions options)
    {
        ReceiverVariant variant = options.Variant
            ?? throw new BenchmarkUsageException("worker requires --variant.");
        if (variant == ReceiverVariant.NativeCpp)
            throw new BenchmarkUsageException("native-cpp must be executed by the native runner.");
        WorkerConfiguration configuration = new(
            options.Profile,
            variant,
            options.AdapterId,
            options.ProcessIndex,
            options.WarmupFrames,
            options.MeasuredFrames,
            options.DrawCount,
            options.BarrierCount,
            options.ShaderDirectory,
            options.OutputPath,
            options.Workloads.Length == 0 ? null : options.Workloads,
            options.DefaultDirectCalls);
        SchedulingResult scheduling = BenchmarkEnvironment.EstablishScheduling(options.Profile);
        ProcessRun run;
        if (!scheduling.Established)
        {
            run = new ProcessRun(
                variant,
                RunDisposition.Unexecuted,
                scheduling.Reason,
                BenchmarkEnvironment.Unavailable(options.ProcessIndex),
                []);
        }
        else
        {
            run = variant switch
            {
                ReceiverVariant.InterfaceReceiver =>
                    RhiBenchmarkRunner.Run(configuration),
                ReceiverVariant.DirectSilk or ReceiverVariant.DirectSilkDefault =>
                    DirectSilkBenchmarkRunner.Run(configuration),
                _ => throw new ArgumentOutOfRangeException(nameof(variant)),
            };
        }
        GraphicsBenchmarkReport.WriteProcess(options.OutputPath, run);
        Console.WriteLine($"{variant}: {run.Disposition} — {run.Reason}");
        return run.Disposition is RunDisposition.Passed or RunDisposition.FunctionalOnly ? 0 : 3;
    }
}
