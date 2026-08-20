namespace SomeEngine.Graphics;

public partial interface IGraphicsBackend
{
    Buffer CreateReservedBuffer(Device device, in BufferDesc desc);
    Texture CreateReservedTexture(Device device, in TextureDesc desc);
    SparseResourceInfo GetSparseResourceInfo(Resource resource);
    QueueCompletion UpdateSparseMappings(
        Queue queue,
        ReadOnlySpan<SparseMappingDesc> mappings);
    QueueCompletion CopySparseMappings(
        Queue queue,
        ReadOnlySpan<SparseMappingCopyDesc> copies);
}
