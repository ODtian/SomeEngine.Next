using System.Numerics;
using Vortice.Direct3D12;
using D3D12HeapFlags = Vortice.Direct3D12.HeapFlags;
using D3D12Range = Vortice.Direct3D12.Range;

namespace SomeEngine.Graphics.Direct3D12;

public sealed partial class Device
{
    public ResourceRequirements GetBufferRequirements(
        in BufferDesc desc,
        MemoryType memoryType = MemoryType.DeviceLocal)
    {
        ThrowIfUnavailable();
        desc.Validate();
        if (!Enum.IsDefined(memoryType)) throw new ArgumentOutOfRangeException(nameof(memoryType));
        ValidateCpuMemoryUsage(desc, memoryType);
        BufferDesc key = desc with { Name = null };
        lock (_requirementGate)
        {
            if (_bufferRequirements.TryGetValue((key, memoryType), out ResourceRequirements cached)) return cached;
            ResourceRequirements queried = QueryBufferRequirements(key, memoryType);
            _bufferRequirements.Add((key, memoryType), queried);
            return queried;
        }
    }

    private ResourceRequirements QueryBufferRequirements(in BufferDesc desc, MemoryType memoryType)
    {
        Interlocked.Increment(ref _nativeBufferRequirementQueries);
        ResourceDescription nativeDesc = CreateBufferDescription(desc);
        ResourceAllocationInfo info = _native.Device.GetResourceAllocationInfo(0, nativeDesc);
        return new ResourceRequirements(info.SizeInBytes, info.Alignment, memoryType, ResourceHeapClass.Buffer, CompatibilityClass(ResourceHeapClass.Buffer, nativeDesc.Flags));
    }

    public ResourceRequirements GetTextureRequirements(in TextureDesc desc)
    {
        ThrowIfUnavailable();
        desc.Validate();
        TextureDesc key = desc with { Name = null };
        lock (_requirementGate)
        {
            if (_textureRequirements.TryGetValue(key, out ResourceRequirements cached)) return cached;
            ResourceRequirements queried = QueryTextureRequirements(key);
            _textureRequirements.Add(key, queried);
            return queried;
        }
    }

    private ResourceRequirements QueryTextureRequirements(in TextureDesc desc)
    {
        Interlocked.Increment(ref _nativeTextureRequirementQueries);
        ResourceDescription nativeDesc = CreateTextureDescription(desc);
        ResourceAllocationInfo info = _native.Device.GetResourceAllocationInfo(0, nativeDesc);
        ResourceHeapClass resourceClass = TextureHeapClass(desc);
        return new ResourceRequirements(
            info.SizeInBytes,
            info.Alignment,
            MemoryType.DeviceLocal,
            resourceClass,
            TextureCompatibilityClass(desc, resourceClass, nativeDesc.Flags));
    }

    public unsafe TextureCopyFootprint GetTextureCopyFootprint(
        in TextureDesc desc,
        in TextureCopyRegion region,
        ulong requestedBufferOffset = 0)
    {
        ThrowIfUnavailable();
        desc.Validate();
        ValidateCopyRegion(desc, region, out int mipWidth, out _, out _);
        QueryNativeCopyFootprint(desc, region, out PlacedSubresourceFootPrint nativeLayout, out _, out ulong nativeRowSize, out _);
        if (nativeRowSize == 0 || nativeRowSize % checked((uint)mipWidth) != 0)
            throw new InvalidOperationException("D3D12 reported a non-integral texture-plane texel size.");

        ulong bytesPerTexel = nativeRowSize / checked((uint)mipWidth);
        ulong rowSize = checked((ulong)region.Width * bytesPerTexel);
        uint rowPitch = checked((uint)AlignUp(rowSize, 256));
        ulong offset = AlignUp(requestedBufferOffset, 512);
        uint rowsPerImage = checked((uint)region.Height);
        ulong slicePitch = checked((ulong)rowPitch * rowsPerImage);
        if (region.Depth > 1 && (slicePitch & 511) != 0)
        {
            // Row pitch is always 256-byte aligned. One padding row therefore makes an odd
            // 256-byte-multiple slice pitch satisfy D3D12's 512-byte placement alignment.
            rowsPerImage = checked(rowsPerImage + 1);
            slicePitch = checked((ulong)rowPitch * rowsPerImage);
        }
        ulong footprintSize = checked(
            (ulong)(region.Depth - 1) * slicePitch +
            (ulong)(region.Height - 1) * rowPitch +
            rowSize);
        return new TextureCopyFootprint(
            new TextureBufferLayout(offset, rowPitch, rowsPerImage),
            checked((uint)rowSize),
            footprintSize);
    }

