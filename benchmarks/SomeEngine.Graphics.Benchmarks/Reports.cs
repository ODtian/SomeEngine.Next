using System.Text.Json;
using System.Text.Json.Serialization;

namespace SomeEngine.Graphics.Benchmarks;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    WriteIndented = false)]
[JsonSerializable(typeof(ProcessRun))]
[JsonSerializable(typeof(GraphicsBenchmarkReport))]
[JsonSerializable(typeof(ShaderManifest))]
[JsonSerializable(typeof(GraphicsCpuBenchmarkReport))]
internal partial class ProcessRunJsonContext : JsonSerializerContext
{
}

internal enum GraphicsCpuWorkload : byte
{
    DagorEnlistedHighWatermark,
}

internal readonly record struct GraphicsCpuSourceIdentity(
    string Project,
    string Revision,
    string Path,
    string Url);

internal readonly record struct GraphicsCpuWorkloadShape(
    uint Seed,
    int PassCount,
    int ResourceCount,
    int BufferCount,
    int TextureCount,
    int AccessCount,
    int DependencyCount,
    int BarrierCount,
    int BarrierBoundaryCount,
    int BufferBarrierCount,
    int TextureBarrierCount,
    int QueueTransferBarrierCount,
    int AliasingBarrierCount,
    int SplitBarrierPairCount,
    int RasterPassCount,
    int ComputePassCount,
    int CopyPassCount,
    int ControlPassCount,
    int DirectDrawCalls,
    int ExecuteIndirectDrawCalls,
    int IndirectDrawCommands,
    int DirectDispatchCalls,
    int ExecuteIndirectDispatchCalls,
    int CopyCommands,
    int QueueCount,
    int SubmissionCount,
    int FrameSlotCount,
    bool SplitBarriersEnabled,
    bool QueueSpecificCommonLayouts,
    ulong TransientResourceBytes,
    ulong TransientHeapBytes);

internal readonly record struct GraphicsCpuFrameSample(
    int FrameIndex,
    long CpuStopwatchTicks,
    double CpuMicroseconds,
    int CompletionCount);

internal sealed record GraphicsCpuWorkloadResult(
    GraphicsCpuWorkload Workload,
    string Case,
    string EvidenceLabel,
    GraphicsCpuSourceIdentity Source,
    GraphicsCpuWorkloadShape Shape,
    string TimingBoundary,
    int MinimumWarmupFrames,
    int ActualWarmupFrames,
    bool WarmupPlateauReached,
    GraphicsCpuFrameSample[] WarmupSamples,
    GraphicsCpuFrameSample[] Samples,
    MetricDistribution Cpu);

internal sealed record GraphicsCpuBenchmarkReport(
    string Schema,
    string Disposition,
    string Reason,
    DateTimeOffset StartedUtc,
    DateTimeOffset CompletedUtc,
    RuntimeEnvironment Environment,
    string StandardPath,
    GraphicsCpuWorkloadResult[] Workloads)
{
    internal void Write(string path)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        using FileStream stream = new(path, FileMode.Create, FileAccess.Write, FileShare.None);
        JsonSerializer.Serialize(
            stream,
            this,
            ProcessRunJsonContext.Default.GraphicsCpuBenchmarkReport);
    }
}

internal readonly record struct BuildIdentity(
    string ExecutablePath,
    string ExecutableSha256,
    string PayloadSha256,
    string AssemblyVersion,
    string Configuration,
    string Commit,
    bool WorktreeDirty,
    string Toolchain,
    string CommandConstructionBoundary = "public-end-return");

internal readonly record struct RuntimeEnvironment(
    string OperatingSystem,
    string Architecture,
    string ProcessorName,
    int ProcessId,
    int ProcessIndex,
    long AffinityMask,
    string Priority,
    string PowerMode,
    string AdapterName,
    uint VendorId,
    uint DeviceId,
    ulong AdapterLuidLow,
    ulong AdapterLuidHigh,
    string DriverVersion,
    bool HardwareAccelerated,
    uint AgilitySdkVersion,
    bool ValidationEnabled,
    bool DredEnabled,
    bool CaptureToolLoaded,
    BuildIdentity Build);

internal readonly record struct CalibrationRecord(
    QueueType Queue,
    int FrameIndex,
    long CpuCounter,
    long CpuFrequency,
    ulong QueueCounter,
    ulong QueueFrequency);

internal readonly record struct FrameSample(
    int FrameIndex,
    long CpuStopwatchTicks,
    double CpuMicroseconds,
    double? GpuMicroseconds,
    long ManagedAllocatedBytes,
    long EtwAllocationEvents,
    ulong CompletionValue,
    long? PostCloseCleanupStopwatchTicks = null,
    double? PostCloseCleanupMicroseconds = null);

internal readonly record struct BarrierEvidence(
    int PublicOrdinal,
    string PublicKind,
    int NativeOrdinal,
    int NativeExpansionCount,
    string? ExpansionReason);

internal readonly record struct CommandWorkloadEvidence(
    int ObjectPacketCount,
    int LogicalDrawRequests,
    int LogicalMaterialBindingRequests,
    int NativeDrawCommands,
    int NativeMaterialBindingCommands,
    int CommandListResetCount,
    int CommandListCloseCount,
    int BarrierCommands,
    int WorkerCount,
    string DrawCallShape);

