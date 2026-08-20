namespace SomeEngine.Graphics.Validation;

public sealed partial class ValidationLayer
{
    public MemoryRequirements GetBufferMemoryRequirements(
        Device device,
        in BufferDesc desc,
        MemoryType memoryType = MemoryType.DeviceLocal)
    {
        RequireDevice(device);
        return Backend.GetBufferMemoryRequirements(device, desc, memoryType);
    }

    public MemoryRequirements GetTextureMemoryRequirements(Device device, in TextureDesc desc)
    {
        RequireDevice(device);
        return Backend.GetTextureMemoryRequirements(device, desc);
    }

    public TextureCopyFootprint GetTextureCopyFootprint(
        Device device,
        in TextureDesc desc,
        in BufferTextureCopy copy,
        ulong requestedBufferOffset = 0)
    {
        RequireDevice(device);
        return Backend.GetTextureCopyFootprint(device, desc, copy, requestedBufferOffset);
    }

    public Heap CreateHeap(Device device, in HeapDesc desc)
    {
        RequireDevice(device);
        var metadata = new HeapValidationState(desc.VisibleNodeMask);
        HeapDesc createDesc = desc;
        var objectInfo = new ValidationObjectInfo(device);
        lock (_gate)
        {
            _objects.EnsureAdditionalCapacity();
            _heapStates.EnsureAdditionalCapacity();
            Heap? result = null;
            bool objectAdded = false;
            bool metadataAdded = false;
            try
            {
                result = Backend.CreateHeap(device, createDesc);
                _objects.Add(result, objectInfo);
                objectAdded = true;
                _heapStates.Add(result, metadata);
                metadataAdded = true;
                return result;
            }
            catch
            {
                if (metadataAdded)
                    _heapStates.Remove(result!);
                if (objectAdded)
                    _objects.Remove(result!);
                result?.Dispose();
                throw;
            }
        }
    }

