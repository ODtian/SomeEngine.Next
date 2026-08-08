using SlangShaderSharp;

namespace SomeEngine.Graphics;

public enum WorkGraphTier : byte
{
    Tier1_0,
}

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

public readonly record struct WorkGraphEntryPointLayout(
    EntryPointReflection EntryPoint,
    uint NodeIndex,
    uint MaximumInputRecordCount);

public readonly record struct WorkGraphNodeOverride(
    EntryPointReflection EntryPoint,
    uint MaximumDispatchGridX,
    uint MaximumDispatchGridY,
    uint MaximumDispatchGridZ,
    uint MaximumInputRecordCount);

[Flags]
public enum WorkGraphPipelineOptions : byte
{
    None = 0,
    IncludeAllAvailableNodes = 1 << 0,
}

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

public readonly record struct WorkGraphMemoryRequirements(
    ulong MinimumSize,
    ulong MaximumSize,
    ulong Granularity);

public enum WorkGraphInitialization : byte
{
    Initialize,
    Preserve,
}

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
