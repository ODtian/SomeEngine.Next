using SlangShaderSharp;

namespace SomeEngine.Graphics;

public enum RayTracingTier : byte
{
    Tier1_0,
    Tier1_1,
}

public sealed class RayTracing : DeviceCapability
{
    internal RayTracing(
        Device device,
        RayTracingTier tier,
        bool inlineRayQuery,
        bool indirectDispatch,
        bool accelerationStructureUpdate,
        bool compaction,
        bool serialization,
        bool stateObjectAdditions,
        uint maximumRecursionDepth,
        uint maximumPayloadSize,
        uint maximumAttributeSize,
        uint maximumGeometriesPerBottomLevel,
        uint maximumInstancesPerTopLevel,
        uint maximumPrimitivesPerBottomLevel,
        uint maximumRayGenerationShaderThreads,
        uint maximumShaderRecordStride,
        ulong accelerationStructureAlignment,
        ulong scratchAlignment,
        ulong shaderTableAlignment,
        ulong shaderRecordAlignment)
        : base(device)
    {
        Tier = tier;
        InlineRayQuery = inlineRayQuery;
        IndirectDispatch = indirectDispatch;
        AccelerationStructureUpdate = accelerationStructureUpdate;
        Compaction = compaction;
        Serialization = serialization;
        StateObjectAdditions = stateObjectAdditions;
        MaximumRecursionDepth = maximumRecursionDepth;
        MaximumPayloadSize = maximumPayloadSize;
        MaximumAttributeSize = maximumAttributeSize;
        MaximumGeometriesPerBottomLevel = maximumGeometriesPerBottomLevel;
        MaximumInstancesPerTopLevel = maximumInstancesPerTopLevel;
        MaximumPrimitivesPerBottomLevel = maximumPrimitivesPerBottomLevel;
        MaximumRayGenerationShaderThreads = maximumRayGenerationShaderThreads;
        MaximumShaderRecordStride = maximumShaderRecordStride;
        AccelerationStructureAlignment = accelerationStructureAlignment;
        ScratchAlignment = scratchAlignment;
        ShaderTableAlignment = shaderTableAlignment;
        ShaderRecordAlignment = shaderRecordAlignment;
    }

    public RayTracingTier Tier { get; }
    public bool InlineRayQuery { get; }
    public bool IndirectDispatch { get; }
    public bool AccelerationStructureUpdate { get; }
    public bool Compaction { get; }
    public bool Serialization { get; }
    public bool StateObjectAdditions { get; }
    public uint MaximumRecursionDepth { get; }
    public uint MaximumPayloadSize { get; }
    public uint MaximumAttributeSize { get; }
    public uint MaximumGeometriesPerBottomLevel { get; }
    public uint MaximumInstancesPerTopLevel { get; }
    public uint MaximumPrimitivesPerBottomLevel { get; }
    public uint MaximumRayGenerationShaderThreads { get; }
    public uint MaximumShaderRecordStride { get; }
    public ulong AccelerationStructureAlignment { get; }
    public ulong ScratchAlignment { get; }
    public ulong ShaderTableAlignment { get; }
    public ulong ShaderRecordAlignment { get; }
}

public enum AccelerationStructureType : byte
{
    BottomLevel,
    TopLevel,
}

[Flags]
public enum AccelerationStructureBuildOptions : byte
{
    None = 0,
    AllowUpdate = 1 << 0,
    AllowCompaction = 1 << 1,
    PreferFastTrace = 1 << 2,
    PreferFastBuild = 1 << 3,
    MinimizeMemory = 1 << 4,
    PerformUpdate = 1 << 5,
}

public readonly record struct AccelerationStructureInfo(
    AccelerationStructureType Type,
    ulong Size,
    Buffer Storage,
    BufferRange StorageRange);

public abstract class AccelerationStructure : Resource
{
    internal AccelerationStructure(
        Device device,
        in AccelerationStructureInfo info,
        string? label)
        : base(
            device,
            info.Storage.Heap,
            PipelineSync.None,
            ResourceAccess.NoAccess,
            label)
    {
        Info = info;
    }

    public AccelerationStructureInfo Info { get; }
}

public readonly record struct AccelerationStructureSrvDesc(
    AccelerationStructure AccelerationStructure,
    string? Label = null);