    internal unsafe Vortice.DXGI.Format GetTextureCopyPlaneFormat(
        in TextureDesc desc,
        in TextureCopyRegion region)
    {
        QueryNativeCopyFootprint(desc, region, out PlacedSubresourceFootPrint nativeLayout, out _, out _, out _);
        return nativeLayout.Footprint.Format;
    }

    private unsafe void QueryNativeCopyFootprint(
        in TextureDesc desc,
        in TextureCopyRegion region,
        out PlacedSubresourceFootPrint layout,
        out uint rows,
        out ulong rowSize,
        out ulong totalBytes)
    {
        ResourceDescription nativeDesc = CreateTextureDescription(desc);
        uint subresource = NativeSubresource(desc, region.MipLevel, region.ArrayLayer, region.Aspect);
        fixed (PlacedSubresourceFootPrint* layoutPointer = &layout)
        fixed (uint* rowsPointer = &rows)
        fixed (ulong* rowSizePointer = &rowSize)
        {
            _native.Device.GetCopyableFootprints(
                nativeDesc,
                subresource,
                1,
                0,
                layoutPointer,
                rowsPointer,
                rowSizePointer,
                out totalBytes);
        }
    }

}

public sealed partial class Device
{

    public HeapHandle CreateHeap(in HeapDesc desc)
    {
        EnsureCoordinator();
        ThrowIfUnavailable();
        if (desc.Size == 0) throw new ArgumentOutOfRangeException(nameof(desc));
        if (!Enum.IsDefined(desc.MemoryType) || !Enum.IsDefined(desc.ResourceClass)) throw new ArgumentOutOfRangeException(nameof(desc));
        if (desc.MemoryType != MemoryType.DeviceLocal && desc.ResourceClass != ResourceHeapClass.Buffer)
        {
            throw new ArgumentException("CPU-visible heaps can contain buffers only.", nameof(desc));
        }
        if (desc.ResourceClass == ResourceHeapClass.All && Compilation.ResourceHeapTier == ResourceHeapTier.Tier1)
        {
            throw new ArgumentException("Tier-1 devices require a concrete heap resource class.", nameof(desc));
        }

        NativeHeap native = CreateNativeHeap(desc);
        HandleKey key = _heaps.Add(native);
        return new HeapHandle(_domain, key.Slot, key.Generation);
    }

    private NativeHeap CreateNativeHeap(in HeapDesc desc)
    {
        HeapDescription description = new(desc.Size, HeapProperties(desc.MemoryType), 0, HeapFlags(desc.ResourceClass));
        ID3D12Heap heap = _native.Device.CreateHeap<ID3D12Heap>(in description);
        NativeHeap native = new(heap, desc, PhysicalAllocationId.Allocate(_domain));
        ApplyObjectName(native, heap, desc.Name);
        return native;
    }

    public BufferHandle CreateBuffer(in BufferDesc desc, MemoryType memoryType = MemoryType.DeviceLocal)
    {
        EnsureCoordinator();
        ThrowIfUnavailable();
        desc.Validate();
        ValidateCpuMemoryUsage(desc, memoryType);
        NativeBuffer native = CreateCommittedBuffer(desc, memoryType);
        HandleKey key = _buffers.Add(native);
        return new BufferHandle(_domain, key.Slot, key.Generation);
    }

    private NativeBuffer CreateCommittedBuffer(in BufferDesc desc, MemoryType memoryType)
    {
        ResourceDescription description = CreateBufferDescription(desc);
        ResourceRequirements requirements = GetBufferRequirements(desc, memoryType);
        ResourceStates state = InitialState(memoryType);
        ID3D12Resource resource = _native.Device.CreateCommittedResource(HeapType(memoryType), description, state);
        NativeBuffer native = new(
            resource,
            desc,
            memoryType,
            state,
            null,
            new PhysicalAllocationInfo(PhysicalAllocationId.Allocate(_domain), 0, requirements.Size));
        ApplyObjectName(native, resource, desc.Name);
        return native;
    }

