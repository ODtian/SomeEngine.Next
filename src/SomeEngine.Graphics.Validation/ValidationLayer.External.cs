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
        ExternalResources capability = RequireCapability<ExternalResources>(device);
        ValidateExternalHandleType(capability.SupportsBufferImport(handle.Type), handle.Type);
        ValidateBufferDescription(device, desc, MemoryType.DeviceLocal);
        ValidateImportedResourceState(state, texture: false);
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
        ExternalResources capability = RequireCapability<ExternalResources>(device);
        ValidateExternalHandleType(capability.SupportsTextureImport(handle.Type), handle.Type);
        ValidateTextureDescription(device, desc);
        ValidateImportedResourceState(state, texture: true);
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
        ExternalResources capability = RequireCapability<ExternalResources>(device);
        ValidateExternalHandleType(capability.SupportsHeapImport(handle.Type), handle.Type);
        ValidateHeapDescription(device, desc);
        if ((desc.Flags & HeapFlags.Shareable) == 0)
            throw new ArgumentException("An imported Heap requires Shareable flags.", nameof(desc));
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
        ExternalResources capability = RequireCapability<ExternalResources>(buffer.Device);
        ValidateExternalHandleType(capability.SupportsBufferExport(type), type);
        if ((buffer.Info.Usages & BufferUsages.Shareable) == 0)
            throw new ArgumentException("The Buffer requires Shareable usage.", nameof(buffer));
        return Backend.ExportBuffer(buffer, type);
    }

    public ExternalHandle ExportTexture(Texture texture, ExternalHandleType type)
    {
        Require(texture);
        ExternalResources capability = RequireCapability<ExternalResources>(texture.Device);
        ValidateExternalHandleType(capability.SupportsTextureExport(type), type);
        if ((texture.Info.Usages & TextureUsages.Shareable) == 0)
            throw new ArgumentException("The Texture requires Shareable usage.", nameof(texture));
        return Backend.ExportTexture(texture, type);
    }

    public ExternalHandle ExportHeap(Heap heap, ExternalHandleType type)
    {
        Require(heap);
        ExternalResources capability = RequireCapability<ExternalResources>(heap.Device);
        ValidateExternalHandleType(capability.SupportsHeapExport(type), type);
        if ((heap.Info.Flags & HeapFlags.Shareable) == 0)
            throw new ArgumentException("The Heap requires Shareable flags.", nameof(heap));
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
        ExternalTimelines capability = RequireCapability<ExternalTimelines>(device);
        ValidateExternalHandleType(capability.SupportsImport(handle.Type), handle.Type);
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
        ExternalTimelines capability = RequireCapability<ExternalTimelines>(timeline.Device);
        ValidateExternalHandleType(capability.SupportsExport(type), type);
        return Backend.ExportTimeline(timeline, type);
    }

    private static void ValidateExternalHandleType(bool supported, ExternalHandleType type)
    {
        if (!Enum.IsDefined(type))
            throw new ArgumentOutOfRangeException(nameof(type));
        if (!supported)
            throw new NotSupportedException($"External handle type {type} is unavailable for this operation.");
    }

    private static void ValidateImportedResourceState(in ImportedResourceState state, bool texture)
    {
        if (!Enum.IsDefined(state.QueueType) || state.Layout is TextureLayout layout && !Enum.IsDefined(layout))
            throw new ArgumentOutOfRangeException(nameof(state));
        if (texture != state.Layout.HasValue)
            throw new ArgumentException("Imported Texture state requires a layout; Buffer state contains sync and access.", nameof(state));
    }
}