public abstract class AccelerationStructureSrv : DeviceResource
{
    internal AccelerationStructureSrv(Device device, in AccelerationStructureSrvDesc description)
        : base(device, description.Label) => Description = description;

    public AccelerationStructureSrvDesc Description { get; }
    public AccelerationStructure Resource => Description.AccelerationStructure;
}

public abstract class BindlessAccelerationStructureSrv : AccelerationStructureSrv
{
    internal BindlessAccelerationStructureSrv(
        Device device,
        in AccelerationStructureSrvDesc description,
        uint descriptorIndex)
        : base(device, description) => DescriptorIndex = descriptorIndex;

    public uint DescriptorIndex { get; }
}

public enum AccelerationStructureGeometryType : byte
{
    Triangles,
    AxisAlignedBoundingBoxes,
    Instances,
}

[Flags]
public enum AccelerationStructureGeometryOptions : byte
{
    None = 0,
    Opaque = 1 << 0,
    NoDuplicateAnyHitInvocation = 1 << 1,
}

public readonly record struct AccelerationStructureGeometry(
    AccelerationStructureGeometryType Type,
    BufferRegion Primary,
    Format PrimaryFormat,
    uint PrimaryStride,
    uint Count,
    BufferRegion Secondary,
    Format SecondaryFormat,
    AccelerationStructureGeometryOptions Options = AccelerationStructureGeometryOptions.None,
    IndexType IndexType = IndexType.UInt32,
    BufferRegion Transform = default);

public readonly ref struct AccelerationStructureBuildDesc
{
    public AccelerationStructureBuildDesc(
        AccelerationStructureType type,
        AccelerationStructureBuildOptions options,
        ReadOnlySpan<AccelerationStructureGeometry> geometries,
        AccelerationStructure destination,
        Buffer scratch,
        BufferRange scratchRange,
        AccelerationStructure? source = null)
    {
        Type = type;
        Options = options;
        Geometries = geometries;
        Destination = destination;
        Scratch = scratch;
        ScratchRange = scratchRange;
        Source = source;
    }

    public AccelerationStructureType Type { get; }
    public AccelerationStructureBuildOptions Options { get; }
    public ReadOnlySpan<AccelerationStructureGeometry> Geometries { get; }
    public AccelerationStructure Destination { get; }
    public Buffer Scratch { get; }
    public BufferRange ScratchRange { get; }
    public AccelerationStructure? Source { get; }
}

public readonly record struct AccelerationStructureBuildInfo(
    ulong ResultSize,
    ulong ResultAlignment,
    ulong BuildScratchSize,
    ulong BuildScratchAlignment,
    ulong UpdateScratchSize,
    ulong UpdateScratchAlignment);

public enum AccelerationStructureCopyType : byte
{
    Clone,
    Compact,
}

public enum AccelerationStructurePostBuildInfoType : byte
{
    CompactedSize,
    SerializationSize,
    CurrentSize,
}

public readonly record struct RayTracingHitGroup(
    string Name,
    EntryPointReflection ClosestHit,
    EntryPointReflection AnyHit,
    EntryPointReflection Intersection);

[Flags]
public enum RayTracingPipelineOptions : byte
{
    None = 0,
    SkipTriangles = 1 << 0,
    SkipProceduralPrimitives = 1 << 1,
}

public readonly ref struct RayTracingPipelineDesc
{
    public RayTracingPipelineDesc(
        IComponentType program,
        ReadOnlySpan<EntryPointReflection> rayGeneration,
        ReadOnlySpan<EntryPointReflection> miss,
        ReadOnlySpan<EntryPointReflection> callable,
        ReadOnlySpan<RayTracingHitGroup> hitGroups,
        uint maximumRecursionDepth,
        uint maximumPayloadSize,
        uint maximumAttributeSize,
        RayTracingPipelineOptions options = RayTracingPipelineOptions.None,
        uint nodeMask = 1,
        string? label = null)
    {
        Program = program;
        RayGeneration = rayGeneration;
        Miss = miss;
        Callable = callable;
        HitGroups = hitGroups;
        MaximumRecursionDepth = maximumRecursionDepth;
        MaximumPayloadSize = maximumPayloadSize;
        MaximumAttributeSize = maximumAttributeSize;
        Options = options;
        NodeMask = nodeMask;
        Label = label;
    }

    public IComponentType Program { get; }
    public ReadOnlySpan<EntryPointReflection> RayGeneration { get; }
    public ReadOnlySpan<EntryPointReflection> Miss { get; }
    public ReadOnlySpan<EntryPointReflection> Callable { get; }
    public ReadOnlySpan<RayTracingHitGroup> HitGroups { get; }
    public uint MaximumRecursionDepth { get; }
    public uint MaximumPayloadSize { get; }
    public uint MaximumAttributeSize { get; }
    public RayTracingPipelineOptions Options { get; }
    public uint NodeMask { get; }
    public string? Label { get; }
}

