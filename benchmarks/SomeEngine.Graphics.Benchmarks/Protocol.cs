using System.Diagnostics;

namespace SomeEngine.Graphics.Benchmarks;

internal enum BenchmarkCommand : byte
{
    Warp,
    Certify,
    Worker,
    Evaluate,
}

internal enum BenchmarkProfile : byte
{
    WarpFunctional,
    VendorCertification,
}

internal enum ReceiverVariant : byte
{
    GenericRhi,
    InterfaceRhi,
    DirectSilk,
    NativeCpp,
}

internal enum GraphicsWorkload : byte
{
    EmptySubmit,
    PersistentDraw10000,
    TransientDraw10000,
    StateSuppression10000,
    ExplicitBarrier4096,
    ThreeQueuePresent,
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
    internal const string Schema = "someengine.graphics.performance/v2";
    internal const int WarmupFrames = 8_192;
    internal const int MeasuredFrames = 16_384;
    internal const int ProcessCount = 5;
    internal const int DrawCount = 10_000;
    internal const int BarrierCount = 4_096;
    internal const int WarpWarmupFrames = 2;
    internal const int WarpMeasuredFrames = 4;
    internal const int WarpDrawCount = 64;
    internal const int WarpBarrierCount = 32;
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
        ReceiverVariant.GenericRhi,
        ReceiverVariant.InterfaceRhi,
        ReceiverVariant.DirectSilk,
        ReceiverVariant.NativeCpp,
    ];

    // The first four rounds form a Latin square, so every receiver occupies
    // every process position exactly once. The required fifth round repeats
    // the canonical receiver order and keeps the schedule deterministic.
    internal static readonly ReceiverVariant[][] InterleavedRounds =
    [
        [ReceiverVariant.GenericRhi, ReceiverVariant.InterfaceRhi, ReceiverVariant.DirectSilk, ReceiverVariant.NativeCpp],
        [ReceiverVariant.InterfaceRhi, ReceiverVariant.DirectSilk, ReceiverVariant.NativeCpp, ReceiverVariant.GenericRhi],
        [ReceiverVariant.DirectSilk, ReceiverVariant.NativeCpp, ReceiverVariant.GenericRhi, ReceiverVariant.InterfaceRhi],
        [ReceiverVariant.NativeCpp, ReceiverVariant.GenericRhi, ReceiverVariant.InterfaceRhi, ReceiverVariant.DirectSilk],
        [ReceiverVariant.GenericRhi, ReceiverVariant.InterfaceRhi, ReceiverVariant.DirectSilk, ReceiverVariant.NativeCpp],
    ];

    internal static readonly GraphicsWorkload[] Workloads = Enum.GetValues<GraphicsWorkload>();

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
    string OutputPath)
{
    internal bool IsCertificationShape => FixedGraphicsProtocol.IsCertificationShape(this);
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
        int warmupFrames,
        int measuredFrames,
        int processCount,
        int drawCount,
        int barrierCount) => new(
            warmupFrames,
            measuredFrames,
            processCount,
            drawCount,
            barrierCount,
            FixedGraphicsProtocol.PercentileMethod,
            FixedGraphicsProtocol.CpuInterval,
            FixedGraphicsProtocol.GpuInterval,
            Enumerable.Range(0, processCount)
                .Select(static processIndex => FixedGraphicsProtocol
                    .GetInterleavedRound(processIndex)
                    .ToArray()
                    .Select(static value => value.ToString())
                    .ToArray())
                .ToArray(),
            FixedGraphicsProtocol.Workloads.Select(static value => value.ToString()).ToArray());
}

internal static class BenchmarkClock
{
    internal static double TicksToMicroseconds(long ticks) =>
        ticks * (1_000_000.0 / Stopwatch.Frequency);
}
