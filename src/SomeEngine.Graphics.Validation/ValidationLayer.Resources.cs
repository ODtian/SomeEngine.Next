namespace SomeEngine.Graphics.Validation;

public sealed partial class ValidationLayer
{
    public MemoryRequirements GetBufferMemoryRequirements(
        Device device,
        in BufferDesc desc,
        MemoryType memoryType = MemoryType.DeviceLocal)
    {
        RequireDevice(device);
        ValidateBufferDescription(device, desc, memoryType);
        return Backend.GetBufferMemoryRequirements(device, desc, memoryType);
    }

    public MemoryRequirements GetTextureMemoryRequirements(Device device, in TextureDesc desc)
    {
        RequireDevice(device);
        ValidateTextureDescription(device, desc);
        return Backend.GetTextureMemoryRequirements(device, desc);
    }

    public TextureCopyFootprint GetTextureCopyFootprint(
        Device device,
        in TextureDesc desc,
        in BufferTextureCopy copy,
        ulong requestedBufferOffset = 0)
    {
        RequireDevice(device);
        ValidateTextureDescription(device, desc);
        if (requestedBufferOffset % device.Capabilities.Limits.TextureDataPlacementAlignment != 0)
            throw new ArgumentOutOfRangeException(nameof(requestedBufferOffset));
        ValidateTextureRegion(TextureInfoFromDescription(desc), copy.MipLevel, copy.ArrayLayer, copy.Aspect,
            copy.X, copy.Y, copy.Z, copy.Width, copy.Height, copy.Depth);
        return Backend.GetTextureCopyFootprint(device, desc, copy, requestedBufferOffset);
    }

    public Heap CreateHeap(Device device, in HeapDesc desc)
    {
        RequireDevice(device);
        ValidateHeapDescription(device, desc);
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
        ValidateBufferDescription(device, desc, memoryType);
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
        ValidateBufferDescription(device, desc, heap.Info.MemoryType);
        ValidatePlacedResourceNodePlacement(desc.NodePlacement, heap.Info, nameof(desc));
        ValidatePlacement(heap, offset, Backend.GetBufferMemoryRequirements(device, desc, heap.Info.MemoryType),
            HeapFlags.Buffers, (desc.Usages & BufferUsages.Shareable) != 0);
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
        ValidateTextureDescription(device, desc);
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
        if (heap.Info.MemoryType != MemoryType.DeviceLocal) throw new ArgumentException("Textures require device-local heap memory.", nameof(heap));
        ValidateTextureDescription(device, desc);
        ValidatePlacedResourceNodePlacement(desc.NodePlacement, heap.Info, nameof(desc));
        ValidatePlacement(heap, offset, Backend.GetTextureMemoryRequirements(device, desc),
            (desc.Usages & (TextureUsages.ColorAttachment | TextureUsages.DepthStencilAttachment)) != 0 ? HeapFlags.Attachments : HeapFlags.Textures,
            (desc.Usages & TextureUsages.Shareable) != 0);
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
        ValidateConstantBufferRange(device, desc.Buffer, desc.Range);
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
        ValidateBufferView(desc.Buffer, desc.Range, BufferUsages.ShaderRead, desc.Format, desc.StructureStride);
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
        ValidateBufferView(desc.Buffer, desc.Range, BufferUsages.ShaderWrite, desc.Format, desc.StructureStride);
        ValidateCounter(device, desc);
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
        ValidateTextureView(desc.Texture, desc.Range, desc.Format, desc.Dimension, TextureViewKind.ShaderResource);
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
        ValidateTextureView(desc.Texture, desc.Range, desc.Format, desc.Dimension, TextureViewKind.UnorderedAccess);
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
        ValidateTextureView(desc.Texture, desc.Range, desc.Format, desc.Dimension, TextureViewKind.ColorAttachment);
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
        ValidateTextureView(desc.Texture, desc.Range, desc.Format, desc.Dimension, TextureViewKind.DepthStencil);
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
        ValidateSampler(desc);
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
        if (buffer.Mapping?.IsActive == true) throw new InvalidOperationException("The buffer already has an active mapping.");
        if (!Enum.IsDefined(type)) throw new ArgumentOutOfRangeException(nameof(type));
        BufferRange resolved = range.Resolve(buffer.Info.Size);
        ValidateBufferRange(buffer, resolved.Offset, resolved.Size);
        if (buffer.Device.Capabilities.CompileTarget == SlangShaderSharp.SlangCompileTarget.Spirv)
        {
            if (buffer.Info.MemoryType == MemoryType.DeviceLocal) throw new NotSupportedException("Device-local buffers cannot be mapped.");
            if (type != MapType.Read && buffer.Info.MemoryType == MemoryType.Readback)
                throw new ArgumentException("Readback buffers require a read mapping.", nameof(type));
        }
        return Backend.Map(buffer, type, range);
    }

    private void RequireSameDevice(Device expected, Device actual, string objectType)
    {
        if (!ReferenceEquals(expected, actual))
            RejectArgument("Ownership", $"{objectType} belongs to another Device.");
    }

}