    public TextureHandle CreateTexture(in TextureDesc desc)
    {
        EnsureCoordinator();
        ThrowIfUnavailable();
        desc.Validate();
        NativeTexture native = CreateCommittedTexture(desc);
        HandleKey key = _textures.Add(native);
        return new TextureHandle(_domain, key.Slot, key.Generation);
    }

    private NativeTexture CreateCommittedTexture(in TextureDesc desc)
    {
        ResourceRequirements requirements = GetTextureRequirements(desc);
        ID3D12Resource resource = CreateCommittedTextureResource(desc);
        NativeTexture native = WrapCommittedTexture(resource, desc, requirements.Size);
        ApplyObjectName(native, resource, desc.Name);
        return native;
    }

    private ID3D12Resource CreateCommittedTextureResource(in TextureDesc desc) =>
        _native.Device.CreateCommittedResource(
            Vortice.Direct3D12.HeapType.Default,
            CreateTextureDescription(desc),
            ResourceStates.Common);

    private NativeTexture WrapCommittedTexture(ID3D12Resource resource, in TextureDesc desc, ulong size) =>
        new(
            resource,
            desc,
            MemoryType.DeviceLocal,
            ResourceStates.Common,
            null,
            new PhysicalAllocationInfo(PhysicalAllocationId.Allocate(_domain), 0, size));

    public BufferHandle CreatePlacedBuffer(HeapHandle heap, ulong offset, in BufferDesc desc)
    {
        EnsureCoordinator();
        ThrowIfUnavailable();
        desc.Validate();
        NativeHeap nativeHeap = _heaps.Get(heap.Domain, heap.Slot, heap.Generation, "heap");
        ResourceRequirements requirements = GetBufferRequirements(desc, nativeHeap.Desc.MemoryType);
        ValidatePlacement(nativeHeap, offset, requirements);
        NativeBuffer native = CreateNativePlacedBuffer(nativeHeap, offset, desc, requirements);
        HandleKey key = _buffers.Add(native);
        return new BufferHandle(_domain, key.Slot, key.Generation);
    }

    private NativeBuffer CreateNativePlacedBuffer(
        NativeHeap heap,
        ulong offset,
        in BufferDesc desc,
        in ResourceRequirements requirements)
    {
        ResourceStates state = InitialState(heap.Desc.MemoryType);
        ID3D12Resource resource = _native.Device.CreatePlacedResource<ID3D12Resource>(
            heap.Heap,
            offset,
            CreateBufferDescription(desc),
            state,
            null);
        heap.AddChild(ResourceHeapClass.Buffer);
        NativeBuffer native = new(
            resource,
            desc,
            heap.Desc.MemoryType,
            state,
            heap,
            new PhysicalAllocationInfo(heap.AllocationId, offset, requirements.Size));
        ApplyObjectName(native, resource, desc.Name);
        return native;
    }

    public TextureHandle CreatePlacedTexture(HeapHandle heap, ulong offset, in TextureDesc desc)
    {
        EnsureCoordinator();
        ThrowIfUnavailable();
        desc.Validate();
        NativeHeap nativeHeap = _heaps.Get(heap.Domain, heap.Slot, heap.Generation, "heap");
        if (nativeHeap.Desc.MemoryType != MemoryType.DeviceLocal)
            throw new ArgumentException("Placed textures require device-local heaps.", nameof(heap));
        ResourceRequirements requirements = GetTextureRequirements(desc);
        ValidatePlacement(nativeHeap, offset, requirements);
        NativeTexture native = CreateNativePlacedTexture(nativeHeap, offset, desc, requirements);
        HandleKey key = _textures.Add(native);
        return new TextureHandle(_domain, key.Slot, key.Generation);
    }

    private NativeTexture CreateNativePlacedTexture(
        NativeHeap heap,
        ulong offset,
        in TextureDesc desc,
        in ResourceRequirements requirements)
    {
        ID3D12Resource resource = _native.Device.CreatePlacedResource<ID3D12Resource>(
            heap.Heap,
            offset,
            CreateTextureDescription(desc),
            ResourceStates.Common,
            null);
        heap.AddChild(requirements.ResourceClass);
        NativeTexture native = new(
            resource,
            desc,
            heap.Desc.MemoryType,
            ResourceStates.Common,
            heap,
            new PhysicalAllocationInfo(heap.AllocationId, offset, requirements.Size));
        ApplyObjectName(native, resource, desc.Name);
        return native;
    }

