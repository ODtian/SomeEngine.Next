using System.Diagnostics;

namespace SomeEngine.Graphics.Benchmarks;

public enum BenchmarkCommand : byte
{
    Warp,
    Diagnose,
    Worker,
    Evaluate,
    Probe,
    GraphCpu,
}

public enum BenchmarkProfile : byte
{
    WarpFunctional,
    FastDiagnostic,
    DeveloperProbe,
    RepresentativeCpuFrame,
    GraphicsCpuDevelopment,
}

public enum ReceiverVariant : byte
{
    InterfaceReceiver,
    DirectSilk,
    NativeCpp,
    DirectSilkDefault,
}

public enum GraphicsWorkload : byte
{
    EmptySubmit,
    PersistentDraw10000,
    TransientDraw10000,
    StateSuppression10000,
    ExplicitBarrier4096,
    ThreeQueuePresent,
    RepresentativeFrameSerial,
    RepresentativeFrameParallel,
}

public enum RunDisposition : byte
{
    Failed,
    FunctionalOnly,
    Unexecuted,
}

internal static class BenchmarkDefaults
{
    internal const string Schema = "someengine.graphics.measurements/v1";
    internal const int WarpWarmupFrames = 2;
    internal const int WarpMeasuredFrames = 4;
    internal const int WarpDrawCount = 64;
    internal const int WarpBarrierCount = 32;
    internal const int DiagnosticWarmupFrames = 512;
    internal const int DiagnosticMeasuredFrames = 1_024;
    internal const int DiagnosticDrawCount = 10_000;
    internal const int DiagnosticBarrierCount = 0;
    internal const int ProbeWarmupFrames = 64;
    internal const int ProbeMeasuredFrames = 256;
    internal const int ProbeDrawCount = 1_000;
    internal const int ProbeBarrierCount = 1_000;
    internal const int RepresentativeWarmupFrames = 4_096;
    internal const int RepresentativeMeasuredFrames = 4_096;
    internal const int GraphicsCpuMinimumWarmupFrames = 1_024;
    internal const int GraphicsCpuMeasuredFrames = 1_024;
    internal const int RenderWidth = 64;
    internal const int RenderHeight = 64;
    internal const string PercentileMethod =
        "R-7 linear interpolation over fresh samples";
    internal const string CpuInterval =
        "Immediately before the first CommandContext.Begin equivalent through the final Submit/Present return";
    internal const string GpuInterval =
        "First workload timestamp on the earliest participating Queue through the final graphics workload command before Present";

    internal static readonly ReceiverVariant[] Variants =
    [
        ReceiverVariant.InterfaceReceiver,
        ReceiverVariant.DirectSilk,
        ReceiverVariant.NativeCpp,
    ];

    internal static readonly GraphicsWorkload[] Workloads =
    [
        GraphicsWorkload.EmptySubmit,
        GraphicsWorkload.PersistentDraw10000,
        GraphicsWorkload.TransientDraw10000,
        GraphicsWorkload.StateSuppression10000,
        GraphicsWorkload.ExplicitBarrier4096,
        GraphicsWorkload.ThreeQueuePresent,
    ];

    internal static readonly GraphicsWorkload[] DiagnosticWorkloads =
    [
        GraphicsWorkload.PersistentDraw10000,
        GraphicsWorkload.TransientDraw10000,
        GraphicsWorkload.StateSuppression10000,
    ];

    internal static readonly GraphicsWorkload[] ProbeWorkloads =
    [
        GraphicsWorkload.PersistentDraw10000,
    ];

    internal static readonly GraphicsWorkload[] RepresentativeWorkloads =
    [
        GraphicsWorkload.RepresentativeFrameSerial,
        GraphicsWorkload.RepresentativeFrameParallel,
    ];

    internal static readonly ReceiverVariant[] ProbeVariants =
    [
        ReceiverVariant.InterfaceReceiver,
    ];

    internal static ReadOnlySpan<GraphicsWorkload> GetWorkloads(BenchmarkProfile profile) =>
        profile switch
        {
            BenchmarkProfile.FastDiagnostic => DiagnosticWorkloads,
            BenchmarkProfile.DeveloperProbe => ProbeWorkloads,
            BenchmarkProfile.RepresentativeCpuFrame => RepresentativeWorkloads,
            _ => Workloads,
        };




}

internal readonly record struct WorkerConfiguration(
    BenchmarkProfile Profile,
    ReceiverVariant Variant,
    AdapterId AdapterId,
    int ProcessIndex,
    int WarmupFrames,
    int MeasuredFrames,
    int DrawCount,
    int BarrierCount,
    string ShaderDirectory,
    string OutputPath,
    GraphicsWorkload[]? SelectedWorkloads = null,
    bool DefaultDirectCalls = false)
{


    internal ReadOnlySpan<GraphicsWorkload> Workloads =>
        SelectedWorkloads ?? BenchmarkDefaults.GetWorkloads(Profile).ToArray();
}

public readonly record struct MetricDistribution(
    double P50,
    double P95,
    double P99,
    double Maximum)
{
    public static MetricDistribution From(ReadOnlySpan<double> values)
    {
        if (values.IsEmpty)
            throw new ArgumentException("A distribution requires at least one value.", nameof(values));
        double[] sorted = values.ToArray();
        Array.Sort(sorted);
        return new MetricDistribution(
            PercentileR7(sorted, 0.50),
            PercentileR7(sorted, 0.95),
            PercentileR7(sorted, 0.99),
            sorted[^1]);
    }

    public static double PercentileR7(ReadOnlySpan<double> sorted, double percentile)
    {
        if (sorted.IsEmpty)
            throw new ArgumentException("A percentile requires at least one value.", nameof(sorted));
        if (!double.IsFinite(percentile) || percentile is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(percentile));
        double position = (sorted.Length - 1) * percentile;
        int lower = (int)Math.Floor(position);
        int upper = (int)Math.Ceiling(position);
        if (lower == upper)
            return sorted[lower];
        double fraction = position - lower;
        return sorted[lower] + (sorted[upper] - sorted[lower]) * fraction;
    }
}

internal readonly record struct ProtocolSnapshot(
    int WarmupFrames,
    int MeasuredFrames,
    int ProcessCount,
    int DrawCount,
    int BarrierCount,
    string PercentileMethod,
    string CpuInterval,
    string GpuInterval,
    string[][] VariantOrder,
    string[] Workloads)
{
    internal static ProtocolSnapshot Create(
        BenchmarkProfile profile,
        int warmupFrames,
        int measuredFrames,
        int processCount,
        int drawCount,
        int barrierCount,
        GraphicsWorkload[]? selectedWorkloads = null,
        ReceiverVariant[]? selectedVariants = null) => new(
            warmupFrames,
            measuredFrames,
            processCount,
            drawCount,
            barrierCount,
            BenchmarkDefaults.PercentileMethod,
            BenchmarkDefaults.CpuInterval,
            BenchmarkDefaults.GpuInterval,
            Enumerable.Range(0, processCount)
                .Select(processIndex => (selectedVariants ?? BenchmarkDefaults.Variants)
                    .Select(static value => value.ToString())
                    .ToArray())
                .ToArray(),
            (selectedWorkloads ?? BenchmarkDefaults.GetWorkloads(profile).ToArray())
                .Select(static value => value.ToString())
                .ToArray());
}

internal static class BenchmarkClock
{
    internal static double TicksToMicroseconds(long ticks) =>
        ticks * (1_000_000.0 / Stopwatch.Frequency);
}
