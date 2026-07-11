namespace SomeEngine.Graphics.Null;

public sealed partial class Device
{
    public HeapHandle CreateHeap(in HeapDesc desc)
    {
        EnsureCoordinatorThread();
        if (desc.Size == 0) throw new ArgumentOutOfRangeException(nameof(desc));
        if (!Enum.IsDefined(desc.MemoryType) || !Enum.IsDefined(desc.ResourceClass)) throw new ArgumentOutOfRangeException(nameof(desc));
        if (desc.MemoryType != MemoryType.DeviceLocal && desc.ResourceClass != ResourceHeapClass.Buffer)
        {
            throw new ArgumentException("CPU-visible heaps can contain buffers only.", nameof(desc));
        }
        lock (_gate)
        {
            EnsureNotDisposed();
            (uint slot, uint generation) = _heaps.Allocate(new HeapRecord
            {
                Desc = desc,
                Storage = new byte[ToArrayLength(desc.Size, nameof(desc))],
                AllocationId = PhysicalAllocationId.Allocate(_domain),
            });
            _statistics = _statistics with { HeapCreates = _statistics.HeapCreates + 1 };
            return new HeapHandle(_domain, slot, generation);
        }
    }

    public BufferHandle CreateBuffer(in BufferDesc desc, MemoryType memoryType = MemoryType.DeviceLocal)
    {
        EnsureCoordinatorThread();
        desc.Validate();
        if (!Enum.IsDefined(memoryType)) throw new ArgumentOutOfRangeException(nameof(memoryType));
        ResourceRequirements requirements = GetBufferRequirements(desc, memoryType);
        lock (_gate)
        {
            EnsureNotDisposed();
            (uint slot, uint generation) = _buffers.Allocate(new BufferRecord
            {
                Desc = desc,
                MemoryType = memoryType,
                Allocation = new PhysicalAllocationInfo(PhysicalAllocationId.Allocate(_domain), 0, requirements.Size),
                Storage = new byte[ToArrayLength(desc.Size, nameof(desc))],
                BaseOffset = 0,
                State = InitialBufferState(memoryType),
            });
            _statistics = _statistics with { BufferCreates = _statistics.BufferCreates + 1 };
            return new BufferHandle(_domain, slot, generation);
        }
    }

    public TextureHandle CreateTexture(in TextureDesc desc)
    {
        EnsureCoordinatorThread();
        desc.Validate();
        ResourceRequirements requirements = GetTextureRequirements(desc);
        lock (_gate)
        {
            EnsureNotDisposed();
            TextureRecord record = CreateTextureRecord(
                desc,
                MemoryType.DeviceLocal,
                new PhysicalAllocationInfo(PhysicalAllocationId.Allocate(_domain), 0, requirements.Size),
                new byte[ToArrayLength(TextureLayout.GetByteSize(desc), nameof(desc))],
                0,
                default);
            (uint slot, uint generation) = _textures.Allocate(record);
            _statistics = _statistics with { TextureCreates = _statistics.TextureCreates + 1 };
            return new TextureHandle(_domain, slot, generation);
        }
    }

    public BufferHandle CreatePlacedBuffer(HeapHandle heap, ulong offset, in BufferDesc desc)
    {
        EnsureCoordinatorThread();
        desc.Validate();
        lock (_gate)
        {
            EnsureNotDisposed();
            HeapRecord heapRecord = RequireHeap(heap);
            ResourceRequirements requirements = GetBufferRequirements(desc, heapRecord.Desc.MemoryType);
            ValidatePlacedResource(heapRecord.Desc, offset, requirements);
            (uint slot, uint generation) = _buffers.Allocate(new BufferRecord
            {
                Desc = desc,
                MemoryType = heapRecord.Desc.MemoryType,
                Allocation = new PhysicalAllocationInfo(heapRecord.AllocationId, offset, requirements.Size),
                Storage = heapRecord.Storage,
                BaseOffset = checked((int)offset),
                Heap = heap,
                State = InitialBufferState(heapRecord.Desc.MemoryType),
            });
            _statistics = _statistics with { BufferCreates = _statistics.BufferCreates + 1 };
            return new BufferHandle(_domain, slot, generation);
        }
    }

