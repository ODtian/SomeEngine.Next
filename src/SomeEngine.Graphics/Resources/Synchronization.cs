namespace SomeEngine.Graphics;

[Flags]
public enum PipelineSync : ulong
{
    None = 0,
    Draw = 1UL << 0,
    IndexInput = 1UL << 1,
    VertexShading = 1UL << 2,
    PixelShading = 1UL << 3,
    DepthStencil = 1UL << 4,
    RenderTarget = 1UL << 5,
    ComputeShading = 1UL << 6,
    RayTracing = 1UL << 7,
    Copy = 1UL << 8,
    Resolve = 1UL << 9,
    ExecuteIndirect = 1UL << 10,
    Predication = 1UL << 11,
    AllShading = 1UL << 12,
    NonPixelShading = 1UL << 13,
    Clear = 1UL << 14,
    AccelerationStructureCopy = 1UL << 15,
    EmitAccelerationStructurePostBuildInfo = 1UL << 16,
    BuildRayTracingAccelerationStructure = 1UL << 17,
    CopyRayTracingAccelerationStructure = 1UL << 18,
    Split = 1UL << 19,
    All = ulong.MaxValue,
}

[Flags]
public enum ResourceAccess : ulong
{
    NoAccess = 0,
    Common = 1UL << 0,
    VertexBuffer = 1UL << 1,
    ConstantBuffer = 1UL << 2,
    IndexBuffer = 1UL << 3,
    RenderTarget = 1UL << 4,
    UnorderedAccess = 1UL << 5,
    DepthStencilWrite = 1UL << 6,
    DepthStencilRead = 1UL << 7,
    ShaderResource = 1UL << 8,
    StreamOutput = 1UL << 9,
    IndirectArgument = 1UL << 10,
    Predication = 1UL << 11,
    CopyDestination = 1UL << 12,
    CopySource = 1UL << 13,
    ResolveDestination = 1UL << 14,
    ResolveSource = 1UL << 15,
    RayTracingAccelerationStructureRead = 1UL << 16,
    RayTracingAccelerationStructureWrite = 1UL << 17,
    ShadingRateSource = 1UL << 18,
}

public enum TextureLayout : byte
{
    Undefined,
    Common,
    Present,
    RenderTarget,
    UnorderedAccess,
    DepthStencilWrite,
    DepthStencilRead,
    ShaderResource,
    CopySource,
    CopyDestination,
    ResolveSource,
    ResolveDestination,
    ShadingRateSource,
    QueueCommon,
}
