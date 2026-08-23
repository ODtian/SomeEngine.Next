using SlangShaderSharp;

namespace SomeEngine.Graphics;

/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe. Immutable values may be shared; referenced RHI objects retain their own contracts.</para>
/// <para><b>Ownership:</b> Pure value; owns no RHI, OS, or native lifetime.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public enum RayTracingTier : byte
{
    Tier1_0,
    Tier1_1,
}

/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe. Immutable values may be shared; referenced RHI objects retain their own contracts.</para>
/// <para><b>Ownership:</b> Borrowed or caller-supplied managed identity; it owns no independent native lifetime unless a member explicitly says otherwise.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state; associated RHI objects retain their own terminal state.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public sealed class RayTracing : DeviceCapability
{
    internal RayTracing(
        Device device,
        RayTracingTier tier,
        bool pipelineRayTracing,
        bool inlineRayQuery,
        bool indirectDispatch,
        bool accelerationStructureUpdate,
        bool compaction,
        bool serialization,
        bool stateObjectAdditions,
        bool shaderRecordResourceBindings,
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
        PipelineRayTracing = pipelineRayTracing;
        InlineRayQuery = inlineRayQuery;
        IndirectDispatch = indirectDispatch;
        AccelerationStructureUpdate = accelerationStructureUpdate;
        Compaction = compaction;
        Serialization = serialization;
        StateObjectAdditions = stateObjectAdditions;
        ShaderRecordResourceBindings = shaderRecordResourceBindings;
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
    public bool PipelineRayTracing { get; }
    public bool InlineRayQuery { get; }
    public bool IndirectDispatch { get; }
    public bool AccelerationStructureUpdate { get; }
    public bool Compaction { get; }
    public bool Serialization { get; }
    public bool StateObjectAdditions { get; }
    /// <summary>
    /// Whether shader-table records may contain opaque resource and sampler bindings in addition
    /// to ordinary bytes. Vulkan shader records only expose ordinary bytes; DXR local root
    /// signatures can also expose descriptor tables.
    /// </summary>
    public bool ShaderRecordResourceBindings { get; }
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

/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe. Immutable values may be shared; referenced RHI objects retain their own contracts.</para>
/// <para><b>Ownership:</b> Pure value; owns no RHI, OS, or native lifetime.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public enum AccelerationStructureType : byte
{
    BottomLevel,
    TopLevel,
}

/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe. Immutable values may be shared; referenced RHI objects retain their own contracts.</para>
/// <para><b>Ownership:</b> Pure value; owns no RHI, OS, or native lifetime.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
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

/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe. Immutable values may be shared; referenced RHI objects retain their own contracts.</para>
/// <para><b>Ownership:</b> Pure managed value; copying it does not transfer ownership of any referenced RHI object.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state; referenced objects retain their own terminal state.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public readonly record struct AccelerationStructureInfo(
    AccelerationStructureType Type,
    ulong Size,
    Buffer Storage,
    BufferRange StorageRange);

/// <remarks>
/// <para><b>Thread safety:</b> Externally synchronized. Concurrent Dispose calls are safe and collectively perform one logical release; normal use racing with Dispose is not.</para>
/// <para><b>Ownership:</b> Caller-disposed RHI identity. Its backend or Device parent also ends it during cascading teardown; association properties are not shared ownership.</para>
/// <para><b>After Dispose:</b> Only immutable managed metadata explicitly exposed by the type remains readable; behavior and native access are invalid.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
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

/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe. Immutable values may be shared; referenced RHI objects retain their own contracts.</para>
/// <para><b>Ownership:</b> Pure managed value; copying it does not transfer ownership of any referenced RHI object.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state; referenced objects retain their own terminal state.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public readonly record struct AccelerationStructureSrvDesc(
    AccelerationStructure AccelerationStructure,
    string? Label = null);

/// <remarks>
/// <para><b>Thread safety:</b> Externally synchronized. Concurrent Dispose calls are safe and collectively perform one logical release; normal use racing with Dispose is not.</para>
/// <para><b>Ownership:</b> Caller-disposed RHI identity. Its backend or Device parent also ends it during cascading teardown; association properties are not shared ownership.</para>
/// <para><b>After Dispose:</b> Only immutable managed metadata explicitly exposed by the type remains readable; behavior and native access are invalid.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public abstract class AccelerationStructureSrv : DeviceResource
{
    internal AccelerationStructureSrv(Device device, in AccelerationStructureSrvDesc description)
        : base(device, description.Label) => Description = description;

    public AccelerationStructureSrvDesc Description { get; }
    public AccelerationStructure Resource => Description.AccelerationStructure;
}

