namespace SomeEngine.Graphics;

public partial interface IGraphicsBackend
{
    AccelerationStructure CreateAccelerationStructure(
        Device device,
        Buffer storage,
        in BufferRange storageRange,
        AccelerationStructureType type,
        string? label = null);

    AccelerationStructureSrv CreateAccelerationStructureSrv(
        Device device,
        in AccelerationStructureSrvDesc desc);

    /// <summary>Synchronously encodes top-level instances into caller-owned CPU memory.</summary>
    /// <remarks>
    /// Requires a live Device with RayTracing support and live bottom-level structures on that Device.
    /// Structures may be encoded before their GPU build; complete their build before building the top level.
    /// All inputs and destination capacity are checked before any bytes are written. Exactly
    /// instances.Length * RayTracing.InstanceSize bytes are written; the destination tail is unchanged.
    /// Empty input writes nothing. The encoded bytes are specific to this Device/backend, not portable data.
    /// No spans or object lifetimes are retained and no GPU work is submitted. The caller uploads the
    /// bytes into AccelerationStructureInput storage at RayTracing.InstanceAlignment and supplies it
    /// as Instances geometry. The caller must keep every referenced bottom-level structure and its
    /// storage alive until all dependent top-level structures and ray-tracing uses have finished.
    /// Inputs and destination must not be mutated concurrently, and normal use must not race disposal.
    /// </remarks>
    void EncodeAccelerationStructureInstances(
        Device device,
        ReadOnlySpan<AccelerationStructureInstance> instances,
        Span<byte> destination);

    AccelerationStructureBuildInfo GetAccelerationStructureBuildInfo(
        Device device,
        AccelerationStructureType type,
        AccelerationStructureBuildOptions options,
        ReadOnlySpan<AccelerationStructureGeometry> geometries);

    Pipeline CreateRayTracingPipeline(
        Device device,
        in RayTracingPipelineDesc desc,
        PipelineCache? cache = null);

    /// <summary>
    /// Creates a ray-tracing Pipeline asynchronously. Successful completion means the returned
    /// state object is ready for shader-table creation and command recording.
    /// </summary>
    Task<Pipeline> CreateRayTracingPipelineAsync(
        Device device,
        in RayTracingPipelineDesc desc,
        PipelineCache? cache = null);

    RayTracingShaderTable CreateRayTracingShaderTable(
        Device device,
        in RayTracingShaderTableDesc desc);

    void BuildAccelerationStructure(
        CommandContext context,
        in AccelerationStructureBuildDesc desc);

    void CopyAccelerationStructure(
        CommandContext context,
        AccelerationStructure destination,
        AccelerationStructure source,
        AccelerationStructureCopyType type);

    void SerializeAccelerationStructure(
        CommandContext context,
        in BufferRegion destination,
        AccelerationStructure source);

    void DeserializeAccelerationStructure(
        CommandContext context,
        AccelerationStructure destination,
        in BufferRegion source);

    void EmitAccelerationStructurePostBuildInfo(
        CommandContext context,
        AccelerationStructure source,
        AccelerationStructurePostBuildInfoType type,
        Buffer destination,
        ulong destinationOffset);

    void UpdateRayTracingShaderTable(
        CommandContext context,
        RayTracingShaderTable table,
        in RayTracingShaderTableUpdate update);

    void DispatchRays(CommandContext context, in DispatchRaysDesc desc);
}
