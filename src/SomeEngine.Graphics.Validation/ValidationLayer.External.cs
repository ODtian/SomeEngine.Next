namespace SomeEngine.Graphics.Validation;

public sealed partial class ValidationLayer
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
        var resourceState = new ResourceValidationState(buffer: true);
        BufferDesc createDesc = desc;
        ImportedResourceState importState = state;
        var objectInfo = new ValidationObjectInfo(device);
        lock (_gate)
        {
            _objects.EnsureAdditionalCapacity();
            _resourceStates.EnsureAdditionalCapacity();
            Buffer? result = null;
            bool objectAdded = false;
            bool stateAdded = false;
            try
            {
                result = Backend.ImportBuffer(device, handle, createDesc, importState);
                resourceState.Bind(result);
                _objects.Add(result, objectInfo);
                objectAdded = true;
                _resourceStates.Add(result, resourceState);
                stateAdded = true;
                return result;
            }
            catch
            {
                if (stateAdded)
                    _resourceStates.Remove(result!);
                if (objectAdded)
                    _objects.Remove(result!);
                result?.Dispose();
                throw;
            }
        }
    }

    public Texture ImportTexture(
        Device device,
        ExternalHandle handle,
        in TextureDesc desc,
        in ImportedResourceState state)
    {
        RequireCapability<ExternalResources>(device);
        _ = handle.Value;
        var resourceState = new ResourceValidationState(buffer: false);
        var objectInfo = new ValidationObjectInfo(device);
        lock (_gate)
        {
            _objects.EnsureAdditionalCapacity();
            _resourceStates.EnsureAdditionalCapacity();
            Texture? result = null;
            bool objectAdded = false;
            bool stateAdded = false;
            try
            {
                result = Backend.ImportTexture(device, handle, desc, state);
                resourceState.Bind(result);
                _objects.Add(result, objectInfo);
                objectAdded = true;
                _resourceStates.Add(result, resourceState);
                stateAdded = true;
                return result;
            }
            catch
            {
                if (stateAdded)
                    _resourceStates.Remove(result!);
                if (objectAdded)
                    _objects.Remove(result!);
                result?.Dispose();
                throw;
            }
        }
    }

    public Heap ImportHeap(
        Device device,
        ExternalHandle handle,
        in HeapDesc desc)
    {
        RequireCapability<ExternalResources>(device);
        _ = handle.Value;
        var metadata = new HeapValidationState(desc.VisibleNodeMask);
        HeapDesc createDesc = desc;
        var objectInfo = new ValidationObjectInfo(device);
        lock (_gate)
        {
            _objects.EnsureAdditionalCapacity();
            _heapStates.EnsureAdditionalCapacity();
            Heap? result = null;
            bool objectAdded = false;
            bool stateAdded = false;
            try
            {
                result = Backend.ImportHeap(device, handle, createDesc);
                _objects.Add(result, objectInfo);
                objectAdded = true;
                _heapStates.Add(result, metadata);
                stateAdded = true;
                return result;
            }
            catch
            {
                if (stateAdded)
                    _heapStates.Remove(result!);
                if (objectAdded)
                    _objects.Remove(result!);
                result?.Dispose();
                throw;
            }
        }
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
        var state = new TimelineValidationState(true, initialValue);
        var objectInfo = new ValidationObjectInfo(device);
        lock (_gate)
        {
            _objects.EnsureAdditionalCapacity();
            _timelines.EnsureAdditionalCapacity();
            ExternalTimeline? result = null;
            bool objectAdded = false;
            bool stateAdded = false;
            try
            {
                result = Backend.CreateExternalTimeline(device, initialValue, label);
                _objects.Add(result, objectInfo);
                objectAdded = true;
                _timelines.Add(result, state);
                stateAdded = true;
                return result;
            }
            catch
            {
                if (stateAdded)
                    _timelines.Remove(result!);
                if (objectAdded)
                    _objects.Remove(result!);
                result?.Dispose();
                throw;
            }
        }
    }

    public ExternalTimeline ImportTimeline(
        Device device,
        ExternalHandle handle,
        string? label = null)
    {
        RequireCapability<ExternalTimelines>(device);
        _ = handle.Value;
        var state = new TimelineValidationState(false, 0);
        var objectInfo = new ValidationObjectInfo(device);
        lock (_gate)
        {
            _objects.EnsureAdditionalCapacity();
            _timelines.EnsureAdditionalCapacity();
            ExternalTimeline? result = null;
            bool objectAdded = false;
            bool stateAdded = false;
            try
            {
                result = Backend.ImportTimeline(device, handle, label);
                _objects.Add(result, objectInfo);
                objectAdded = true;
                _timelines.Add(result, state);
                stateAdded = true;
                return result;
            }
            catch
            {
                if (stateAdded)
                    _timelines.Remove(result!);
                if (objectAdded)
                    _objects.Remove(result!);
                result?.Dispose();
                throw;
            }
        }
    }

    public ExternalHandle ExportTimeline(ExternalTimeline timeline, ExternalHandleType type)
    {
        Require(timeline);
        RequireCapability<ExternalTimelines>(timeline.Device);
        return Backend.ExportTimeline(timeline, type);
    }
}
