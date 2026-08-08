using System.Runtime.CompilerServices;

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

public sealed partial class Graphics<TBackend>
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Buffer CreateReservedBuffer(Device device, in BufferDesc desc) =>
        Receiver.CreateReservedBuffer(device, desc);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Texture CreateReservedTexture(Device device, in TextureDesc desc) =>
        Receiver.CreateReservedTexture(device, desc);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public SparseResourceInfo GetSparseResourceInfo(Resource resource) =>
        Receiver.GetSparseResourceInfo(resource);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public QueueCompletion UpdateSparseMappings(
        Queue queue,
        ReadOnlySpan<SparseMappingDesc> mappings) =>
        Receiver.UpdateSparseMappings(queue, mappings);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public QueueCompletion CopySparseMappings(
        Queue queue,
        ReadOnlySpan<SparseMappingCopyDesc> copies) =>
        Receiver.CopySparseMappings(queue, copies);
}
