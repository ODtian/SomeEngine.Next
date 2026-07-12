namespace SomeEngine.Graphics;

/// <summary>Portable argument record consumed by <see cref="ICommandContext.DrawIndirect"/>.</summary>
public readonly record struct DrawIndirectArguments(
    uint VertexCountPerInstance,
    uint InstanceCount,
    uint StartVertexLocation,
    uint StartInstanceLocation)
{
    public const uint ByteSize = 16;
}

/// <summary>Portable argument record consumed by <see cref="ICommandContext.DrawIndexedIndirect"/>.</summary>
public readonly record struct DrawIndexedIndirectArguments(
    uint IndexCountPerInstance,
    uint InstanceCount,
    uint StartIndexLocation,
    int BaseVertexLocation,
    uint StartInstanceLocation)
{
    public const uint ByteSize = 20;
}

/// <summary>Portable argument record consumed by <see cref="ICommandContext.DispatchIndirect"/>.</summary>
public readonly record struct DispatchIndirectArguments(
    uint ThreadGroupCountX,
    uint ThreadGroupCountY,
    uint ThreadGroupCountZ)
{
    public const uint ByteSize = 12;
}