/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe. Immutable values may be shared; referenced RHI objects retain their own contracts.</para>
/// <para><b>Ownership:</b> Pure value; owns no RHI, OS, or native lifetime.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public enum AccelerationStructureGeometryType : byte
{
    Triangles,
    AxisAlignedBoundingBoxes,
    Instances,
}

/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe. Immutable values may be shared; referenced RHI objects retain their own contracts.</para>
/// <para><b>Ownership:</b> Pure value; owns no RHI, OS, or native lifetime.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
[Flags]
public enum AccelerationStructureGeometryOptions : byte
{
    None = 0,
    Opaque = 1 << 0,
    NoDuplicateAnyHitInvocation = 1 << 1,
}

/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe. Immutable values may be shared; referenced RHI objects retain their own contracts.</para>
/// <para><b>Ownership:</b> Pure managed value; copying it does not transfer ownership of any referenced RHI object.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state; referenced objects retain their own terminal state.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
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

/// <remarks>
/// <para><b>Thread safety:</b> Externally synchronized. This type has no Dispose operation.</para>
/// <para><b>Ownership:</b> Stack-only description or view; it owns no referenced RHI object and receiver calls consume every Span synchronously.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state; borrowed storage remains caller-owned.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
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

/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe. Immutable values may be shared; referenced RHI objects retain their own contracts.</para>
/// <para><b>Ownership:</b> Pure managed value; copying it does not transfer ownership of any referenced RHI object.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state; referenced objects retain their own terminal state.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public readonly record struct AccelerationStructureBuildInfo(
    ulong ResultSize,
    ulong ResultAlignment,
    ulong BuildScratchSize,
    ulong BuildScratchAlignment,
    ulong UpdateScratchSize,
    ulong UpdateScratchAlignment);

/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe. Immutable values may be shared; referenced RHI objects retain their own contracts.</para>
/// <para><b>Ownership:</b> Pure value; owns no RHI, OS, or native lifetime.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public enum AccelerationStructureCopyType : byte
{
    Clone,
    Compact,
}

/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe. Immutable values may be shared; referenced RHI objects retain their own contracts.</para>
/// <para><b>Ownership:</b> Pure value; owns no RHI, OS, or native lifetime.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public enum AccelerationStructurePostBuildInfoType : byte
{
    CompactedSize,
    SerializationSize,
    CurrentSize,
}

/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe. Immutable values may be shared; referenced RHI objects retain their own contracts.</para>
/// <para><b>Ownership:</b> Pure managed value; copying it does not transfer ownership of any referenced RHI object.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state; referenced objects retain their own terminal state.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public readonly record struct RayTracingHitGroup(
    string Name,
    EntryPointReflection ClosestHit,
    EntryPointReflection AnyHit,
    EntryPointReflection Intersection);

/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe. Immutable values may be shared; referenced RHI objects retain their own contracts.</para>
/// <para><b>Ownership:</b> Pure value; owns no RHI, OS, or native lifetime.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
[Flags]
public enum RayTracingPipelineOptions : byte
{
    None = 0,
    SkipTriangles = 1 << 0,
    SkipProceduralPrimitives = 1 << 1,
}

/// <remarks>
/// <para><b>Thread safety:</b> Externally synchronized. This type has no Dispose operation.</para>
/// <para><b>Ownership:</b> Stack-only description or view; it owns no referenced RHI object and receiver calls consume every Span synchronously.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state; borrowed storage remains caller-owned.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
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
        string? label = null,
        ReadOnlySpan<StaticSamplerBinding> staticSamplers = default)
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
        StaticSamplers = staticSamplers;
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
    public ReadOnlySpan<StaticSamplerBinding> StaticSamplers { get; }
}

/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe. Immutable values may be shared; referenced RHI objects retain their own contracts.</para>
/// <para><b>Ownership:</b> Pure managed value; copying it does not transfer ownership of any referenced RHI object.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state; referenced objects retain their own terminal state.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public readonly record struct RayTracingShaderTableDesc(
    Pipeline Pipeline,
    uint RayGenerationRecordCount,
    uint MissRecordCount,
    uint HitRecordCount,
    uint CallableRecordCount,
    uint MaximumRecordSize,
    string? Label = null);