    public TextureHandle CreatePlacedTexture(HeapHandle heap, ulong offset, in TextureDesc desc)
    {
        EnsureCoordinatorThread();
        desc.Validate();
        lock (_gate)
        {
            EnsureNotDisposed();
            HeapRecord heapRecord = RequireHeap(heap);
            if (heapRecord.Desc.MemoryType != MemoryType.DeviceLocal)
            {
                throw ValidationError("Textures require device-local heaps.");
            }
            ResourceRequirements requirements = GetTextureRequirements(desc);
            ValidatePlacedResource(heapRecord.Desc, offset, requirements);
            TextureRecord record = CreateTextureRecord(
                desc,
                heapRecord.Desc.MemoryType,
                new PhysicalAllocationInfo(heapRecord.AllocationId, offset, requirements.Size),
                heapRecord.Storage,
                checked((int)offset),
                heap);
            (uint slot, uint generation) = _textures.Allocate(record);
            _statistics = _statistics with { TextureCreates = _statistics.TextureCreates + 1 };
            return new TextureHandle(_domain, slot, generation);
        }
    }

    public void DestroyHeap(HeapHandle heap)
    {
        EnsureCoordinatorThread();
        lock (_gate)
        {
            EnsureNotDisposed();
            _ = RequireHeap(heap);
            foreach ((_, GenerationRegistry<BufferRecord>.Slot slot) in _buffers.Occupied())
            {
                if (slot.Alive && slot.Value!.Heap == heap)
                {
                    throw ValidationError("A heap cannot be destroyed while a placed buffer remains alive.");
                }
            }
            foreach ((_, GenerationRegistry<TextureRecord>.Slot slot) in _textures.Occupied())
            {
                if (slot.Alive && slot.Value!.Heap == heap)
                {
                    throw ValidationError("A heap cannot be destroyed while a placed texture remains alive.");
                }
            }
            _heaps.Destroy(heap.Domain, heap.Slot, heap.Generation);
        }
    }

    public void DestroyBuffer(BufferHandle buffer)
    {
        EnsureCoordinatorThread();
        lock (_gate)
        {
            EnsureNotDisposed();
            _buffers.Destroy(buffer.Domain, buffer.Slot, buffer.Generation);
        }
    }

    public void DestroyTexture(TextureHandle texture)
    {
        EnsureCoordinatorThread();
        lock (_gate)
        {
            EnsureNotDisposed();
            _textures.Destroy(texture.Domain, texture.Slot, texture.Generation);
        }
    }

    public BufferMetadata GetBufferMetadata(BufferHandle buffer)
    {
        EnsureCoordinatorThread();
        lock (_gate)
        {
            EnsureNotDisposed();
            BufferRecord record = RequireBuffer(buffer);
            return new BufferMetadata(record.Desc, record.MemoryType, record.Allocation);
        }
    }

    public TextureMetadata GetTextureMetadata(TextureHandle texture)
    {
        EnsureCoordinatorThread();
        lock (_gate)
        {
            EnsureNotDisposed();
            TextureRecord record = RequireTexture(texture);
            return new TextureMetadata(record.Desc, record.MemoryType, record.Allocation);
        }
    }

    public TextureViewHandle CreateTextureView(in TextureViewDesc desc)
    {
        EnsureCoordinatorThread();
        lock (_gate)
        {
            EnsureNotDisposed();
            TextureRecord texture = RequireTexture(desc.Texture);
            ValidatedTextureViewDescription validated = TextureViewValidation.Validate(
                texture.Desc,
                desc.Range,
                desc.Usage,
                desc.Format,
                desc.Dimension);
            TextureViewDesc normalized = desc with
            {
                Range = validated.Range,
                Format = validated.Format,
                Dimension = validated.Dimension,
            };
            (uint slot, uint generation) = _textureViews.Allocate(new TextureViewRecord(normalized));
            _textures.AddChild(desc.Texture.Domain, desc.Texture.Slot, desc.Texture.Generation);
            return new TextureViewHandle(_domain, slot, generation);
        }
    }

    public BufferViewHandle CreateBufferView(in BufferViewDesc desc)
    {
        EnsureCoordinatorThread();
        lock (_gate)
        {
            EnsureNotDisposed();
            BufferRecord buffer = RequireBuffer(desc.Buffer);
            ResolveBufferRange(buffer.Desc, desc.Range, out _, out _);
            ValidateBufferViewKind(buffer.Desc, desc.Kind);
            if (desc.Stride != 0 && desc.Stride > buffer.Desc.Size) throw new ArgumentOutOfRangeException(nameof(desc));
            (uint slot, uint generation) = _bufferViews.Allocate(new BufferViewRecord(desc));
            _buffers.AddChild(desc.Buffer.Domain, desc.Buffer.Slot, desc.Buffer.Generation);
            return new BufferViewHandle(_domain, slot, generation);
        }
    }

