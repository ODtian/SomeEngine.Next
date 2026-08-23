using System.Diagnostics;

namespace SomeEngine.Graphics.Benchmarks;

internal enum BenchmarkCommand : byte
{
    Warp,
    Diagnose,
    Certify,
    Worker,
    Evaluate,
    Probe,
    GraphCpu,
}

internal enum BenchmarkProfile : byte
{
    WarpFunctional,
    FastDiagnostic,
    VendorCertification,
    DeveloperProbe,
    RepresentativeCpuFrame,
    GraphicsCpuDevelopment,
}

internal enum ReceiverVariant : byte
{
    InterfaceReceiver,
    DirectSilk,
    NativeCpp,
    DirectSilkDefault,
}

internal enum GraphicsWorkload : byte
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

internal enum RunDisposition : byte
{
    Passed,
    Failed,
    FunctionalOnly,
    Unexecuted,
}

internal static class FixedGraphicsProtocol
{
    internal const string Schema = "someengine.graphics.performance/v3";
    internal const int WarmupFrames = 8_192;
    internal const int MeasuredFrames = 16_384;
    internal const int ProcessCount = 5;
    internal const int DrawCount = 10_000;
    internal const int BarrierCount = 4_096;
    internal const int WarpWarmupFrames = 2;
    internal const int WarpMeasuredFrames = 4;
    internal const int WarpDrawCount = 64;
    internal const int WarpBarrierCount = 32;
    internal const int DiagnosticWarmupFrames = 512;
    internal const int DiagnosticMeasuredFrames = 1_024;
    internal const int DiagnosticProcessCount = 4;
    internal const int DiagnosticDrawCount = 10_000;
    internal const int DiagnosticBarrierCount = 0;
    internal const int ProbeWarmupFrames = 64;
    internal const int ProbeMeasuredFrames = 256;
    internal const int ProbeDrawCount = 1_000;
    internal const int ProbeBarrierCount = 1_000;
    internal const int RepresentativeWarmupFrames = 4_096;
    internal const int RepresentativeMeasuredFrames = 4_096;
    internal const int RepresentativeProcessCount = 5;
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

    // The first three rounds form a Latin square, so every receiver occupies
    // every process position exactly once. The required fourth and fifth rounds
    // continue the same deterministic rotation.
    internal static readonly ReceiverVariant[][] InterleavedRounds =
    [
        [ReceiverVariant.InterfaceReceiver, ReceiverVariant.DirectSilk, ReceiverVariant.NativeCpp],
        [ReceiverVariant.DirectSilk, ReceiverVariant.NativeCpp, ReceiverVariant.InterfaceReceiver],
        [ReceiverVariant.NativeCpp, ReceiverVariant.InterfaceReceiver, ReceiverVariant.DirectSilk],
        [ReceiverVariant.InterfaceReceiver, ReceiverVariant.DirectSilk, ReceiverVariant.NativeCpp],
        [ReceiverVariant.InterfaceReceiver, ReceiverVariant.DirectSilk, ReceiverVariant.NativeCpp],
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

    internal static int GetProcessCount(BenchmarkProfile profile) => profile switch
    {
        BenchmarkProfile.WarpFunctional => 1,
        BenchmarkProfile.FastDiagnostic => DiagnosticProcessCount,
        BenchmarkProfile.VendorCertification => ProcessCount,
        BenchmarkProfile.DeveloperProbe => 1,
        BenchmarkProfile.RepresentativeCpuFrame => RepresentativeProcessCount,
        _ => throw new ArgumentOutOfRangeException(nameof(profile)),
    };

    internal static ReadOnlySpan<ReceiverVariant> GetInterleavedRound(int processIndex)
    {
        if ((uint)processIndex >= (uint)InterleavedRounds.Length)
            throw new ArgumentOutOfRangeException(nameof(processIndex));
        return InterleavedRounds[processIndex];
    }

    internal static bool IsCertificationShape(in WorkerConfiguration value) =>
        value.Profile == BenchmarkProfile.VendorCertification &&
        value.WarmupFrames == WarmupFrames &&
        value.MeasuredFrames == MeasuredFrames &&
        value.DrawCount == DrawCount &&
        value.BarrierCount == BarrierCount;

    internal static bool IsDiagnosticShape(in WorkerConfiguration value) =>
        value.Profile == BenchmarkProfile.FastDiagnostic &&
        value.WarmupFrames == DiagnosticWarmupFrames &&
        value.MeasuredFrames == DiagnosticMeasuredFrames &&
        value.DrawCount == DiagnosticDrawCount &&
        value.BarrierCount == DiagnosticBarrierCount;
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
    internal bool IsCertificationShape => FixedGraphicsProtocol.IsCertificationShape(this);

    internal bool IsDiagnosticShape => FixedGraphicsProtocol.IsDiagnosticShape(this);

    internal ReadOnlySpan<GraphicsWorkload> Workloads =>
        SelectedWorkloads ?? FixedGraphicsProtocol.GetWorkloads(Profile).ToArray();
}

internal readonly record struct MetricDistribution(
    double P50,
    double P95,
    double P99,
    double Maximum)
{
    internal static MetricDistribution From(ReadOnlySpan<double> values)
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

    internal static double PercentileR7(ReadOnlySpan<double> sorted, double percentile)
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
    string[][] InterleavedRounds,
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
            FixedGraphicsProtocol.PercentileMethod,
            FixedGraphicsProtocol.CpuInterval,
            FixedGraphicsProtocol.GpuInterval,
            Enumerable.Range(0, processCount)
                .Select(processIndex => (selectedVariants ?? FixedGraphicsProtocol
                    .GetInterleavedRound(processIndex).ToArray())
                    .Select(static value => value.ToString())
                    .ToArray())
                .ToArray(),
            (selectedWorkloads ?? FixedGraphicsProtocol.GetWorkloads(profile).ToArray())
                .Select(static value => value.ToString())
                .ToArray());
}

internal static class BenchmarkClock
{
    internal static double TicksToMicroseconds(long ticks) =>
        ticks * (1_000_000.0 / Stopwatch.Frequency);
}