internal sealed record WorkloadRun(
    GraphicsWorkload Workload,
    RunDisposition Disposition,
    string Reason,
    int WarmupFrames,
    int MeasuredFrames,
    int DrawCount,
    int BarrierCount,
    FrameSample[] Samples,
    CalibrationRecord[] Calibrations,
    string OutputSha256,
    string ShaderManifestSha256,
    BarrierEvidence[] Barriers,
    MetricDistribution? Cpu,
    MetricDistribution? Gpu,
    MetricDistribution? PostCloseCleanup = null,
    CommandWorkloadEvidence? WorkloadEvidence = null);

internal sealed record ProcessRun(
    ReceiverVariant Variant,
    RunDisposition Disposition,
    string Reason,
    RuntimeEnvironment Environment,
    WorkloadRun[] Workloads);

internal sealed record WorkloadMeasurements(
    GraphicsWorkload Workload,
    RunDisposition Disposition,
    string Reason,
    int WarmupFrames,
    int MeasuredFrames,
    int DrawCount,
    int BarrierCount,
    int SampleCount,
    double[] CpuSamples,
    double[] GpuSamples,
    string OutputSha256,
    string ShaderManifestSha256,
    BarrierEvidence[] Barriers,
    double[] PostCloseCleanupSamples)
{
    internal static WorkloadMeasurements Create(WorkloadRun run)
    {
        return new WorkloadMeasurements(
            run.Workload,
            run.Disposition,
            run.Reason,
            run.WarmupFrames,
            run.MeasuredFrames,
            run.DrawCount,
            run.BarrierCount,
            run.Samples.Length,
            run.Samples.Select(static sample => sample.CpuMicroseconds).ToArray(),
            run.Samples
                .Where(static sample => sample.GpuMicroseconds.HasValue)
                .Select(static sample => sample.GpuMicroseconds!.Value)
                .ToArray(),
            run.OutputSha256,
            run.ShaderManifestSha256,
            run.Barriers,
            run.Samples
                .Where(static sample => sample.PostCloseCleanupMicroseconds.HasValue)
                .Select(static sample => sample.PostCloseCleanupMicroseconds!.Value)
                .ToArray());
    }
}

internal sealed record ProcessMeasurements(
    ReceiverVariant Variant,
    RunDisposition Disposition,
    string Reason,
    RuntimeEnvironment Environment,
    int Position,
    WorkloadMeasurements[] Workloads)
{
    internal static ProcessMeasurements Create(ProcessRun run, int position) => new(
        run.Variant,
        run.Disposition,
        run.Reason,
        run.Environment,
        position,
        run.Workloads.Select(WorkloadMeasurements.Create).ToArray());

}

internal readonly record struct RawProcessEvidence(
    string Path,
    string Sha256,
    ReceiverVariant Variant,
    int ProcessIndex,
    int Position);

internal readonly record struct MeasurementIssue(
    string Code,
    string Message,
    ReceiverVariant? Variant = null,
    GraphicsWorkload? Workload = null);

internal readonly record struct ComparisonResult(
    string Name,
    GraphicsWorkload Workload,
    string Metric,
    string Percentile,
    double CandidateMicroseconds,
    double BaselineMicroseconds,
    double DeltaMicroseconds,
    double? DeltaPercent);


internal sealed record PairedBlockDiagnostic(
    int ProcessIndex,
    GraphicsWorkload Workload,
    ReceiverVariant BaselineVariant,
    int BaselinePosition,
    ReceiverVariant CandidateVariant,
    int CandidatePosition,
    double DeltaMicrosecondsPerCall,
    double DeltaPercent,
    double BaselineP95OverP50,
    double CandidateP95OverP50);

internal sealed record MeasurementSummary(
    RunDisposition Disposition,
    string Reason,
    MeasurementIssue[] Issues,
    ComparisonResult[] Comparisons)
{
    public PairedBlockDiagnostic[] PairedDiagnostics { get; init; } = [];
}

internal sealed record GraphicsBenchmarkReport(
    string Schema,
    BenchmarkProfile Profile,
    RunDisposition Disposition,
    string Reason,
    DateTimeOffset StartedUtc,
    DateTimeOffset CompletedUtc,
    ProtocolSnapshot Protocol,
    RawProcessEvidence[] RawEvidence,
    MeasurementSummary Summary)
{
    internal void Write(string path)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        using FileStream stream = new(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None);
        JsonSerializer.Serialize(
            stream,
            this,
            ProcessRunJsonContext.Default.GraphicsBenchmarkReport);
    }

    internal static GraphicsBenchmarkReport Read(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return JsonSerializer.Deserialize(
            stream,
            ProcessRunJsonContext.Default.GraphicsBenchmarkReport)
            ?? throw new InvalidDataException($"'{path}' does not contain a graphics benchmark report.");
    }

    internal static ProcessRun ReadProcess(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return JsonSerializer.Deserialize(stream, ProcessRunJsonContext.Default.ProcessRun)
            ?? throw new InvalidDataException($"'{path}' does not contain a graphics process result.");
    }

    internal static void WriteProcess(string path, ProcessRun run)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        using FileStream stream = new(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None);
        JsonSerializer.Serialize(stream, run, ProcessRunJsonContext.Default.ProcessRun);
    }
}
