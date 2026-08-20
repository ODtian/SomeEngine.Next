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
        uint maximumDispatchGridDimension,
        uint maximumDispatchGridVolume)
        : this(
            device,
            tier,
            cpuInput,
            gpuInput,
            maximumNodeCount,
            maximumInputRecordSize,
            maximumOutputRecordSize,
            maximumDispatchGridDimension,
            maximumDispatchGridVolume,
            maximumDispatchGridDimension)
    {
    }

    internal WorkGraphs(
        Device device,
        WorkGraphTier tier,
        bool cpuInput,
        bool gpuInput,
        uint maximumNodeCount,
        uint maximumInputRecordSize,
        uint maximumOutputRecordSize,
        uint maximumDispatchGridDimension,
        uint maximumDispatchGridVolume,
        uint maximumOneDimensionalDispatchGridX)
        : base(device)
    {
        Tier = tier;
        CpuInput = cpuInput;
        GpuInput = gpuInput;
        MaximumNodeCount = maximumNodeCount;
        MaximumInputRecordSize = maximumInputRecordSize;
        MaximumOutputRecordSize = maximumOutputRecordSize;
        MaximumDispatchGridDimension = maximumDispatchGridDimension;
        MaximumDispatchGridVolume = maximumDispatchGridVolume;
        MaximumOneDimensionalDispatchGridX = maximumOneDimensionalDispatchGridX;
    }

    public WorkGraphTier Tier { get; }
    public bool CpuInput { get; }
    public bool GpuInput { get; }
    public uint MaximumNodeCount { get; }
    public uint MaximumInputRecordSize { get; }
    public uint MaximumOutputRecordSize { get; }

    /// <summary>
    /// Traditional maximum accepted for each broadcasting-grid axis unless X uses the conditional
    /// one-dimensional extension reported by <see cref="MaximumOneDimensionalDispatchGridX"/>.
    /// </summary>
    public uint MaximumDispatchGridDimension { get; }

    /// <summary>Maximum product of all three axes of a broadcasting-node dispatch grid.</summary>
    public uint MaximumDispatchGridVolume { get; }

    /// <summary>
    /// Maximum broadcasting-grid X value when Y and Z are both one; it is never below
    /// <see cref="MaximumDispatchGridDimension"/>.
    /// </summary>
    public uint MaximumOneDimensionalDispatchGridX { get; }
}

internal static class WorkGraphValidation
{
    internal static bool IsMaximumDispatchGridValid(
        uint maximumDimension,
        uint maximumOneDimensionalX,
        uint maximumVolume,
        uint x,
        uint y,
        uint z)
    {
        if (!AreLimitsValid(maximumDimension, maximumOneDimensionalX, maximumVolume) ||
            x == 0 || y == 0 || z == 0)
            return false;

        if (x > maximumDimension)
            return IsExtendedOneDimensionalGridValid(
                maximumOneDimensionalX,
                maximumVolume,
                x,
                y,
                z);

        if (y > maximumDimension || z > maximumDimension)
            return false;

        return FitsVolume(maximumVolume, x, y, z);
    }

    private static bool AreLimitsValid(
        uint maximumDimension,
        uint maximumOneDimensionalX,
        uint maximumVolume) =>
        maximumDimension != 0 &&
        maximumOneDimensionalX >= maximumDimension &&
        maximumVolume != 0;

    private static bool IsExtendedOneDimensionalGridValid(
        uint maximumOneDimensionalX,
        uint maximumVolume,
        uint x,
        uint y,
        uint z) =>
        y == 1 &&
        z == 1 &&
        x <= maximumOneDimensionalX &&
        x <= maximumVolume;

    private static bool FitsVolume(uint maximumVolume, uint x, uint y, uint z)
    {
        if (x > maximumVolume / y)
            return false;
        uint xy = checked(x * y);
        return z <= maximumVolume / xy;
    }

    internal static bool IsEntryPointLayoutValid(
        uint maximumInputRecordSize,
        uint size,
        uint alignment)
    {
        if (size == 0)
            return alignment == 0;
        return size != uint.MaxValue &&
            alignment != uint.MaxValue &&
            size <= maximumInputRecordSize &&
            alignment >= 4 &&
            (alignment & (alignment - 1)) == 0 &&
            size % alignment == 0;
    }

    internal static string GetEffectiveEntryPointName(
        string name,
        string? nameOverride) =>
        string.IsNullOrWhiteSpace(nameOverride) ? name : nameOverride;

    internal static string GetEffectiveEntryPointName(EntryPointReflection entryPoint) =>
        GetEffectiveEntryPointName(entryPoint.Name, entryPoint.NameOverride);
}