/// <remarks>
/// <para><b>Thread safety:</b> Externally synchronized. Concurrent Dispose calls are safe and collectively perform one logical release; normal use racing with Dispose is not.</para>
/// <para><b>Ownership:</b> Caller-disposed RHI identity. Its backend or Device parent also ends it during cascading teardown; association properties are not shared ownership.</para>
/// <para><b>After Dispose:</b> Only immutable managed metadata explicitly exposed by the type remains readable; behavior and native access are invalid.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public abstract class RayTracingShaderTable : DeviceResource
{
    internal RayTracingShaderTable(Device device, in RayTracingShaderTableDesc description)
        : base(device, description.Label) => Description = description;

    public RayTracingShaderTableDesc Description { get; }
}

/// <summary>One exact SL# parameter-object occurrence encoded into a DXR shader record.</summary>
/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe. Immutable values may be shared; referenced SL# reflection remains owned by the associated Pipeline.</para>
/// <para><b>Ownership:</b> Pure managed value; it borrows the exact Slang layout identity and slices of the enclosing update payload.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public readonly record struct RayTracingLocalParameterBlock(
    VariableLayoutReflection Layout,
    uint ResourceOffset,
    uint ResourceCount,
    uint OrdinaryDataOffset,
    uint OrdinaryDataSize);

/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe. Immutable values may be shared; referenced RHI objects retain their own contracts.</para>
/// <para><b>Ownership:</b> Pure managed value; copying it does not transfer ownership of any referenced RHI object.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state; referenced objects retain their own terminal state.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public readonly struct RayTracingShaderRecord
{
    private RayTracingShaderRecord(
        EntryPointReflection entryPoint,
        string? hitGroupName,
        uint parameterBlockOffset,
        uint parameterBlockCount)
    {
        EntryPoint = entryPoint;
        HitGroupName = hitGroupName;
        ParameterBlockOffset = parameterBlockOffset;
        ParameterBlockCount = parameterBlockCount;
    }

    public EntryPointReflection EntryPoint { get; }
    public string? HitGroupName { get; }
    public uint ParameterBlockOffset { get; }
    public uint ParameterBlockCount { get; }

    public static RayTracingShaderRecord Entry(
        EntryPointReflection entryPoint,
        uint parameterBlockOffset,
        uint parameterBlockCount) =>
        new(
            entryPoint,
            null,
            parameterBlockOffset,
            parameterBlockCount);

    public static RayTracingShaderRecord HitGroup(
        string hitGroupName,
        uint parameterBlockOffset,
        uint parameterBlockCount) =>
        new(
            EntryPointReflection.Null,
            hitGroupName ?? throw new ArgumentNullException(nameof(hitGroupName)),
            parameterBlockOffset,
            parameterBlockCount);
}

/// <remarks>
/// <para><b>Thread safety:</b> Externally synchronized. This type has no Dispose operation.</para>
/// <para><b>Ownership:</b> Stack-only description or view; it owns no referenced RHI object and receiver calls consume every Span synchronously.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state; borrowed storage remains caller-owned.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public readonly ref struct RayTracingShaderTableUpdate
{
    public RayTracingShaderTableUpdate(
        ReadOnlySpan<RayTracingShaderRecord> rayGeneration,
        ReadOnlySpan<RayTracingShaderRecord> miss,
        ReadOnlySpan<RayTracingShaderRecord> hit,
        ReadOnlySpan<RayTracingShaderRecord> callable,
        ReadOnlySpan<RayTracingLocalParameterBlock> parameterBlocks,
        ReadOnlySpan<ResourceBinding> resources,
        ReadOnlySpan<byte> ordinaryData)
    {
        RayGeneration = rayGeneration;
        Miss = miss;
        Hit = hit;
        Callable = callable;
        ParameterBlocks = parameterBlocks;
        Resources = resources;
        OrdinaryData = ordinaryData;
    }

    public ReadOnlySpan<RayTracingShaderRecord> RayGeneration { get; }
    public ReadOnlySpan<RayTracingShaderRecord> Miss { get; }
    public ReadOnlySpan<RayTracingShaderRecord> Hit { get; }
    public ReadOnlySpan<RayTracingShaderRecord> Callable { get; }
    public ReadOnlySpan<RayTracingLocalParameterBlock> ParameterBlocks { get; }
    public ReadOnlySpan<ResourceBinding> Resources { get; }
    public ReadOnlySpan<byte> OrdinaryData { get; }
}

/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe. Immutable values may be shared; referenced RHI objects retain their own contracts.</para>
/// <para><b>Ownership:</b> Pure managed value; copying it does not transfer ownership of any referenced RHI object.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state; referenced objects retain their own terminal state.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public readonly record struct DispatchRaysDesc(
    RayTracingShaderTable ShaderTable,
    uint Width,
    uint Height = 1,
    uint Depth = 1);