    public SamplerHandle CreateSampler(in SamplerDesc desc)
    {
        EnsureCoordinatorThread();
        lock (_gate)
        {
            EnsureNotDisposed();
            (uint slot, uint generation) = _samplers.Allocate(new SamplerRecord(desc));
            return new SamplerHandle(_domain, slot, generation);
        }
    }

    public void DestroyTextureView(TextureViewHandle view)
    {
        EnsureCoordinatorThread();
        lock (_gate)
        {
            EnsureNotDisposed();
            TextureViewRecord record = RequireTextureView(view);
            _textureViews.Destroy(view.Domain, view.Slot, view.Generation);
            TextureHandle texture = record.Desc.Texture;
            _textures.ReleaseChild(texture.Domain, texture.Slot, texture.Generation);
        }
    }

    public void DestroyBufferView(BufferViewHandle view)
    {
        EnsureCoordinatorThread();
        lock (_gate)
        {
            EnsureNotDisposed();
            BufferViewRecord record = RequireBufferView(view);
            _bufferViews.Destroy(view.Domain, view.Slot, view.Generation);
            BufferHandle buffer = record.Desc.Buffer;
            _buffers.ReleaseChild(buffer.Domain, buffer.Slot, buffer.Generation);
        }
    }

    public void DestroySampler(SamplerHandle sampler)
    {
        EnsureCoordinatorThread();
        lock (_gate) { EnsureNotDisposed(); _samplers.Destroy(sampler.Domain, sampler.Slot, sampler.Generation); }
    }

    public void WriteBuffer(BufferHandle buffer, ulong offset, ReadOnlySpan<byte> data)
    {
        EnsureCoordinatorThread();
        lock (_gate)
        {
            EnsureNotDisposed();
            BufferRecord record = RequireBuffer(buffer);
            if (record.MemoryType != MemoryType.Upload) throw ValidationError("WriteBuffer requires an upload buffer.");
            if (!_buffers.HasCompletedLastUse(buffer.Domain, buffer.Slot, buffer.Generation, _completed))
                throw ValidationError("An upload buffer cannot be rewritten before its exact queue use has completed.");
            ValidateByteRange(record.Desc.Size, offset, checked((ulong)data.Length));
            data.CopyTo(record.Bytes.Slice(checked((int)offset), data.Length));
        }
    }

    public void ReadBuffer(BufferHandle buffer, ulong offset, Span<byte> destination)
    {
        EnsureCoordinatorThread();
        lock (_gate)
        {
            EnsureNotDisposed();
            BufferRecord record = RequireBuffer(buffer);
            if (record.MemoryType != MemoryType.Readback) throw ValidationError("ReadBuffer requires a readback buffer.");
            if (!_buffers.HasCompletedLastUse(buffer.Domain, buffer.Slot, buffer.Generation, _completed))
                throw ValidationError("A readback buffer cannot be read before its exact queue use has completed.");
            ValidateByteRange(record.Desc.Size, offset, checked((ulong)destination.Length));
            record.Bytes.Slice(checked((int)offset), destination.Length).CopyTo(destination);
        }
    }

