namespace SomeEngine.Graphics;

public enum QueryType : byte
{
    Timestamp,
    Occlusion,
    BinaryOcclusion,
    PipelineStatistics,
    StreamOutputStatistics,
}

public readonly record struct QueryPoolDesc(
    QueryType Type,
    QueueType QueueType,
    uint Count,
    uint StreamIndex = 0,
    string? Label = null);

public readonly record struct QueryResultInfo(
    uint ElementSize,
    uint ElementAlignment,
    uint ResultStride);

public abstract class QueryPool : DeviceResource
{
    internal QueryPool(
        Device device,
        in QueryPoolDesc description,
        in QueryResultInfo resultInfo)
        : base(device, description.Label)
    {
        Description = description;
        ResultInfo = resultInfo;
    }

    public QueryPoolDesc Description { get; }
    public QueryResultInfo ResultInfo { get; }
}

public readonly record struct PipelineStatistics(
    ulong InputAssemblerVertices,
    ulong InputAssemblerPrimitives,
    ulong VertexShaderInvocations,
    ulong GeometryShaderInvocations,
    ulong GeometryShaderPrimitives,
    ulong ClippingInvocations,
    ulong ClippingPrimitives,
    ulong PixelShaderInvocations,
    ulong HullShaderInvocations,
    ulong DomainShaderInvocations,
    ulong ComputeShaderInvocations);

public readonly record struct StreamOutputStatistics(
    ulong PrimitivesWritten,
    ulong StorageNeeded);
