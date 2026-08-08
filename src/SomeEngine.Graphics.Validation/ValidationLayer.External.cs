namespace SomeEngine.Graphics.Validation;

public sealed partial class ValidationLayer<TBackend>
{
    public CalibratedTimestampInfo CalibrateTimestamps(Queue queue)
    {
        RequireQueue(queue);
        RequireCapability<CalibratedTimestamps>(queue.Device);
        return Backend.CalibrateTimestamps(queue);
    }

    public Buffer ImportBuffer(
        Device device,
        ExternalHandle handle,
        in BufferDesc desc,
        in ImportedResourceState state)
    {
        RequireCapability<ExternalResources>(device);
        _ = handle.Value;
        return Track(Backend.ImportBuffer(device, handle, desc, state), device);
    }

    public Texture ImportTexture(
        Device device,
        ExternalHandle handle,
        in TextureDesc desc,
        in ImportedResourceState state)
    {
        RequireCapability<ExternalResources>(device);
        _ = handle.Value;
        return Track(Backend.ImportTexture(device, handle, desc, state), device);
    }

    public Heap ImportHeap(
        Device device,
        ExternalHandle handle,
        in HeapDesc desc)
    {
        RequireCapability<ExternalResources>(device);
        _ = handle.Value;
        return Track(Backend.ImportHeap(device, handle, desc), device);
    }

    public ExternalHandle ExportBuffer(Buffer buffer, ExternalHandleType type)
    {
        Require(buffer);
        RequireCapability<ExternalResources>(buffer.Device);
        return Backend.ExportBuffer(buffer, type);
    }

    public ExternalHandle ExportTexture(Texture texture, ExternalHandleType type)
    {
        Require(texture);
        RequireCapability<ExternalResources>(texture.Device);
        return Backend.ExportTexture(texture, type);
    }

    public ExternalHandle ExportHeap(Heap heap, ExternalHandleType type)
    {
        Require(heap);
        RequireCapability<ExternalResources>(heap.Device);
        return Backend.ExportHeap(heap, type);
    }

    public ExternalTimeline CreateExternalTimeline(
        Device device,
        ulong initialValue,
        string? label = null)
    {
        RequireCapability<ExternalTimelines>(device);
        ExternalTimeline timeline = Track(
            Backend.CreateExternalTimeline(device, initialValue, label),
            device);
        _timelines.Add(timeline, new TimelineValidationState(true, initialValue));
        return timeline;
    }

    public ExternalTimeline ImportTimeline(
        Device device,
        ExternalHandle handle,
        string? label = null)
    {
        RequireCapability<ExternalTimelines>(device);
        _ = handle.Value;
        ExternalTimeline timeline = Track(
            Backend.ImportTimeline(device, handle, label),
            device);
        _timelines.Add(timeline, new TimelineValidationState(false, 0));
        return timeline;
    }

    public ExternalHandle ExportTimeline(ExternalTimeline timeline, ExternalHandleType type)
    {
        Require(timeline);
        RequireCapability<ExternalTimelines>(timeline.Device);
        return Backend.ExportTimeline(timeline, type);
    }
}