public readonly record struct RayTracingShaderTableDesc(
    Pipeline Pipeline,
    uint RayGenerationRecordCount,
    uint MissRecordCount,
    uint HitRecordCount,
    uint CallableRecordCount,
    uint MaximumRecordSize,
    string? Label = null);

public abstract class RayTracingShaderTable : DeviceResource
{
    internal RayTracingShaderTable(Device device, in RayTracingShaderTableDesc description)
        : base(device, description.Label) => Description = description;

    public RayTracingShaderTableDesc Description { get; }
}

public readonly struct RayTracingShaderRecord
{
    private RayTracingShaderRecord(
        EntryPointReflection entryPoint,
        string? hitGroupName,
        VariableLayoutReflection layout,
        uint resourceOffset,
        uint resourceCount,
        uint ordinaryDataOffset,
        uint ordinaryDataSize)
    {
        EntryPoint = entryPoint;
        HitGroupName = hitGroupName;
        Layout = layout;
        ResourceOffset = resourceOffset;
        ResourceCount = resourceCount;
        OrdinaryDataOffset = ordinaryDataOffset;
        OrdinaryDataSize = ordinaryDataSize;
    }

    public EntryPointReflection EntryPoint { get; }
    public string? HitGroupName { get; }
    public VariableLayoutReflection Layout { get; }
    public uint ResourceOffset { get; }
    public uint ResourceCount { get; }
    public uint OrdinaryDataOffset { get; }
    public uint OrdinaryDataSize { get; }

    public static RayTracingShaderRecord Entry(
        EntryPointReflection entryPoint,
        VariableLayoutReflection layout,
        uint resourceOffset,
        uint resourceCount,
        uint ordinaryDataOffset,
        uint ordinaryDataSize) =>
        new(
            entryPoint,
            null,
            layout,
            resourceOffset,
            resourceCount,
            ordinaryDataOffset,
            ordinaryDataSize);

    public static RayTracingShaderRecord HitGroup(
        string hitGroupName,
        VariableLayoutReflection layout,
        uint resourceOffset,
        uint resourceCount,
        uint ordinaryDataOffset,
        uint ordinaryDataSize) =>
        new(
            EntryPointReflection.Null,
            hitGroupName ?? throw new ArgumentNullException(nameof(hitGroupName)),
            layout,
            resourceOffset,
            resourceCount,
            ordinaryDataOffset,
            ordinaryDataSize);
}

public readonly ref struct RayTracingShaderTableUpdate
{
    public RayTracingShaderTableUpdate(
        ReadOnlySpan<RayTracingShaderRecord> rayGeneration,
        ReadOnlySpan<RayTracingShaderRecord> miss,
        ReadOnlySpan<RayTracingShaderRecord> hit,
        ReadOnlySpan<RayTracingShaderRecord> callable,
        ReadOnlySpan<ResourceBinding> resources,
        ReadOnlySpan<byte> ordinaryData)
    {
        RayGeneration = rayGeneration;
        Miss = miss;
        Hit = hit;
        Callable = callable;
        Resources = resources;
        OrdinaryData = ordinaryData;
    }

    public ReadOnlySpan<RayTracingShaderRecord> RayGeneration { get; }
    public ReadOnlySpan<RayTracingShaderRecord> Miss { get; }
    public ReadOnlySpan<RayTracingShaderRecord> Hit { get; }
    public ReadOnlySpan<RayTracingShaderRecord> Callable { get; }
    public ReadOnlySpan<ResourceBinding> Resources { get; }
    public ReadOnlySpan<byte> OrdinaryData { get; }
}

public readonly record struct DispatchRaysDesc(
    RayTracingShaderTable ShaderTable,
    uint Width,
    uint Height = 1,
    uint Depth = 1);
