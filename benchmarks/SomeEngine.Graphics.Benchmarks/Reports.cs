using System.Text.Json;
using System.Text.Json.Serialization;

namespace SomeEngine.Graphics.Benchmarks;

internal readonly record struct BuildIdentity(
    string ExecutablePath,
    string ExecutableSha256,
    string PayloadSha256,
    string AssemblyVersion,
    string Configuration,
    string Commit,
    bool WorktreeDirty,
    string Toolchain);

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
    ulong CompletionValue);

internal readonly record struct BarrierEvidence(
    int PublicOrdinal,
    string PublicKind,
    int NativeOrdinal,
    int NativeExpansionCount,
    string? ExpansionReason);

internal readonly record struct NativeSetterEvidence(
    int PipelineSetters,
    int PersistentBindingSetters,
    int ViewportSetters,
    int ScissorSetters,
    int DrawCalls);

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
    NativeSetterEvidence NativeSetters,
    MetricDistribution? Cpu,
    MetricDistribution? Gpu);

internal sealed record ProcessRun(
    ReceiverVariant Variant,
    RunDisposition Disposition,
    string Reason,
    RuntimeEnvironment Environment,
    WorkloadRun[] Workloads);

internal readonly record struct GateIssue(
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
    double? DeltaPercent,
    double AbsoluteLimitMicroseconds,
    double? RelativeLimitPercent,
    bool Passed);

internal sealed record GateResult(
    RunDisposition Disposition,
    string Reason,
    GateIssue[] Issues,
    ComparisonResult[] Comparisons);

internal sealed record GraphicsBenchmarkReport(
    string Schema,
    BenchmarkProfile Profile,
    RunDisposition Disposition,
    string Reason,
    DateTimeOffset StartedUtc,
    DateTimeOffset CompletedUtc,
    ProtocolSnapshot Protocol,
    ProcessRun[] Runs,
    GateResult Gate)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

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
        JsonSerializer.Serialize(stream, this, JsonOptions);
    }

    internal static GraphicsBenchmarkReport Read(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<GraphicsBenchmarkReport>(stream, JsonOptions)
            ?? throw new InvalidDataException($"'{path}' does not contain a graphics benchmark report.");
    }

    internal static ProcessRun ReadProcess(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<ProcessRun>(stream, JsonOptions)
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
        JsonSerializer.Serialize(stream, run, JsonOptions);
    }
}
