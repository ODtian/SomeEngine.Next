namespace SomeEngine.Graphics;

public enum QueryType : byte
{
    Timestamp,
    Occlusion,
    PipelineStatistics,
}

public readonly record struct QueryPoolDesc(QueryType Type, uint Count, string? Name = null)
{
    public uint ResultSize => Type == QueryType.PipelineStatistics
        ? PipelineStatisticsValues.ByteSize
        : sizeof(ulong);

    public void Validate()
    {
        if (!Enum.IsDefined(Type)) throw new ArgumentOutOfRangeException(nameof(Type));
        if (Count == 0) throw new ArgumentOutOfRangeException(nameof(Count));
    }
}

/// <summary>Immutable shape of one exact live query-pool handle.</summary>
public readonly record struct QueryPoolMetadata(QueryType Type, uint Count, uint ResultSize);

public readonly record struct TimestampCalibration(
    QueueType Queue,
    ulong CpuTimestamp,
    ulong GpuTimestamp,
    ulong TimestampFrequency);

/// <summary>The portable, fixed-width payload returned by a pipeline-statistics query.</summary>
public readonly record struct PipelineStatisticsValues(
    ulong InputAssemblyVertices,
    ulong InputAssemblyPrimitives,
    ulong VertexShaderInvocations,
    ulong GeometryShaderInvocations,
    ulong GeometryShaderPrimitives,
    ulong ClippingInvocations,
    ulong ClippingPrimitives,
    ulong PixelShaderInvocations,
    ulong HullShaderInvocations,
    ulong DomainShaderInvocations,
    ulong ComputeShaderInvocations)
{
    public const uint ByteSize = 11 * sizeof(ulong);
}
