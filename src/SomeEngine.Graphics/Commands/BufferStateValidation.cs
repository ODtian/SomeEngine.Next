namespace SomeEngine.Graphics;

/// <summary>
/// Describes the API-visible state contract of buffers backed by fixed-state CPU-visible heaps.
/// D3D12 exposes upload heaps as GENERIC_READ, which is a set of read states rather than one
/// transitionable state; readback heaps are permanently COPY_DEST.
/// </summary>
internal static class BufferStateValidation
{
    public static bool HasFixedState(MemoryType memoryType) =>
        memoryType is MemoryType.Upload or MemoryType.Readback;

    public static bool IsFixedState(MemoryType memoryType, ResourceState state) => memoryType switch
    {
        MemoryType.Upload => IsGenericReadState(state),
        MemoryType.Readback => state == ResourceState.CopyDestination,
        _ => false,
    };

    public static bool Satisfies(MemoryType memoryType, ResourceState actual, ResourceState required) =>
        memoryType switch
        {
            MemoryType.Upload => IsGenericReadState(actual) && IsGenericReadState(required),
            MemoryType.Readback => actual == ResourceState.CopyDestination && required == ResourceState.CopyDestination,
            _ => actual == required,
        };

    public static string DescribeFixedState(MemoryType memoryType) => memoryType switch
    {
        MemoryType.Upload =>
            "GENERIC_READ (CopySource, ShaderResource, VertexOrConstantBuffer, IndexBuffer, or IndirectArgument)",
        MemoryType.Readback => "CopyDestination",
        _ => throw new ArgumentOutOfRangeException(nameof(memoryType)),
    };

    private static bool IsGenericReadState(ResourceState state) => state is
        ResourceState.CopySource or
        ResourceState.ShaderResource or
        ResourceState.VertexOrConstantBuffer or
        ResourceState.IndexBuffer or
        ResourceState.IndirectArgument;
}
