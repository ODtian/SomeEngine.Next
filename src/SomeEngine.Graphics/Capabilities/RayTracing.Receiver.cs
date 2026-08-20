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