    public void DestroyHeap(HeapHandle heap)
    {
        EnsureCoordinator();
        ThrowIfUnavailable();
        NativeHeap native = _heaps.Get(heap.Domain, heap.Slot, heap.Generation, "heap");
        if (native.ChildCount != 0) throw new InvalidOperationException("A heap cannot be destroyed while placed resources remain alive.");
        RetirementPoint point = BeginRetirement(native);
        _ = _heaps.Remove(heap.Domain, heap.Slot, heap.Generation, "heap");
        ScheduleRetirement(native, point);
    }

    public void DestroyBuffer(BufferHandle buffer)
    {
        EnsureCoordinator();
        ThrowIfUnavailable();
        NativeBuffer native = _buffers.Get(buffer.Domain, buffer.Slot, buffer.Generation, "buffer");
        if (native.IsMapped) throw new InvalidOperationException("A mapped buffer cannot be destroyed.");
        if (native.ViewCount != 0) throw new InvalidOperationException("A buffer cannot be destroyed while buffer views remain alive.");
        RetirementPoint point = BeginRetirement(native);
        _ = _buffers.Remove(buffer.Domain, buffer.Slot, buffer.Generation, "buffer");
        native.Parent?.RemoveChild();
        ScheduleRetirement(native, point);
    }

    public void DestroyTexture(TextureHandle texture)
    {
        EnsureCoordinator();
        ThrowIfUnavailable();
        NativeTexture native = _textures.Get(texture.Domain, texture.Slot, texture.Generation, "texture");
        if (native.IsSwapchainImage)
            throw new InvalidOperationException("Swapchain images are owned by their swapchain and cannot be destroyed directly.");
        if (native.ViewCount != 0) throw new InvalidOperationException("A texture cannot be destroyed while texture views remain alive.");
        RetirementPoint point = BeginRetirement(native);
        _ = _textures.Remove(texture.Domain, texture.Slot, texture.Generation, "texture");
        native.Parent?.RemoveChild();
        ScheduleRetirement(native, point);
    }

}

public sealed partial class Device
{

    public BufferMetadata GetBufferMetadata(BufferHandle buffer)
    {
        EnsureCoordinator();
        ThrowIfUnavailable();
        NativeBuffer native = GetBuffer(buffer);
        return new BufferMetadata(native.Desc, native.MemoryType, native.Allocation);
    }

    public TextureMetadata GetTextureMetadata(TextureHandle texture)
    {
        EnsureCoordinator();
        ThrowIfUnavailable();
        NativeTexture native = GetTexture(texture);
        return new TextureMetadata(native.Desc, native.MemoryType, native.Allocation);
    }

    public void WriteBuffer(BufferHandle buffer, ulong offset, ReadOnlySpan<byte> data)
    {
        EnsureCoordinator();
        ThrowIfUnavailable();
        NativeBuffer native = GetBuffer(buffer);
        if (native.MemoryType != MemoryType.Upload) throw new InvalidOperationException("Only upload buffers are CPU-writable.");
        if (!native.HasCompletedLastUse(_native))
            throw new InvalidOperationException("An upload buffer cannot be rewritten before its exact queue use has completed.");
        ValidateRange(native.Desc.Size, offset, checked((ulong)data.Length));
        if (!native.TryBeginMapping()) throw new InvalidOperationException("A buffer permits only one active mapping lease.");
        try
        {
            int mappedLength = checked((int)(offset + (ulong)data.Length));
            Span<byte> mapped = native.Resource.Map<byte>(0, mappedLength);
            data.CopyTo(mapped.Slice(checked((int)offset), data.Length));
            native.Resource.Unmap(0, new D3D12Range(new UIntPtr(offset), new UIntPtr(offset + (ulong)data.Length)));
        }
        finally
        {
            native.EndMapping();
        }
    }