    public Buffer CreateBuffer(
        Device device,
        in BufferDesc desc,
        MemoryType memoryType = MemoryType.DeviceLocal)
    {
        RequireDevice(device);
        var state = new ResourceValidationState(buffer: true);
        BufferDesc createDesc = desc;
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
                result = Backend.CreateBuffer(device, createDesc, memoryType);
                state.Bind(result);
                _objects.Add(result, objectInfo);
                objectAdded = true;
                _resourceStates.Add(result, state);
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

    public Buffer CreatePlacedBuffer(Device device, Heap heap, ulong offset, in BufferDesc desc)
    {
        RequireDevice(device);
        RequireOnDevice(device, heap, "Heap");
        var state = new ResourceValidationState(buffer: true);
        BufferDesc createDesc = desc;
        var objectInfo = new ValidationObjectInfo(heap);
        lock (_gate)
        {
            _objects.EnsureAdditionalCapacity();
            _resourceStates.EnsureAdditionalCapacity();
            Buffer? result = null;
            bool objectAdded = false;
            bool stateAdded = false;
            try
            {
                result = Backend.CreatePlacedBuffer(device, heap, offset, createDesc);
                state.Bind(result);
                _objects.Add(result, objectInfo);
                objectAdded = true;
                _resourceStates.Add(result, state);
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

    public Texture CreateTexture(Device device, in TextureDesc desc)
    {
        RequireDevice(device);
        var state = new ResourceValidationState(buffer: false);
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
                result = Backend.CreateTexture(device, desc);
                state.Bind(result);
                _objects.Add(result, objectInfo);
                objectAdded = true;
                _resourceStates.Add(result, state);
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

    public Texture CreatePlacedTexture(Device device, Heap heap, ulong offset, in TextureDesc desc)
    {
        RequireDevice(device);
        RequireOnDevice(device, heap, "Heap");
        var state = new ResourceValidationState(buffer: false);
        var objectInfo = new ValidationObjectInfo(heap);
        lock (_gate)
        {
            _objects.EnsureAdditionalCapacity();
            _resourceStates.EnsureAdditionalCapacity();
            Texture? result = null;
            bool objectAdded = false;
            bool stateAdded = false;
            try
            {
                result = Backend.CreatePlacedTexture(device, heap, offset, desc);
                state.Bind(result);
                _objects.Add(result, objectInfo);
                objectAdded = true;
                _resourceStates.Add(result, state);
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

    public BufferCbv CreateBufferCbv(Device device, in BufferCbvDesc desc)
    {
        RequireDevice(device);
        RequireOnDevice(device, desc.Buffer, "Buffer");
        BufferCbvDesc createDesc = desc;
        var objectInfo = new ValidationObjectInfo(desc.Buffer);
        lock (_gate)
        {
            _objects.EnsureAdditionalCapacity();
            BufferCbv? result = null;
            bool objectAdded = false;
            try
            {
                result = Backend.CreateBufferCbv(device, createDesc);
                _objects.Add(result, objectInfo);
                objectAdded = true;
                return result;
            }
            catch
            {
                if (objectAdded)
                    _objects.Remove(result!);
                result?.Dispose();
                throw;
            }
        }
    }

    public BufferSrv CreateBufferSrv(Device device, in BufferSrvDesc desc)
    {
        RequireDevice(device);
        RequireOnDevice(device, desc.Buffer, "Buffer");
        BufferSrvDesc createDesc = desc;
        var objectInfo = new ValidationObjectInfo(desc.Buffer);
        lock (_gate)
        {
            _objects.EnsureAdditionalCapacity();
            BufferSrv? result = null;
            bool objectAdded = false;
            try
            {
                result = Backend.CreateBufferSrv(device, createDesc);
                _objects.Add(result, objectInfo);
                objectAdded = true;
                return result;
            }
            catch
            {
                if (objectAdded)
                    _objects.Remove(result!);
                result?.Dispose();
                throw;
            }
        }
    }

    public BufferUav CreateBufferUav(Device device, in BufferUavDesc desc)
    {
        RequireDevice(device);
        RequireOnDevice(device, desc.Buffer, "Buffer");
        if (desc.CounterBuffer is not null)
            RequireOnDevice(device, desc.CounterBuffer, "Counter Buffer");
        BufferUavDesc createDesc = desc;
        var objectInfo = new ValidationObjectInfo(desc.Buffer);
        lock (_gate)
        {
            _objects.EnsureAdditionalCapacity();
            BufferUav? result = null;
            bool objectAdded = false;
            try
            {
                result = Backend.CreateBufferUav(device, createDesc);
                _objects.Add(result, objectInfo);
                objectAdded = true;
                return result;
            }
            catch
            {
                if (objectAdded)
                    _objects.Remove(result!);
                result?.Dispose();
                throw;
            }
        }
    }

    public TextureSrv CreateTextureSrv(Device device, in TextureSrvDesc desc)
    {
        RequireDevice(device);
        RequireOnDevice(device, desc.Texture, "Texture");
        TextureSrvDesc createDesc = desc;
        var objectInfo = new ValidationObjectInfo(desc.Texture);
        lock (_gate)
        {
            _objects.EnsureAdditionalCapacity();
            TextureSrv? result = null;
            bool objectAdded = false;
            try
            {
                result = Backend.CreateTextureSrv(device, createDesc);
                _objects.Add(result, objectInfo);
                objectAdded = true;
                return result;
            }
            catch
            {
                if (objectAdded)
                    _objects.Remove(result!);
                result?.Dispose();
                throw;
            }
        }
    }

    public TextureUav CreateTextureUav(Device device, in TextureUavDesc desc)
    {
        RequireDevice(device);
        RequireOnDevice(device, desc.Texture, "Texture");
        TextureUavDesc createDesc = desc;
        var objectInfo = new ValidationObjectInfo(desc.Texture);
        lock (_gate)
        {
            _objects.EnsureAdditionalCapacity();
            TextureUav? result = null;
            bool objectAdded = false;
            try
            {
                result = Backend.CreateTextureUav(device, createDesc);
                _objects.Add(result, objectInfo);
                objectAdded = true;
                return result;
            }
            catch
            {
                if (objectAdded)
                    _objects.Remove(result!);
                result?.Dispose();
                throw;
            }
        }
    }

    public ColorAttachmentView CreateColorAttachmentView(
        Device device,
        in ColorAttachmentViewDesc desc)
    {
        RequireDevice(device);
        RequireOnDevice(device, desc.Texture, "Texture");
        ColorAttachmentViewDesc createDesc = desc;
        var objectInfo = new ValidationObjectInfo(desc.Texture);
        lock (_gate)
        {
            _objects.EnsureAdditionalCapacity();
            ColorAttachmentView? result = null;
            bool objectAdded = false;
            try
            {
                result = Backend.CreateColorAttachmentView(device, createDesc);
                _objects.Add(result, objectInfo);
                objectAdded = true;
                return result;
            }
            catch
            {
                if (objectAdded)
                    _objects.Remove(result!);
                result?.Dispose();
                throw;
            }
        }
    }

    public DepthStencilView CreateDepthStencilView(
        Device device,
        in DepthStencilViewDesc desc)
    {
        RequireDevice(device);
        RequireOnDevice(device, desc.Texture, "Texture");
        DepthStencilViewDesc createDesc = desc;
        var objectInfo = new ValidationObjectInfo(desc.Texture);
        lock (_gate)
        {
            _objects.EnsureAdditionalCapacity();
            DepthStencilView? result = null;
            bool objectAdded = false;
            try
            {
                result = Backend.CreateDepthStencilView(device, createDesc);
                _objects.Add(result, objectInfo);
                objectAdded = true;
                return result;
            }
            catch
            {
                if (objectAdded)
                    _objects.Remove(result!);
                result?.Dispose();
                throw;
            }
        }
    }

    public Sampler CreateSampler(Device device, in SamplerDesc desc)
    {
        RequireDevice(device);
        SamplerDesc createDesc = desc;
        var objectInfo = new ValidationObjectInfo(device);
        lock (_gate)
        {
            _objects.EnsureAdditionalCapacity();
            Sampler? result = null;
            bool objectAdded = false;
            try
            {
                result = Backend.CreateSampler(device, createDesc);
                _objects.Add(result, objectInfo);
                objectAdded = true;
                return result;
            }
            catch
            {
                if (objectAdded)
                    _objects.Remove(result!);
                result?.Dispose();
                throw;
            }
        }
    }

    public MappedBuffer Map(Buffer buffer, MapType type, in BufferRange range)
    {
        Require(buffer);
        return Backend.Map(buffer, type, range);
    }

    private void RequireSameDevice(Device expected, Device actual, string objectType)
    {
        if (!ReferenceEquals(expected, actual))
            Reject("Ownership", $"{objectType} belongs to another Device.");
    }

}