    internal void ValidateBarrierForRecording(in ResourceBarrier barrier)
    {
        lock (_gate)
        {
            EnsureNotDisposed();
            if (!Enum.IsDefined(barrier.Kind) || !Enum.IsDefined(barrier.Before) || !Enum.IsDefined(barrier.After))
                throw new ArgumentOutOfRangeException(nameof(barrier));
            switch (barrier.Kind)
            {
                case BarrierKind.Transition:
                case BarrierKind.UnorderedAccess:
                    object resource = RequireResource(barrier.Resource);
                    if (barrier.Kind == BarrierKind.Transition && resource is BufferRecord buffer &&
                        BufferStateValidation.HasFixedState(buffer.MemoryType) &&
                        (!BufferStateValidation.IsFixedState(buffer.MemoryType, barrier.Before) ||
                         !BufferStateValidation.IsFixedState(buffer.MemoryType, barrier.After)))
                    {
                        throw ValidationError(
                            $"{buffer.MemoryType} buffers have fixed logical state {BufferStateValidation.DescribeFixedState(buffer.MemoryType)}.");
                    }
                    if (resource is TextureRecord texture)
                    {
                        TextureLayout.NormalizeRange(texture.Desc, barrier.TextureRange, out _, out _, out _, out _);
                    }
                    break;
                case BarrierKind.Aliasing:
                    RequireResource(barrier.Resource);
                    if (barrier.AliasingBefore.IsValid) RequireResource(barrier.AliasingBefore);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(barrier));
            }
        }
    }

    internal void ValidateBufferCopyForRecording(BufferHandle source, ulong sourceOffset, BufferHandle destination, ulong destinationOffset, ulong size)
    {
        lock (_gate)
        {
            BufferRecord sourceRecord = RequireBuffer(source);
            BufferRecord destinationRecord = RequireBuffer(destination);
            RequireUsage(sourceRecord.Desc.Usage, BufferUsage.CopySource, "copy source");
            RequireUsage(destinationRecord.Desc.Usage, BufferUsage.CopyDestination, "copy destination");
            ValidateByteRange(sourceRecord.Desc.Size, sourceOffset, size);
            ValidateByteRange(destinationRecord.Desc.Size, destinationOffset, size);
        }
    }

    internal void ValidateBufferToTextureForRecording(in BufferTextureCopy copy)
    {
        lock (_gate)
        {
            BufferRecord source = RequireBuffer(copy.Source);
            TextureRecord destination = RequireTexture(copy.Destination);
            RequireUsage(source.Desc.Usage, BufferUsage.CopySource, "buffer-to-texture source");
            RequireUsage(destination.Desc.Usage, TextureUsage.CopyDestination, "buffer-to-texture destination");
            ValidateTextureCopyLayout(destination.Desc, copy.DestinationRegion, copy.SourceLayout, source.Desc.Size);
        }
    }

    internal void ValidateTextureToBufferForRecording(in TextureBufferCopy copy)
    {
        lock (_gate)
        {
            TextureRecord source = RequireTexture(copy.Source);
            BufferRecord destination = RequireBuffer(copy.Destination);
            RequireUsage(source.Desc.Usage, TextureUsage.CopySource, "texture-to-buffer source");
            RequireUsage(destination.Desc.Usage, BufferUsage.CopyDestination, "texture-to-buffer destination");
            ValidateTextureCopyLayout(source.Desc, copy.SourceRegion, copy.DestinationLayout, destination.Desc.Size);
        }
    }

    internal void ValidateTextureResolveForRecording(in TextureResolveRegion resolve)
    {
        lock (_gate)
        {
            TextureRecord source = RequireTexture(resolve.Source);
            TextureRecord destination = RequireTexture(resolve.Destination);
            if (resolve.Source == resolve.Destination)
                throw new ArgumentException("Resolve source and destination must be different textures.", nameof(resolve));
            TextureResolveValidation.Validate(resolve, source.Desc, destination.Desc);
        }
    }

    internal void ValidateVertexBufferForRecording(BufferHandle buffer, ulong offset, uint stride)
    {
        lock (_gate)
        {
            BufferRecord record = RequireBuffer(buffer);
            RequireUsage(record.Desc.Usage, BufferUsage.Vertex, "vertex buffer");
            if (stride == 0 || offset >= record.Desc.Size) throw new ArgumentOutOfRangeException(nameof(offset));
        }
    }

    internal void ValidateIndexBufferForRecording(BufferHandle buffer, ulong offset, IndexFormat format)
    {
        lock (_gate)
        {
            BufferRecord record = RequireBuffer(buffer);
            RequireUsage(record.Desc.Usage, BufferUsage.Index, "index buffer");
            int size = format == IndexFormat.UInt16 ? 2 : 4;
            if (offset >= record.Desc.Size || offset % (ulong)size != 0) throw new ArgumentOutOfRangeException(nameof(offset));
        }
    }

    private HeapRecord RequireHeap(HeapHandle handle) => _heaps.RequireAlive(handle.Domain, handle.Slot, handle.Generation).Value!;
    private BufferRecord RequireBuffer(BufferHandle handle) => _buffers.RequireAlive(handle.Domain, handle.Slot, handle.Generation).Value!;
    private TextureRecord RequireTexture(TextureHandle handle) => _textures.RequireAlive(handle.Domain, handle.Slot, handle.Generation).Value!;
    private TextureViewRecord RequireTextureView(TextureViewHandle handle) => _textureViews.RequireAlive(handle.Domain, handle.Slot, handle.Generation).Value!;
    private BufferViewRecord RequireBufferView(BufferViewHandle handle) => _bufferViews.RequireAlive(handle.Domain, handle.Slot, handle.Generation).Value!;
    private SamplerRecord RequireSampler(SamplerHandle handle) => _samplers.RequireAlive(handle.Domain, handle.Slot, handle.Generation).Value!;

    private object RequireResource(ResourceHandle handle) => handle.Kind switch
    {
        ResourceKind.Buffer => RequireBuffer(new BufferHandle(handle.Domain, handle.Slot, handle.Generation)),
        ResourceKind.Texture => RequireTexture(new TextureHandle(handle.Domain, handle.Slot, handle.Generation)),
        _ => throw new ArgumentOutOfRangeException(nameof(handle)),
    };

    private static TextureRecord CreateTextureRecord(
        in TextureDesc desc,
        MemoryType memoryType,
        in PhysicalAllocationInfo allocation,
        byte[] storage,
        int baseOffset,
        HeapHandle heap)
    {
        int subresourceCount = TextureLayout.GetStateCount(desc);
        ResourceState[] states = new ResourceState[subresourceCount];
        Array.Fill(states, ResourceState.Common);
        return new TextureRecord
        {
            Desc = desc,
            MemoryType = memoryType,
            Allocation = allocation,
            Storage = storage,
            BaseOffset = baseOffset,
            Heap = heap,
            States = states,
        };
    }

    private static ResourceState InitialBufferState(MemoryType memoryType) => memoryType switch
    {
        MemoryType.Upload => ResourceState.CopySource,
        MemoryType.Readback => ResourceState.CopyDestination,
        _ => ResourceState.Common,
    };

    private static void ValidatePlacedResource(in HeapDesc heap, ulong offset, in ResourceRequirements requirements)
    {
        if (offset % requirements.Alignment != 0 || offset > heap.Size || requirements.Size > heap.Size - offset)
            throw new ArgumentOutOfRangeException(nameof(offset));
        if (heap.ResourceClass != ResourceHeapClass.All && heap.ResourceClass != requirements.ResourceClass)
            throw new InvalidOperationException($"Heap class {heap.ResourceClass} cannot hold {requirements.ResourceClass} resources.");
        if (heap.MemoryType != requirements.MemoryType)
            throw new InvalidOperationException("Placed resource requirements do not match the heap memory type.");
    }

    private static void ValidateBufferViewKind(in BufferDesc desc, BindingKind kind)
    {
        switch (kind)
        {
            case BindingKind.ConstantBuffer:
                RequireUsage(desc.Usage, BufferUsage.Constant, "constant buffer view");
                break;
            case BindingKind.ReadOnlyBuffer:
                RequireUsage(desc.Usage, BufferUsage.ShaderRead, "read-only shader buffer view");
                break;
            case BindingKind.StorageBuffer:
                RequireUsage(desc.Usage, BufferUsage.ShaderWrite, "writable shader buffer view");
                break;
            default:
                throw new ArgumentException($"Binding kind {kind} is not valid for a buffer view.");
        }
    }

    private static void ResolveBufferRange(in BufferDesc desc, in BufferRange range, out ulong offset, out ulong size)
    {
        offset = range.Offset;
        size = range.Size == ulong.MaxValue ? desc.Size - offset : range.Size;
        ValidateByteRange(desc.Size, offset, size);
    }

    private static void ValidateByteRange(ulong total, ulong offset, ulong size)
    {
        if (size == 0 || offset > total || size > total - offset) throw new ArgumentOutOfRangeException(nameof(offset));
    }

    private static void RequireUsage(BufferUsage actual, BufferUsage required, string operation)
    {
        if ((actual & required) != required) throw new InvalidOperationException($"{operation} requires buffer usage {required}; actual usage is {actual}.");
    }

    private static void RequireUsage(TextureUsage actual, TextureUsage required, string operation)
    {
        if ((actual & required) != required) throw new InvalidOperationException($"{operation} requires texture usage {required}; actual usage is {actual}.");
    }

    private static void ValidateBufferMemoryUsage(in BufferDesc desc, MemoryType memoryType)
    {
        const BufferUsage uploadAllowed =
            BufferUsage.CopySource |
            BufferUsage.Constant |
            BufferUsage.ShaderRead |
            BufferUsage.Vertex |
            BufferUsage.Index |
            BufferUsage.Indirect;

        if (memoryType == MemoryType.Upload && (desc.Usage & ~uploadAllowed) != 0)
        {
            throw new ArgumentException(
                $"Upload buffers are fixed in a generic-read state and cannot declare usage {desc.Usage & ~uploadAllowed}.",
                nameof(desc));
        }
        if (memoryType == MemoryType.Readback && desc.Usage != BufferUsage.CopyDestination)
        {
            throw new ArgumentException("Readback buffers can declare CopyDestination usage only.", nameof(desc));
        }
    }

    private static int ToArrayLength(ulong size, string parameter)
    {
        if (size > int.MaxValue) throw new NotSupportedException($"The Null backend cannot allocate more than {int.MaxValue} bytes for {parameter}.");
        return checked((int)size);
    }
}