    public void ReadBuffer(BufferHandle buffer, ulong offset, Span<byte> destination)
    {
        EnsureCoordinator();
        ThrowIfUnavailable();
        NativeBuffer native = GetBuffer(buffer);
        if (native.MemoryType != MemoryType.Readback) throw new InvalidOperationException("Only readback buffers are CPU-readable.");
        if (!native.HasCompletedLastUse(_native))
            throw new InvalidOperationException("A readback buffer cannot be read before its exact queue use has completed.");
        ValidateRange(native.Desc.Size, offset, checked((ulong)destination.Length));
        if (!native.TryBeginMapping()) throw new InvalidOperationException("A buffer permits only one active mapping lease.");
        try
        {
            int mappedLength = checked((int)(offset + (ulong)destination.Length));
            Span<byte> mapped = native.Resource.Map<byte>(0, mappedLength);
            mapped.Slice(checked((int)offset), destination.Length).CopyTo(destination);
            native.Resource.Unmap(0, new D3D12Range(UIntPtr.Zero, UIntPtr.Zero));
        }
        finally
        {
            native.EndMapping();
        }
    }

    private static ResourceDescription CreateBufferDescription(in BufferDesc desc) =>
        ResourceDescription.Buffer(desc.Size, (desc.Usage & BufferUsage.ShaderWrite) != 0 ? ResourceFlags.AllowUnorderedAccess : ResourceFlags.None);

    private static ResourceDescription CreateTextureDescription(in TextureDesc desc)
    {
        ResourceFlags flags = ResourceFlags.None;
        if ((desc.Usage & TextureUsage.ColorAttachment) != 0) flags |= ResourceFlags.AllowRenderTarget;
        if ((desc.Usage & TextureUsage.DepthStencilAttachment) != 0) flags |= ResourceFlags.AllowDepthStencil;
        if ((desc.Usage & TextureUsage.Storage) != 0) flags |= ResourceFlags.AllowUnorderedAccess;
        ResourceDimension dimension = desc.Dimension switch
        {
            TextureDimension.Texture1D => ResourceDimension.Texture1D,
            TextureDimension.Texture2D => ResourceDimension.Texture2D,
            TextureDimension.Texture3D => ResourceDimension.Texture3D,
            _ => throw new ArgumentOutOfRangeException(nameof(desc)),
        };
        ushort depthOrArraySize = desc.Dimension == TextureDimension.Texture3D
            ? checked((ushort)desc.Depth)
            : checked((ushort)desc.ArrayLayers);
        return new ResourceDescription(
            dimension,
            0,
            checked((ulong)desc.Width),
            checked((uint)desc.Height),
            depthOrArraySize,
            checked((ushort)desc.MipLevels),
            Mappings.ResourceFormat(desc),
            checked((uint)desc.SampleCount),
            0,
            TextureLayout.Unknown,
            flags);
    }

    private static HeapProperties HeapProperties(MemoryType memoryType) => memoryType switch
    {
        MemoryType.Upload => Vortice.Direct3D12.HeapProperties.UploadHeapProperties,
        MemoryType.Readback => Vortice.Direct3D12.HeapProperties.ReadbackHeapProperties,
        _ => Vortice.Direct3D12.HeapProperties.DefaultHeapProperties,
    };

    private static HeapType HeapType(MemoryType memoryType) => memoryType switch
    {
        MemoryType.Upload => Vortice.Direct3D12.HeapType.Upload,
        MemoryType.Readback => Vortice.Direct3D12.HeapType.Readback,
        _ => Vortice.Direct3D12.HeapType.Default,
    };

    private D3D12HeapFlags HeapFlags(ResourceHeapClass resourceClass)
    {
        if (Compilation.ResourceHeapTier == ResourceHeapTier.Tier2 || resourceClass == ResourceHeapClass.All) return D3D12HeapFlags.None;
        return resourceClass switch
        {
            ResourceHeapClass.Buffer => D3D12HeapFlags.DenyNonRenderTargetDepthStencilTextures | D3D12HeapFlags.DenyRenderTargetDepthStencilTextures,
            ResourceHeapClass.Texture => D3D12HeapFlags.DenyBuffers | D3D12HeapFlags.DenyRenderTargetDepthStencilTextures,
            ResourceHeapClass.RenderTargetOrDepth => D3D12HeapFlags.DenyBuffers | D3D12HeapFlags.DenyNonRenderTargetDepthStencilTextures,
            _ => throw new ArgumentOutOfRangeException(nameof(resourceClass)),
        };
    }

    private static ResourceStates InitialState(MemoryType memoryType) => memoryType switch
    {
        MemoryType.Upload => ResourceStates.GenericRead,
        MemoryType.Readback => ResourceStates.CopyDest,
        _ => ResourceStates.Common,
    };