/// <remarks>
/// <para><b>Thread safety:</b> Externally synchronized. This type has no Dispose operation.</para>
/// <para><b>Ownership:</b> Stack-only description or view; it owns no referenced RHI object and receiver calls consume every Span synchronously.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state; borrowed storage remains caller-owned.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public readonly ref struct WorkGraphPipelineDesc
{
    public WorkGraphPipelineDesc(
        IComponentType program,
        uint nodeMask = 1,
        string? label = null,
        ReadOnlySpan<StaticSamplerBinding> staticSamplers = default)
    {
        Program = program;
        NodeMask = nodeMask;
        Label = label;
        StaticSamplers = staticSamplers;
    }

    public IComponentType Program { get; }
    public uint NodeMask { get; }
    public string? Label { get; }
    public ReadOnlySpan<StaticSamplerBinding> StaticSamplers { get; }
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
    ulong Granularity)
{
    internal ulong NormalizeBackingSize(ulong suppliedSize)
    {
        if (MaximumSize < MinimumSize)
        {
            throw new InvalidOperationException(
                "Work Graph backing-memory requirements have a maximum below their minimum.");
        }
        if (suppliedSize < MinimumSize)
            throw new ArgumentOutOfRangeException(nameof(suppliedSize));

        ulong cappedSize = Math.Min(suppliedSize, MaximumSize);
        if (cappedSize == MinimumSize || Granularity == 0)
            return MinimumSize;

        ulong usableIncrement =
            ((cappedSize - MinimumSize) / Granularity) * Granularity;
        return MinimumSize + usableIncrement;
    }
}

/// <summary>
/// Authoritative materialized identity and input-record contract for one Work Graph entry point.
/// </summary>
/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe. Immutable values may be shared.</para>
/// <para><b>Ownership:</b> Pure managed value; owns no native lifetime.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state; the value remains readable after its source Pipeline is disposed.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public readonly record struct WorkGraphEntryPointInfo(
    EntryPointReflection EntryPoint,
    uint RecordSize,
    uint RecordAlignment);

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
/// <para><b>Thread safety:</b> Externally synchronized. This type has no Dispose operation.</para>
/// <para><b>Ownership:</b> Stack-only description or view; it owns no referenced RHI object and receiver calls consume every Span synchronously.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state; borrowed storage remains caller-owned.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public enum WorkGraphDispatchInputMode : byte
{
    NodeCpu,
    NodeGpu,
    MultiNodeCpu,
    MultiNodeGpu,
}

/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe. Immutable values may be shared; referenced RHI objects retain their own contracts.</para>
/// <para><b>Ownership:</b> Pure managed value; copying it does not transfer ownership of the reflected entry point.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public readonly record struct WorkGraphCpuNodeInput(
    EntryPointReflection EntryPoint,
    uint RecordOffset,
    uint RecordCount,
    uint RecordStride);

/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe. Immutable values may be shared; referenced RHI objects retain their own contracts.</para>
/// <para><b>Ownership:</b> Pure managed value; it borrows its Buffer and reflected entry point.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state; referenced objects retain their own terminal state.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public readonly record struct WorkGraphGpuNodeInput(
    EntryPointReflection EntryPoint,
    BufferRegion Records,
    uint RecordCount,
    uint RecordStride);

/// <remarks>
/// <para><b>Thread safety:</b> Externally synchronized. This type has no Dispose operation.</para>
/// <para><b>Ownership:</b> Stack-only description; all spans and referenced objects are borrowed and consumed synchronously by the receiver.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state; borrowed storage remains caller-owned.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public readonly ref struct WorkGraphDispatchDesc
{
    public WorkGraphDispatchDesc(
        EntryPointReflection entryPoint,
        ReadOnlySpan<byte> records,
        uint recordCount,
        uint recordStride)
    {
        Mode = WorkGraphDispatchInputMode.NodeCpu;
        EntryPoint = entryPoint;
        Records = records;
        RecordCount = recordCount;
        RecordStride = recordStride;
        GpuRecords = default;
        CpuNodeInputs = default;
        GpuNodeInputs = default;
    }

    public WorkGraphDispatchDesc(
        EntryPointReflection entryPoint,
        in BufferRegion gpuRecords,
        uint recordCount,
        uint recordStride)
    {
        Mode = WorkGraphDispatchInputMode.NodeGpu;
        EntryPoint = entryPoint;
        Records = default;
        RecordCount = recordCount;
        RecordStride = recordStride;
        GpuRecords = gpuRecords;
        CpuNodeInputs = default;
        GpuNodeInputs = default;
    }

    public WorkGraphDispatchDesc(
        ReadOnlySpan<WorkGraphCpuNodeInput> nodeInputs,
        ReadOnlySpan<byte> records)
    {
        Mode = WorkGraphDispatchInputMode.MultiNodeCpu;
        EntryPoint = EntryPointReflection.Null;
        Records = records;
        RecordCount = 0;
        RecordStride = 0;
        GpuRecords = default;
        CpuNodeInputs = nodeInputs;
        GpuNodeInputs = default;
    }

    public WorkGraphDispatchDesc(ReadOnlySpan<WorkGraphGpuNodeInput> nodeInputs)
    {
        Mode = WorkGraphDispatchInputMode.MultiNodeGpu;
        EntryPoint = EntryPointReflection.Null;
        Records = default;
        RecordCount = 0;
        RecordStride = 0;
        GpuRecords = default;
        CpuNodeInputs = default;
        GpuNodeInputs = nodeInputs;
    }

    public WorkGraphDispatchInputMode Mode { get; }
    public EntryPointReflection EntryPoint { get; }
    public ReadOnlySpan<byte> Records { get; }
    public BufferRegion GpuRecords { get; }
    public uint RecordCount { get; }
    public uint RecordStride { get; }
    public ReadOnlySpan<WorkGraphCpuNodeInput> CpuNodeInputs { get; }
    public ReadOnlySpan<WorkGraphGpuNodeInput> GpuNodeInputs { get; }
}
