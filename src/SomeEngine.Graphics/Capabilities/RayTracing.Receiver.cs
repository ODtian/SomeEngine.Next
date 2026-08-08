using System.Runtime.CompilerServices;

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

    BindlessAccelerationStructureSrv CreateBindlessAccelerationStructureSrv(
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
    void DispatchRaysIndirect(CommandContext context, RayTracingShaderTable table, in BufferRegion arguments);
}

public sealed partial class Graphics<TBackend>
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public AccelerationStructure CreateAccelerationStructure(
        Device device,
        Buffer storage,
        in BufferRange storageRange,
        AccelerationStructureType type,
        string? label = null) =>
        Receiver.CreateAccelerationStructure(device, storage, storageRange, type, label);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public AccelerationStructureSrv CreateAccelerationStructureSrv(
        Device device,
        in AccelerationStructureSrvDesc desc) =>
        Receiver.CreateAccelerationStructureSrv(device, desc);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public BindlessAccelerationStructureSrv CreateBindlessAccelerationStructureSrv(
        Device device,
        in AccelerationStructureSrvDesc desc) =>
        Receiver.CreateBindlessAccelerationStructureSrv(device, desc);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public AccelerationStructureBuildInfo GetAccelerationStructureBuildInfo(
        Device device,
        AccelerationStructureType type,
        AccelerationStructureBuildOptions options,
        ReadOnlySpan<AccelerationStructureGeometry> geometries) =>
        Receiver.GetAccelerationStructureBuildInfo(device, type, options, geometries);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Pipeline CreateRayTracingPipeline(
        Device device,
        in RayTracingPipelineDesc desc,
        PipelineCache? cache = null) =>
        Receiver.CreateRayTracingPipeline(device, desc, cache);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public RayTracingShaderTable CreateRayTracingShaderTable(
        Device device,
        in RayTracingShaderTableDesc desc) =>
        Receiver.CreateRayTracingShaderTable(device, desc);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void BuildAccelerationStructure(
        CommandContext context,
        in AccelerationStructureBuildDesc desc) =>
        Receiver.BuildAccelerationStructure(context, desc);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void CopyAccelerationStructure(
        CommandContext context,
        AccelerationStructure destination,
        AccelerationStructure source,
        AccelerationStructureCopyType type) =>
        Receiver.CopyAccelerationStructure(context, destination, source, type);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SerializeAccelerationStructure(
        CommandContext context,
        in BufferRegion destination,
        AccelerationStructure source) =>
        Receiver.SerializeAccelerationStructure(context, destination, source);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void DeserializeAccelerationStructure(
        CommandContext context,
        AccelerationStructure destination,
        in BufferRegion source) =>
        Receiver.DeserializeAccelerationStructure(context, destination, source);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void EmitAccelerationStructurePostBuildInfo(
        CommandContext context,
        AccelerationStructure source,
        AccelerationStructurePostBuildInfoType type,
        Buffer destination,
        ulong destinationOffset) =>
        Receiver.EmitAccelerationStructurePostBuildInfo(
            context,
            source,
            type,
            destination,
            destinationOffset);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void UpdateRayTracingShaderTable(
        CommandContext context,
        RayTracingShaderTable table,
        in RayTracingShaderTableUpdate update) =>
        Receiver.UpdateRayTracingShaderTable(context, table, update);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void DispatchRays(CommandContext context, in DispatchRaysDesc desc) =>
        Receiver.DispatchRays(context, desc);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void DispatchRaysIndirect(
        CommandContext context,
        RayTracingShaderTable table,
        in BufferRegion arguments) =>
        Receiver.DispatchRaysIndirect(context, table, arguments);
}