    private static ResourceHeapClass TextureHeapClass(in TextureDesc desc) =>
        (desc.Usage & (TextureUsage.ColorAttachment | TextureUsage.DepthStencilAttachment)) != 0
            ? ResourceHeapClass.RenderTargetOrDepth
            : ResourceHeapClass.Texture;

    private ulong CompatibilityClass(ResourceHeapClass resourceClass, ResourceFlags flags) =>
        ((ulong)Compilation.ResourceHeapTier << 56) | ((ulong)resourceClass << 48) | (uint)flags;

    private ulong TextureCompatibilityClass(
        in TextureDesc desc,
        ResourceHeapClass resourceClass,
        ResourceFlags flags) =>
        desc.CompatibilitySignature() ^
        ((ulong)Compilation.ResourceHeapTier << 61) ^
        ((ulong)resourceClass << 58) ^
        ((ulong)(uint)flags << 48);

    private static void ValidateCpuMemoryUsage(in BufferDesc desc, MemoryType memoryType)
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
                $"Upload buffers are fixed in D3D12_RESOURCE_STATE_GENERIC_READ and cannot declare usage {desc.Usage & ~uploadAllowed}.",
                nameof(desc));
        }
        if (memoryType == MemoryType.Readback && desc.Usage != BufferUsage.CopyDestination)
        {
            throw new ArgumentException(
                "Readback buffers are fixed in D3D12_RESOURCE_STATE_COPY_DEST and can declare CopyDestination usage only.",
                nameof(desc));
        }
    }

}

public sealed partial class Device
{

    private static void ValidatePlacement(NativeHeap heap, ulong offset, in ResourceRequirements requirements)
    {
        if (offset % requirements.Alignment != 0) throw new ArgumentException("Placed-resource offset does not satisfy native alignment.", nameof(offset));
        if (offset > heap.Desc.Size || requirements.Size > heap.Desc.Size - offset) throw new ArgumentException("Placed resource exceeds its heap.", nameof(offset));
        if (heap.Desc.ResourceClass != ResourceHeapClass.All && heap.Desc.ResourceClass != requirements.ResourceClass)
            throw new ArgumentException("Placed resource is incompatible with the heap class.", nameof(heap));
        if (heap.Desc.MemoryType != requirements.MemoryType)
            throw new ArgumentException("Placed resource requirements do not match the heap memory type.", nameof(heap));
    }

    internal static void ValidateRange(ulong capacity, ulong offset, ulong size)
    {
        if (size == 0 || offset > capacity || size > capacity - offset) throw new ArgumentOutOfRangeException(nameof(size));
    }

    internal static void ValidateCopyRegion(
        in TextureDesc desc,
        in TextureCopyRegion region,
        out int mipWidth,
        out int mipHeight,
        out int mipDepth)
    {
        ValidateLinearCopySampleCount(desc.SampleCount);
        (mipWidth, mipHeight, mipDepth) = ValidateTextureRegionCore(desc, region);
        ValidateDepthStencilCopy(desc, region);
    }

    internal static void ValidateTextureRegion(in TextureDesc desc, in TextureCopyRegion region)
    {
        _ = ValidateTextureRegionCore(desc, region);
        ValidateDepthStencilCopy(desc, region);
    }

    private static (int Width, int Height, int Depth) ValidateTextureRegionCore(
        in TextureDesc desc,
        in TextureCopyRegion region)
    {
        (int width, int height, int depth) = GetMipExtent(desc, region.MipLevel);
        ValidateTextureLayer(desc, region);
        ValidateTextureAspect(desc.Format, region.Aspect);
        ValidateTextureExtent(region, width, height, depth);
        ValidateTextureShape(desc.Dimension, region);
        return (width, height, depth);
    }

    private static (int Width, int Height, int Depth) GetMipExtent(in TextureDesc desc, int mipLevel)
    {
        if (mipLevel < 0 || mipLevel >= desc.MipLevels) throw new ArgumentOutOfRangeException(nameof(mipLevel));
        return (
            Math.Max(1, desc.Width >> mipLevel),
            Math.Max(1, desc.Height >> mipLevel),
            Math.Max(1, desc.Depth >> mipLevel));
    }

