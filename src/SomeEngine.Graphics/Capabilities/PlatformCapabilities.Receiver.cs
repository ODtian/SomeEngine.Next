using System.Runtime.CompilerServices;

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

public sealed partial class Graphics<TBackend>
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public CalibratedTimestampInfo CalibrateTimestamps(Queue queue) =>
        Receiver.CalibrateTimestamps(queue);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Buffer ImportBuffer(
        Device device,
        ExternalHandle handle,
        in BufferDesc desc,
        in ImportedResourceState state) =>
        Receiver.ImportBuffer(device, handle, desc, state);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Texture ImportTexture(
        Device device,
        ExternalHandle handle,
        in TextureDesc desc,
        in ImportedResourceState state) =>
        Receiver.ImportTexture(device, handle, desc, state);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Heap ImportHeap(
        Device device,
        ExternalHandle handle,
        in HeapDesc desc) =>
        Receiver.ImportHeap(device, handle, desc);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ExternalHandle ExportBuffer(Buffer buffer, ExternalHandleType type) =>
        Receiver.ExportBuffer(buffer, type);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ExternalHandle ExportTexture(Texture texture, ExternalHandleType type) =>
        Receiver.ExportTexture(texture, type);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ExternalHandle ExportHeap(Heap heap, ExternalHandleType type) =>
        Receiver.ExportHeap(heap, type);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ExternalTimeline CreateExternalTimeline(
        Device device,
        ulong initialValue,
        string? label = null) =>
        Receiver.CreateExternalTimeline(device, initialValue, label);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ExternalTimeline ImportTimeline(
        Device device,
        ExternalHandle handle,
        string? label = null) =>
        Receiver.ImportTimeline(device, handle, label);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ExternalHandle ExportTimeline(ExternalTimeline timeline, ExternalHandleType type) =>
        Receiver.ExportTimeline(timeline, type);
}
