using SlangShaderSharp;

namespace SomeEngine.Graphics;

/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe. Immutable values may be shared; referenced RHI objects retain their own contracts.</para>
/// <para><b>Ownership:</b> Pure value; owns no RHI, OS, or native lifetime.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public enum WorkGraphTier : byte
{
    Tier1_0,
}

/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe. Immutable values may be shared; referenced RHI objects retain their own contracts.</para>
/// <para><b>Ownership:</b> Borrowed or caller-supplied managed identity; it owns no independent native lifetime unless a member explicitly says otherwise.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state; associated RHI objects retain their own terminal state.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public sealed class WorkGraphs : DeviceCapability
{
    internal WorkGraphs(
        Device device,
        WorkGraphTier tier,
        bool cpuInput,
        bool gpuInput,
        uint maximumNodeCount,
        uint maximumInputRecordSize,
        uint maximumOutputRecordSize,
        uint maximumInputRecordCount)
        : base(device)
    {
        Tier = tier;
        CpuInput = cpuInput;
        GpuInput = gpuInput;
        MaximumNodeCount = maximumNodeCount;
        MaximumInputRecordSize = maximumInputRecordSize;
        MaximumOutputRecordSize = maximumOutputRecordSize;
        MaximumInputRecordCount = maximumInputRecordCount;
    }

    public WorkGraphTier Tier { get; }
    public bool CpuInput { get; }
    public bool GpuInput { get; }
    public uint MaximumNodeCount { get; }
    public uint MaximumInputRecordSize { get; }
    public uint MaximumOutputRecordSize { get; }
    public uint MaximumInputRecordCount { get; }
}

/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe. Immutable values may be shared; referenced RHI objects retain their own contracts.</para>
/// <para><b>Ownership:</b> Pure managed value; copying it does not transfer ownership of any referenced RHI object.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state; referenced objects retain their own terminal state.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public readonly record struct WorkGraphEntryPointLayout(
    EntryPointReflection EntryPoint,
    uint NodeIndex,
    uint MaximumInputRecordCount);

/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe. Immutable values may be shared; referenced RHI objects retain their own contracts.</para>
/// <para><b>Ownership:</b> Pure managed value; copying it does not transfer ownership of any referenced RHI object.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state; referenced objects retain their own terminal state.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public readonly record struct WorkGraphNodeOverride(
    EntryPointReflection EntryPoint,
    uint MaximumDispatchGridX,
    uint MaximumDispatchGridY,
    uint MaximumDispatchGridZ,
    uint MaximumInputRecordCount);

/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe. Immutable values may be shared; referenced RHI objects retain their own contracts.</para>
/// <para><b>Ownership:</b> Pure value; owns no RHI, OS, or native lifetime.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
[Flags]
public enum WorkGraphPipelineOptions : byte
{
    None = 0,
    IncludeAllAvailableNodes = 1 << 0,
}

/// <remarks>
/// <para><b>Thread safety:</b> Externally synchronized. Concurrent Dispose calls are safe where supported; normal use racing with Dispose is not.</para>
/// <para><b>Ownership:</b> Stack-only description or view; it owns no referenced RHI object and receiver calls consume every Span synchronously.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state; borrowed storage remains caller-owned.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public readonly ref struct WorkGraphPipelineDesc
{
    public WorkGraphPipelineDesc(
        IComponentType program,
        string programName,
        ReadOnlySpan<WorkGraphEntryPointLayout> entryPoints,
        ReadOnlySpan<WorkGraphNodeOverride> nodeOverrides,
        uint maximumInputRecordCount,
        WorkGraphPipelineOptions options = WorkGraphPipelineOptions.None,
        uint nodeMask = 1,
        string? label = null)
    {
        Program = program;
        ProgramName = programName;
        EntryPoints = entryPoints;
        NodeOverrides = nodeOverrides;
        MaximumInputRecordCount = maximumInputRecordCount;
        Options = options;
        NodeMask = nodeMask;
        Label = label;
    }

    public IComponentType Program { get; }
    public string ProgramName { get; }
    public ReadOnlySpan<WorkGraphEntryPointLayout> EntryPoints { get; }
    public ReadOnlySpan<WorkGraphNodeOverride> NodeOverrides { get; }
    public uint MaximumInputRecordCount { get; }
    public WorkGraphPipelineOptions Options { get; }
    public uint NodeMask { get; }
    public string? Label { get; }
}

/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe. Immutable values may be shared; referenced RHI objects retain their own contracts.</para>
/// <para><b>Ownership:</b> Pure managed value; copying it does not transfer ownership of any referenced RHI object.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state; referenced objects retain their own terminal state.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public readonly record struct WorkGraphMemoryRequirements(
    ulong MinimumSize,
    ulong MaximumSize,
    ulong Granularity);

/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe. Immutable values may be shared; referenced RHI objects retain their own contracts.</para>
/// <para><b>Ownership:</b> Pure value; owns no RHI, OS, or native lifetime.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public enum WorkGraphInitialization : byte
{
    Initialize,
    Preserve,
}

/// <remarks>
/// <para><b>Thread safety:</b> Externally synchronized. Concurrent Dispose calls are safe where supported; normal use racing with Dispose is not.</para>
/// <para><b>Ownership:</b> Stack-only description or view; it owns no referenced RHI object and receiver calls consume every Span synchronously.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state; borrowed storage remains caller-owned.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public readonly ref struct WorkGraphDispatchDesc
{
    public WorkGraphDispatchDesc(
        uint entryPointIndex,
        ReadOnlySpan<byte> records,
        uint recordCount,
        uint recordStride)
    {
        EntryPointIndex = entryPointIndex;
        Records = records;
        RecordCount = recordCount;
        RecordStride = recordStride;
        GpuRecords = default;
        UsesGpuRecords = false;
    }

    public WorkGraphDispatchDesc(
        uint entryPointIndex,
        in BufferRegion gpuRecords,
        uint recordCount,
        uint recordStride)
    {
        EntryPointIndex = entryPointIndex;
        Records = default;
        RecordCount = recordCount;
        RecordStride = recordStride;
        GpuRecords = gpuRecords;
        UsesGpuRecords = true;
    }

    public uint EntryPointIndex { get; }
    public ReadOnlySpan<byte> Records { get; }
    public BufferRegion GpuRecords { get; }
    public bool UsesGpuRecords { get; }
    public uint RecordCount { get; }
    public uint RecordStride { get; }
}