    private static void ValidateTextureLayer(in TextureDesc desc, in TextureCopyRegion region)
    {
        if (desc.Dimension == TextureDimension.Texture3D)
        {
            if (region.ArrayLayer != 0) throw new ArgumentOutOfRangeException(nameof(region), "A 3D texture has no array layer.");
        }
        else if (region.ArrayLayer < 0 || region.ArrayLayer >= desc.ArrayLayers)
        {
            throw new ArgumentOutOfRangeException(nameof(region));
        }
    }

    private static void ValidateTextureAspect(Format format, TextureAspect aspect)
    {
        TextureAspect allowed = format switch
        {
            Format.D24UNormS8UInt => TextureAspect.Depth | TextureAspect.Stencil,
            Format.D32Float => TextureAspect.Depth,
            _ => TextureAspect.Color,
        };
        byte aspectBits = (byte)aspect;
        if (aspectBits == 0 || !BitOperations.IsPow2(aspectBits) || (aspect & allowed) == 0)
            throw new ArgumentOutOfRangeException(nameof(aspect), "A copy region selects exactly one valid texture plane.");
    }

    private static void ValidateTextureExtent(in TextureCopyRegion region, int width, int height, int depth)
    {
        if (region.X < 0 || region.Y < 0 || region.Z < 0 || region.Width <= 0 || region.Height <= 0 || region.Depth <= 0 ||
            region.X > width - region.Width || region.Y > height - region.Height || region.Z > depth - region.Depth)
            throw new ArgumentOutOfRangeException(nameof(region), "The texture copy region exceeds its mip extent.");
    }

    private static void ValidateTextureShape(TextureDimension dimension, in TextureCopyRegion region)
    {
        if (dimension == TextureDimension.Texture1D && (region.Y != 0 || region.Height != 1))
            throw new ArgumentOutOfRangeException(nameof(region), "A 1D texture copy has Y=0 and Height=1.");
        if (dimension != TextureDimension.Texture3D && (region.Z != 0 || region.Depth != 1))
            throw new ArgumentOutOfRangeException(nameof(region), "A non-3D texture copy has Z=0 and Depth=1.");
    }

    private static void ValidateDepthStencilCopy(in TextureDesc desc, in TextureCopyRegion region)
    {
        if ((desc.Usage & TextureUsage.DepthStencilAttachment) != 0 && !IsWholeSubresource(desc, region))
            throw new NotSupportedException("D3D12 depth-stencil texture copies must cover the whole selected subresource.");
    }

    private static void ValidateLinearCopySampleCount(int sampleCount)
    {
        if (sampleCount != 1) throw new NotSupportedException("Multisampled textures must be resolved before linear-buffer copies.");
    }

    internal static bool IsWholeSubresource(in TextureDesc desc, in TextureCopyRegion region)
    {
        int width = Math.Max(1, desc.Width >> region.MipLevel);
        int height = Math.Max(1, desc.Height >> region.MipLevel);
        int depth = Math.Max(1, desc.Depth >> region.MipLevel);
        return region.X == 0 && region.Y == 0 && region.Z == 0 &&
            region.Width == width && region.Height == height && region.Depth == depth;
    }

    internal static void ResolveBufferRange(in BufferDesc desc, in BufferRange range, out ulong offset, out ulong size)
    {
        offset = range.Offset;
        size = range.Size == ulong.MaxValue ? checked(desc.Size - offset) : range.Size;
        ValidateRange(desc.Size, offset, size);
    }

    internal static uint NativeSubresource(in TextureDesc desc, int mip, int layer, TextureAspect aspect)
    {
        uint plane = aspect == TextureAspect.Stencil ? 1u : 0u;
        uint arrayLayer = desc.Dimension == TextureDimension.Texture3D ? 0u : checked((uint)layer);
        return checked((uint)mip + arrayLayer * (uint)desc.MipLevels + plane * (uint)desc.MipLevels * (uint)desc.ArrayLayers);
    }

    private static ulong AlignUp(ulong value, ulong alignment) => checked((value + alignment - 1) & ~(alignment - 1));
}

internal sealed class NativeHeap : NativeLifetime
{
    private int _liveChildren;
    private int _nativeChildren;

    public NativeHeap(ID3D12Heap heap, HeapDesc desc, PhysicalAllocationId allocationId)
    {
        Heap = heap;
        Desc = desc;
        AllocationId = allocationId;
    }

