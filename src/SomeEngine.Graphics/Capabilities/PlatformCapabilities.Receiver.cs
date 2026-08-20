namespace SomeEngine.Graphics;

public partial interface IGraphicsBackend
{
    CalibratedTimestampInfo CalibrateTimestamps(Queue queue);

    Buffer ImportBuffer(
        Device device,
        ExternalHandle handle,
        in BufferDesc desc,
        in ImportedResourceState state);

    Texture ImportTexture(
        Device device,
        ExternalHandle handle,
        in TextureDesc desc,
        in ImportedResourceState state);

    Heap ImportHeap(
        Device device,
        ExternalHandle handle,
        in HeapDesc desc);

    ExternalHandle ExportBuffer(Buffer buffer, ExternalHandleType type);
    ExternalHandle ExportTexture(Texture texture, ExternalHandleType type);
    ExternalHandle ExportHeap(Heap heap, ExternalHandleType type);

    ExternalTimeline CreateExternalTimeline(
        Device device,
        ulong initialValue,
        string? label = null);

    ExternalTimeline ImportTimeline(
        Device device,
        ExternalHandle handle,
        string? label = null);

    ExternalHandle ExportTimeline(ExternalTimeline timeline, ExternalHandleType type);
}
