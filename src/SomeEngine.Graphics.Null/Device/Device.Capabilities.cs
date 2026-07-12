namespace SomeEngine.Graphics.Null;

public sealed partial class Device
{
    public FormatSupport GetFormatSupport(Format format)
    {
        if (!Enum.IsDefined(format)) throw new ArgumentOutOfRangeException(nameof(format));
        if (format == Format.Unknown) return FormatSupport.None;

        bool depth = format is Format.D24UNormS8UInt or Format.D32Float;
        FormatSupport support = FormatSupport.Copy | FormatSupport.Sampled;
        if (depth)
        {
            return support | FormatSupport.DepthStencil;
        }

        support |= FormatSupport.VertexBuffer | FormatSupport.RenderTarget | FormatSupport.Resolve;
        if (format is not (Format.R8G8B8A8UNormSrgb or Format.B8G8R8A8UNorm))
            support |= FormatSupport.Storage;
        if (format is Format.R16UInt or Format.R32UInt)
            support |= FormatSupport.IndexBuffer;
        if (format is Format.R8G8B8A8UNorm or Format.R8G8B8A8UNormSrgb or Format.B8G8R8A8UNorm)
            support |= FormatSupport.Present;
        return support;
    }

    public MemoryBudget GetMemoryBudget(MemoryType memoryType)
    {
        EnsureCoordinatorThread();
        if (!Enum.IsDefined(memoryType)) throw new ArgumentOutOfRangeException(nameof(memoryType));
        lock (_gate)
        {
            EnsureNotDisposed();
            HashSet<PhysicalAllocationId> allocations = [];
            ulong usage = checked(
                GetHeapUsage(memoryType, allocations) +
                GetBufferUsage(memoryType, allocations) +
                GetTextureUsage(memoryType, allocations));
            return MemoryBudget.FromUsage(GetMemoryBudgetLimit(memoryType), usage);
        }
    }

    private ulong GetHeapUsage(MemoryType memoryType, HashSet<PhysicalAllocationId> allocations)
    {
        ulong usage = 0;
        foreach ((_, GenerationRegistry<HeapRecord>.Slot slot) in _heaps.Occupied())
        {
            HeapRecord heap = slot.Value!;
            if (heap.Desc.MemoryType == memoryType && allocations.Add(heap.AllocationId))
                usage = checked(usage + heap.Desc.Size);
        }
        return usage;
    }

    private ulong GetBufferUsage(MemoryType memoryType, HashSet<PhysicalAllocationId> allocations)
    {
        ulong usage = 0;
        foreach ((_, GenerationRegistry<BufferRecord>.Slot slot) in _buffers.Occupied())
        {
            BufferRecord buffer = slot.Value!;
            if (buffer.MemoryType == memoryType && allocations.Add(buffer.Allocation.Identity))
                usage = checked(usage + buffer.Allocation.Size);
        }
        return usage;
    }

    private ulong GetTextureUsage(MemoryType memoryType, HashSet<PhysicalAllocationId> allocations)
    {
        ulong usage = 0;
        foreach ((_, GenerationRegistry<TextureRecord>.Slot slot) in _textures.Occupied())
        {
            TextureRecord texture = slot.Value!;
            if (texture.MemoryType == memoryType && allocations.Add(texture.Allocation.Identity))
                usage = checked(usage + texture.Allocation.Size);
        }
        return usage;
    }

    private ulong GetMemoryBudgetLimit(MemoryType memoryType) => memoryType switch
    {
        MemoryType.DeviceLocal => _options.DeviceLocalBudget,
        MemoryType.Upload => _options.UploadBudget,
        MemoryType.Readback => _options.ReadbackBudget,
        _ => throw new ArgumentOutOfRangeException(nameof(memoryType)),
    };

    public ResourceMemoryInfo GetResourceMemoryInfo(ResourceHandle resource)
    {
        EnsureCoordinatorThread();
        lock (_gate)
        {
            EnsureNotDisposed();
            return resource.Kind switch
            {
                ResourceKind.Buffer => BufferMemoryInfo(resource),
                ResourceKind.Texture => TextureMemoryInfo(resource),
                _ => throw new ArgumentOutOfRangeException(nameof(resource)),
            };
        }
    }

    public void SetResidencyPriority(ResourceHandle resource, ResidencyPriority priority)
    {
        EnsureCoordinatorThread();
        if (!Enum.IsDefined(priority)) throw new ArgumentOutOfRangeException(nameof(priority));
        lock (_gate)
        {
            EnsureNotDisposed();
            switch (resource.Kind)
            {
                case ResourceKind.Buffer:
                    RequireBuffer(new BufferHandle(resource.Domain, resource.Slot, resource.Generation)).Priority = priority;
                    break;
                case ResourceKind.Texture:
                    RequireTexture(new TextureHandle(resource.Domain, resource.Slot, resource.Generation)).Priority = priority;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(resource));
            }
        }
    }

    private ResourceMemoryInfo BufferMemoryInfo(ResourceHandle resource)
    {
        BufferRecord buffer = RequireBuffer(new BufferHandle(resource.Domain, resource.Slot, resource.Generation));
        return new ResourceMemoryInfo(
            resource,
            buffer.MemoryType,
            buffer.Allocation.Size,
            buffer.Allocation.Offset,
            buffer.Priority,
            buffer.Resident);
    }

    private ResourceMemoryInfo TextureMemoryInfo(ResourceHandle resource)
    {
        TextureRecord texture = RequireTexture(new TextureHandle(resource.Domain, resource.Slot, resource.Generation));
        return new ResourceMemoryInfo(
            resource,
            texture.MemoryType,
            texture.Allocation.Size,
            texture.Allocation.Offset,
            texture.Priority,
            texture.Resident);
    }
}