    public ID3D12Heap Heap { get; }
    public HeapDesc Desc { get; }
    public PhysicalAllocationId AllocationId { get; }
    public int ChildCount => Volatile.Read(ref _liveChildren);
    public override bool CanDisposeNative => Volatile.Read(ref _nativeChildren) == 0;

    public void AddChild(ResourceHeapClass resourceClass)
    {
        if (Desc.ResourceClass != ResourceHeapClass.All && Desc.ResourceClass != resourceClass)
            throw new InvalidOperationException("Placed resource is incompatible with its heap.");
        Interlocked.Increment(ref _liveChildren);
        Interlocked.Increment(ref _nativeChildren);
    }

    public void RemoveChild()
    {
        if (Interlocked.Decrement(ref _liveChildren) < 0) throw new InvalidOperationException("Heap live-child count underflow.");
    }

    public void ReleaseNativeChild()
    {
        if (Interlocked.Decrement(ref _nativeChildren) < 0) throw new InvalidOperationException("Heap native-child count underflow.");
    }

    protected override void DisposeNative() => Heap.Dispose();
}

internal sealed class NativeBuffer : NativeLifetime
{
    private int _views;
    private int _mapped;
    public NativeBuffer(
        ID3D12Resource resource,
        BufferDesc desc,
        MemoryType memoryType,
        ResourceStates initialState,
        NativeHeap? parent,
        PhysicalAllocationInfo allocation)
    {
        Resource = resource;
        Desc = desc;
        MemoryType = memoryType;
        InitialState = initialState;
        Parent = parent;
        Allocation = allocation;
    }

    public ID3D12Resource Resource { get; }
    public BufferDesc Desc { get; }
    public MemoryType MemoryType { get; }
    public ResourceStates InitialState { get; }
    public NativeHeap? Parent { get; }
    public PhysicalAllocationInfo Allocation { get; }
    public SomeEngine.Graphics.ResidencyPriority Priority { get; set; } = SomeEngine.Graphics.ResidencyPriority.Normal;
    public int ViewCount => Volatile.Read(ref _views);
    public bool IsMapped => Volatile.Read(ref _mapped) != 0;

    public bool TryBeginMapping() => Interlocked.CompareExchange(ref _mapped, 1, 0) == 0;

    public void EndMapping()
    {
        if (Interlocked.Exchange(ref _mapped, 0) == 0)
            throw new InvalidOperationException("Buffer mapping state underflow.");
    }

    public void AddView() => Interlocked.Increment(ref _views);

    public void RemoveView()
    {
        if (Interlocked.Decrement(ref _views) < 0) throw new InvalidOperationException("Buffer view count underflow.");
    }

    protected override void DisposeNative()
    {
        try
        {
            Resource.Dispose();
        }
        finally
        {
            Parent?.ReleaseNativeChild();
        }
    }
}

internal sealed class NativeTexture : NativeLifetime
{
    private int _views;

    public NativeTexture(
        ID3D12Resource resource,
        TextureDesc desc,
        MemoryType memoryType,
        ResourceStates initialState,
        NativeHeap? parent,
        PhysicalAllocationInfo allocation,
        bool isSwapchainImage = false)
    {
        Resource = resource;
        Desc = desc;
        MemoryType = memoryType;
        InitialState = initialState;
        Parent = parent;
        Allocation = allocation;
        IsSwapchainImage = isSwapchainImage;
    }

    public ID3D12Resource Resource { get; }
    public TextureDesc Desc { get; }
    public MemoryType MemoryType { get; }
    public ResourceStates InitialState { get; }
    public NativeHeap? Parent { get; }
    public PhysicalAllocationInfo Allocation { get; }
    public bool IsSwapchainImage { get; }
    public SomeEngine.Graphics.ResidencyPriority Priority { get; set; } = SomeEngine.Graphics.ResidencyPriority.Normal;
    public int ViewCount => Volatile.Read(ref _views);

    public void AddView() => Interlocked.Increment(ref _views);

    public void RemoveView()
    {
        if (Interlocked.Decrement(ref _views) < 0) throw new InvalidOperationException("Texture view count underflow.");
    }

    protected override void DisposeNative()
    {
        try
        {
            Resource.Dispose();
        }
        finally
        {
            Parent?.ReleaseNativeChild();
        }
    }
}
