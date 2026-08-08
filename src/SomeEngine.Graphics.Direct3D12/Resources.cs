using System.Numerics;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;
using NativeHeapDesc = Silk.NET.Direct3D12.HeapDesc;
using NativeHeapFlags = Silk.NET.Direct3D12.HeapFlags;
using NativeRange = Silk.NET.Direct3D12.Range;
using NativeResource = Silk.NET.Direct3D12.ID3D12Resource;
using NativeResourceDesc = Silk.NET.Direct3D12.ResourceDesc;
using NativeResourceDimension = Silk.NET.Direct3D12.ResourceDimension;
using NativeTextureLayout = Silk.NET.Direct3D12.TextureLayout;
using DxgiFormat = Silk.NET.DXGI.Format;
using NativeSampleDesc = Silk.NET.DXGI.SampleDesc;

namespace SomeEngine.Graphics.Direct3D12;

public sealed unsafe partial class D3D12Backend
{
    public MemoryRequirements GetBufferMemoryRequirements(
        Device device,
        in BufferDesc desc,
        MemoryType memoryType = MemoryType.DeviceLocal)
    {
        D3D12Device nativeDevice = NativeCast.Device(device);
        nativeDevice.ThrowIfUnavailable();
        NativeResourceDesc native = CreateBufferDescription(desc);
        ValidateBufferMemoryType(desc, memoryType);
        ResourceAllocationInfo allocation = nativeDevice.Native->GetResourceAllocationInfo(
            nativeDevice.EnabledNodeMask,
            1,
            &native);
        EnsureAllocationInfo(allocation, "Buffer");
        SomeEngine.Graphics.HeapFlags flags = SomeEngine.Graphics.HeapFlags.Buffers;
        if ((desc.Usages & BufferUsages.Shareable) != 0)
            flags |= SomeEngine.Graphics.HeapFlags.Shareable;
        return new MemoryRequirements(allocation.SizeInBytes, allocation.Alignment, flags);
    }

    public MemoryRequirements GetTextureMemoryRequirements(Device device, in TextureDesc desc)
    {
        D3D12Device nativeDevice = NativeCast.Device(device);
        nativeDevice.ThrowIfUnavailable();
        NativeResourceDesc native = CreateTextureDescription(desc);
        ResourceAllocationInfo allocation = nativeDevice.Native->GetResourceAllocationInfo(
            nativeDevice.EnabledNodeMask,
            1,
            &native);
        EnsureAllocationInfo(allocation, "Texture");
        SomeEngine.Graphics.HeapFlags flags = SomeEngine.Graphics.HeapFlags.Textures;
        if ((desc.Usages & (TextureUsages.ColorAttachment |
                           TextureUsages.DepthStencilAttachment)) != 0)
            flags |= SomeEngine.Graphics.HeapFlags.Attachments;
        if ((desc.Usages & TextureUsages.Shareable) != 0)
            flags |= SomeEngine.Graphics.HeapFlags.Shareable;
        return new MemoryRequirements(allocation.SizeInBytes, allocation.Alignment, flags);
    }

    public TextureCopyFootprint GetTextureCopyFootprint(
        Device device,
        in TextureDesc desc,
        in BufferTextureCopy copy,
        ulong requestedBufferOffset = 0)
    {
        D3D12Device nativeDevice = NativeCast.Device(device);
        nativeDevice.ThrowIfUnavailable();
        if ((requestedBufferOffset & 511) != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(requestedBufferOffset),
                "A Texture footprint base offset must be 512-byte aligned.");
        }
        NativeResourceDesc native = CreateTextureDescription(desc);
        ValidateTextureRegion(
            CreateTextureInfo(desc, 0, 0),
            copy.MipLevel,
            copy.ArrayLayer,
            copy.Aspect,
            copy.X,
            copy.Y,
            copy.Z,
            copy.Width,
            copy.Height,
            copy.Depth);
        uint plane = FormatMappings.PlaneIndex(desc.Format, copy.Aspect);
        uint arrayLayer = desc.Dimension == TextureDimension.Texture3D ? 0 : copy.ArrayLayer;
        uint subresource = copy.MipLevel +
            arrayLayer * desc.MipLevelCount +
            plane * desc.MipLevelCount * desc.ArrayLayerCount;

        PlacedSubresourceFootprint footprint = GetNativeCopyFootprint(
            nativeDevice,
            native,
            subresource,
            requestedBufferOffset,
            out uint rows,
            out ulong rowSize,
            out ulong totalSize);
        return new TextureCopyFootprint(
            footprint.Offset,
            footprint.Footprint.RowPitch,
            rows,
            rowSize,
            totalSize);
    }

    private static PlacedSubresourceFootprint GetNativeCopyFootprint(
        D3D12Device device,
        in NativeResourceDesc description,
        uint subresource,
        ulong baseOffset,
        out uint rowCount,
        out ulong rowSize,
        out ulong totalSize)
    {
        NativeResourceDesc native = description;
        PlacedSubresourceFootprint footprint = default;
        uint nativeRowCount = 0;
        ulong nativeRowSize = 0;
        ulong nativeTotalSize = 0;
        device.Native->GetCopyableFootprints(
            &native,
            subresource,
            1,
            baseOffset,
            &footprint,
            &nativeRowCount,
            &nativeRowSize,
            &nativeTotalSize);
        if (nativeTotalSize == ulong.MaxValue)
        {
            throw new GraphicsException(
                GraphicsError.NativeFailure,
                "ID3D12Device::GetCopyableFootprints rejected the Texture description.");
        }
        rowCount = nativeRowCount;
        rowSize = nativeRowSize;
        totalSize = nativeTotalSize;
        return footprint;
    }

    public Heap CreateHeap(Device device, in HeapDesc desc)
    {
        D3D12Device nativeDevice = NativeCast.Device(device);
        nativeDevice.ThrowIfUnavailable();
        ValidateHeapDescription(nativeDevice, desc);
        NativeHeapDesc nativeDescription = new()
        {
            SizeInBytes = desc.Size,
            Alignment = desc.Alignment,
            Properties = CreateHeapProperties(
                desc.MemoryType,
                desc.CreationNodeMask,
                desc.VisibleNodeMask),
            Flags = ToNativeHeapFlags(desc.Flags),
        };

        ID3D12Heap* native = null;
        Guid iid = ID3D12Heap.Guid;
        NativeCall.ThrowIfFailed(
            nativeDevice.Native->CreateHeap(&nativeDescription, &iid, (void**)&native),
            $"ID3D12Device::CreateHeap(size={desc.Size}, alignment={desc.Alignment}, " +
            $"memory={desc.MemoryType}, flags={desc.Flags}, " +
            $"creationNodeMask={desc.CreationNodeMask}, visibleNodeMask={desc.VisibleNodeMask})");

        D3D12Heap heap;
        try
        {
            heap = new D3D12Heap(
                nativeDevice,
                native,
                new HeapInfo(
                    desc.Size,
                    desc.Alignment,
                    desc.MemoryType,
                    desc.Flags,
                    desc.CreationNodeMask,
                    desc.VisibleNodeMask),
                desc.Label);
        }
        catch
        {
            _ = native->Release();
            throw;
        }
        nativeDevice.RegisterChild(heap);
        return heap;
    }

    public Buffer CreateBuffer(
        Device device,
        in BufferDesc desc,
        MemoryType memoryType = MemoryType.DeviceLocal)
    {
        D3D12Device nativeDevice = NativeCast.Device(device);
        nativeDevice.ThrowIfUnavailable();
        NativeResourceDesc nativeDescription = CreateBufferDescription(desc);
        ValidateBufferMemoryType(desc, memoryType);
        MemoryRequirements requirements = GetBufferMemoryRequirements(device, desc, memoryType);
        (PipelineSync sync, ResourceAccess access) = InitialBufferAccess(memoryType);
        NativeResource* native = CreateCommittedResource(
            nativeDevice,
            memoryType,
            (desc.Usages & BufferUsages.Shareable) != 0,
            nativeDescription,
            ReadOnlySpan<DxgiFormat>.Empty);
        D3D12Buffer buffer;
        try
        {
            buffer = new D3D12Buffer(
                nativeDevice,
                heap: null,
                native,
                new BufferInfo(desc.Size, desc.Usages, memoryType, 0, requirements.Size),
                sync,
                access,
                desc.Label);
        }
        catch
        {
            _ = native->Release();
            throw;
        }
        nativeDevice.RegisterChild(buffer);
        return buffer;
    }

    public Buffer CreatePlacedBuffer(
        Device device,
        Heap heap,
        ulong offset,
        in BufferDesc desc)
    {
        D3D12Device nativeDevice = NativeCast.Device(device);
        D3D12Heap nativeHeap = NativeCast.Heap(heap);
        nativeDevice.ThrowIfUnavailable();
        nativeHeap.ThrowIfDisposed();
        NativeResourceDesc nativeDescription = CreateBufferDescription(desc);
        ValidateBufferMemoryType(desc, nativeHeap.Info.MemoryType);
        MemoryRequirements requirements =
            GetBufferMemoryRequirements(device, desc, nativeHeap.Info.MemoryType);
        ValidatePlacement(
            nativeHeap,
            offset,
            requirements,
            SomeEngine.Graphics.HeapFlags.Buffers,
            (desc.Usages & BufferUsages.Shareable) != 0);
        (PipelineSync sync, ResourceAccess access) = InitialBufferAccess(nativeHeap.Info.MemoryType);
        NativeResource* native = CreatePlacedResource(
            nativeDevice,
            nativeHeap,
            offset,
            nativeDescription,
            ReadOnlySpan<DxgiFormat>.Empty);
        D3D12Buffer buffer;
        try
        {
            buffer = new D3D12Buffer(
                nativeDevice,
                nativeHeap,
                native,
                new BufferInfo(
                    desc.Size,
                    desc.Usages,
                    nativeHeap.Info.MemoryType,
                    offset,
                    requirements.Size),
                sync,
                access,
                desc.Label);
        }
        catch
        {
            _ = native->Release();
            throw;
        }
        nativeDevice.RegisterChild(buffer);
        try
        {
            nativeHeap.RegisterResource(buffer);
        }
        catch
        {
            buffer.Dispose();
            throw;
        }
        return buffer;
    }

    public Texture CreateTexture(Device device, in TextureDesc desc)
    {
        D3D12Device nativeDevice = NativeCast.Device(device);
        nativeDevice.ThrowIfUnavailable();
        NativeResourceDesc nativeDescription = CreateTextureDescription(desc);
        MemoryRequirements requirements = GetTextureMemoryRequirements(device, desc);
        TextureInfo info = CreateTextureInfo(desc, 0, requirements.Size);
        DxgiFormat[] castableFormats = CreateCastableFormats(desc);
        NativeResource* native = CreateCommittedResource(
            nativeDevice,
            MemoryType.DeviceLocal,
            (desc.Usages & TextureUsages.Shareable) != 0,
            nativeDescription,
            castableFormats);
        D3D12Texture texture;
        try
        {
            texture = new D3D12Texture(
                nativeDevice,
                heap: null,
                native,
                info,
                desc.Label);
        }
        catch
        {
            _ = native->Release();
            throw;
        }
        nativeDevice.RegisterChild(texture);
        return texture;
    }

    public Texture CreatePlacedTexture(
        Device device,
        Heap heap,
        ulong offset,
        in TextureDesc desc)
    {
        D3D12Device nativeDevice = NativeCast.Device(device);
        D3D12Heap nativeHeap = NativeCast.Heap(heap);
        nativeDevice.ThrowIfUnavailable();
        nativeHeap.ThrowIfDisposed();
        if (nativeHeap.Info.MemoryType != MemoryType.DeviceLocal)
        {
            throw new ArgumentException(
                "Textures require a DeviceLocal Heap.",
                nameof(heap));
        }
        NativeResourceDesc nativeDescription = CreateTextureDescription(desc);
        MemoryRequirements requirements = GetTextureMemoryRequirements(device, desc);
        SomeEngine.Graphics.HeapFlags requiredClass =
            (desc.Usages & (TextureUsages.ColorAttachment |
                            TextureUsages.DepthStencilAttachment)) != 0
                ? SomeEngine.Graphics.HeapFlags.Attachments
                : SomeEngine.Graphics.HeapFlags.Textures;
        ValidatePlacement(
            nativeHeap,
            offset,
            requirements,
            requiredClass,
            (desc.Usages & TextureUsages.Shareable) != 0);
        TextureInfo info = CreateTextureInfo(desc, offset, requirements.Size);
        DxgiFormat[] castableFormats = CreateCastableFormats(desc);
        NativeResource* native = CreatePlacedResource(
            nativeDevice,
            nativeHeap,
            offset,
            nativeDescription,
            castableFormats);
        D3D12Texture texture;
        try
        {
            texture = new D3D12Texture(
                nativeDevice,
                nativeHeap,
                native,
                info,
                desc.Label);
        }
        catch
        {
            _ = native->Release();
            throw;
        }
        nativeDevice.RegisterChild(texture);
        try
        {
            nativeHeap.RegisterResource(texture);
        }
        catch
        {
            texture.Dispose();
            throw;
        }
        return texture;
    }

    public MappedBuffer Map(Buffer buffer, MapType type, in BufferRange range)
    {
        D3D12Buffer nativeBuffer = NativeCast.Buffer(buffer);
        BufferRange resolved = range.Resolve(nativeBuffer.Info.Size);
        if (resolved.Size > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(range), "A mapped Span cannot exceed Int32.MaxValue.");

        return nativeBuffer.Map(type, resolved);
    }

    private static NativeResource* CreateCommittedResource(
        D3D12Device device,
        MemoryType memoryType,
        bool shareable,
        in NativeResourceDesc description,
        ReadOnlySpan<DxgiFormat> castableFormats)
    {
        HeapProperties properties = CreateHeapProperties(
            memoryType,
            device.EnabledNodeMask,
            device.EnabledNodeMask);
        NativeHeapFlags heapFlags = shareable ? NativeHeapFlags.Shared : NativeHeapFlags.None;
        NativeResource* resource = null;
        Guid iid = NativeResource.Guid;

        if (device.EnhancedBarriers)
        {
            ResourceDesc1 description1 = ToDescription1(description);
            BarrierLayout layout = InitialLayout(memoryType, description.Dimension);
            fixed (DxgiFormat* formats = castableFormats)
            {
                NativeCall.ThrowIfFailed(
                    device.Native->CreateCommittedResource3(
                        &properties,
                        heapFlags,
                        &description1,
                        layout,
                        null,
                        null,
                        (uint)castableFormats.Length,
                        formats,
                        &iid,
                        (void**)&resource),
                    "ID3D12Device10::CreateCommittedResource3");
            }
        }
        else
        {
            NativeResourceDesc copy = description;
            bool accelerationStructure =
                (copy.Flags & ResourceFlags.RaytracingAccelerationStructure) != 0;
            copy.Flags &= ~ResourceFlags.RaytracingAccelerationStructure;
            ResourceStates initialState = accelerationStructure
                ? ResourceStates.RaytracingAccelerationStructure
                : InitialLegacyState(memoryType);
            int createResult = device.Native->CreateCommittedResource(
                &properties,
                heapFlags,
                &copy,
                initialState,
                null,
                &iid,
                (void**)&resource);
            ThrowIfDeviceFailed(
                device,
                createResult,
                "ID3D12Device::CreateCommittedResource");
        }

        return resource;
    }

    private static NativeResource* CreatePlacedResource(
        D3D12Device device,
        D3D12Heap heap,
        ulong offset,
        in NativeResourceDesc description,
        ReadOnlySpan<DxgiFormat> castableFormats)
    {
        NativeResource* resource = null;
        Guid iid = NativeResource.Guid;

        if (device.EnhancedBarriers)
        {
            ResourceDesc1 description1 = ToDescription1(description);
            BarrierLayout layout = InitialLayout(heap.Info.MemoryType, description.Dimension);
            fixed (DxgiFormat* formats = castableFormats)
            {
                NativeCall.ThrowIfFailed(
                    device.Native->CreatePlacedResource2(
                        heap.Native,
                        offset,
                        &description1,
                        layout,
                        null,
                        (uint)castableFormats.Length,
                        formats,
                        &iid,
                        (void**)&resource),
                    "ID3D12Device10::CreatePlacedResource2");
            }
        }
        else
        {
            NativeResourceDesc copy = description;
            bool accelerationStructure =
                (copy.Flags & ResourceFlags.RaytracingAccelerationStructure) != 0;
            copy.Flags &= ~ResourceFlags.RaytracingAccelerationStructure;
            ResourceStates initialState = accelerationStructure
                ? ResourceStates.RaytracingAccelerationStructure
                : InitialLegacyState(heap.Info.MemoryType);
            int createResult = device.Native->CreatePlacedResource(
                heap.Native,
                offset,
                &copy,
                initialState,
                null,
                &iid,
                (void**)&resource);
            ThrowIfDeviceFailed(
                device,
                createResult,
                "ID3D12Device::CreatePlacedResource");
        }

        return resource;
    }

    private static NativeResourceDesc CreateBufferDescription(in BufferDesc desc)
    {
        if (desc.Size == 0)
            throw new ArgumentOutOfRangeException(nameof(desc), "A Buffer size must be nonzero.");
        const BufferUsages knownUsages =
            BufferUsages.CopySource |
            BufferUsages.CopyDestination |
            BufferUsages.Constant |
            BufferUsages.ShaderRead |
            BufferUsages.ShaderWrite |
            BufferUsages.Vertex |
            BufferUsages.Index |
            BufferUsages.Indirect |
            BufferUsages.AccelerationStructure |
            BufferUsages.AccelerationStructureInput |
            BufferUsages.Predication |
            BufferUsages.StreamOutput |
            BufferUsages.QueryResolve |
            BufferUsages.Shareable;
        if ((desc.Usages & ~knownUsages) != 0)
            throw new ArgumentOutOfRangeException(nameof(desc), "The Buffer usage contains unknown bits.");
        return new NativeResourceDesc(
            NativeResourceDimension.Buffer,
            0,
            desc.Size,
            1,
            1,
            1,
            DxgiFormat.FormatUnknown,
            new NativeSampleDesc(1, 0),
            NativeTextureLayout.LayoutRowMajor,
            ToResourceFlags(desc.Usages));
    }

    private static NativeResourceDesc CreateTextureDescription(in TextureDesc desc)
    {
        ValidateTextureDescription(desc);
        NativeResourceDimension dimension = desc.Dimension switch
        {
            TextureDimension.Texture1D => NativeResourceDimension.Texture1D,
            TextureDimension.Texture2D => NativeResourceDimension.Texture2D,
            TextureDimension.Texture3D => NativeResourceDimension.Texture3D,
            _ => throw new ArgumentOutOfRangeException(nameof(desc)),
        };
        ushort depthOrArray = checked((ushort)(
            desc.Dimension == TextureDimension.Texture3D
                ? desc.Depth
                : desc.ArrayLayerCount));
        return new NativeResourceDesc(
            dimension,
            0,
            desc.Width,
            desc.Dimension == TextureDimension.Texture1D ? 1u : desc.Height,
            depthOrArray,
            checked((ushort)desc.MipLevelCount),
            FormatMappings.ToResourceFormat(desc.Format, desc.PermittedViewFormats),
            new NativeSampleDesc(desc.SampleCount, 0),
            NativeTextureLayout.LayoutUnknown,
            ToResourceFlags(desc.Usages));
    }

    private static void ValidateHeapDescription(D3D12Device device, in HeapDesc desc)
    {
        if (desc.Size == 0)
            throw new ArgumentOutOfRangeException(nameof(desc), "A Heap size must be nonzero.");
        if (desc.Alignment is not (0 or 65_536 or 4_194_304))
            throw new ArgumentOutOfRangeException(nameof(desc), "The Heap alignment is not supported by D3D12.");
        if (!Enum.IsDefined(desc.MemoryType))
            throw new ArgumentOutOfRangeException(nameof(desc), "The Heap memory type is unknown.");
        const SomeEngine.Graphics.HeapFlags knownFlags =
            SomeEngine.Graphics.HeapFlags.Buffers |
            SomeEngine.Graphics.HeapFlags.Textures |
            SomeEngine.Graphics.HeapFlags.Attachments |
            SomeEngine.Graphics.HeapFlags.Shareable;
        if ((desc.Flags & ~knownFlags) != 0)
            throw new ArgumentOutOfRangeException(nameof(desc), "The Heap flags contain unknown bits.");
        uint enabled = device.EnabledNodeMask;
        if (desc.CreationNodeMask == 0 ||
            desc.VisibleNodeMask == 0 ||
            (desc.CreationNodeMask & ~enabled) != 0 ||
            (desc.VisibleNodeMask & ~enabled) != 0 ||
            (desc.CreationNodeMask & desc.VisibleNodeMask) != desc.CreationNodeMask)
        {
            throw new ArgumentException(
                "Heap node masks must be nonzero enabled nodes and every creation node must be visible.",
                nameof(desc));
        }
    }

    private static void ValidateBufferMemoryType(in BufferDesc desc, MemoryType memoryType)
    {
        const BufferUsages uploadUsages =
            BufferUsages.CopySource |
            BufferUsages.Constant |
            BufferUsages.ShaderRead |
            BufferUsages.Vertex |
            BufferUsages.Index |
            BufferUsages.Indirect |
            BufferUsages.AccelerationStructureInput |
            BufferUsages.Predication |
            BufferUsages.Shareable;
        const BufferUsages readbackUsages =
            BufferUsages.CopyDestination |
            BufferUsages.QueryResolve |
            BufferUsages.Shareable;
        bool valid = memoryType switch
        {
            MemoryType.DeviceLocal => true,
            MemoryType.Upload => (desc.Usages & ~uploadUsages) == 0,
            MemoryType.Readback => (desc.Usages & ~readbackUsages) == 0,
            _ => throw new ArgumentOutOfRangeException(nameof(memoryType)),
        };
        if (!valid)
        {
            throw new ArgumentException(
                $"The Buffer usage is incompatible with {memoryType} memory.",
                nameof(desc));
        }
    }

    private static void ValidateTextureDescription(in TextureDesc desc)
    {
        if (!Enum.IsDefined(desc.Dimension))
            throw new ArgumentOutOfRangeException(nameof(desc), "The Texture dimension is unknown.");
        const TextureUsages knownUsages =
            TextureUsages.CopySource |
            TextureUsages.CopyDestination |
            TextureUsages.Sampled |
            TextureUsages.Storage |
            TextureUsages.ColorAttachment |
            TextureUsages.DepthStencilAttachment |
            TextureUsages.ShadingRate |
            TextureUsages.SamplerFeedback |
            TextureUsages.Shareable;
        if ((desc.Usages & ~knownUsages) != 0)
            throw new ArgumentOutOfRangeException(nameof(desc), "The Texture usage contains unknown bits.");
        if (desc.Width == 0 || desc.Height == 0 || desc.Depth == 0 ||
            desc.MipLevelCount == 0 || desc.ArrayLayerCount == 0 || desc.SampleCount == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(desc), "Texture dimensions, counts and sample count must be nonzero.");
        }
        if (desc.Dimension == TextureDimension.Texture1D &&
            (desc.Height != 1 || desc.Depth != 1))
            throw new ArgumentException("A 1D Texture has Height=1 and Depth=1.", nameof(desc));
        if (desc.Dimension == TextureDimension.Texture2D && desc.Depth != 1)
            throw new ArgumentException("A 2D Texture has Depth=1.", nameof(desc));
        if (desc.Dimension == TextureDimension.Texture3D && desc.ArrayLayerCount != 1)
            throw new ArgumentException("A 3D Texture has exactly one array layer.", nameof(desc));
        uint depthOrArray = desc.Dimension == TextureDimension.Texture3D
            ? desc.Depth
            : desc.ArrayLayerCount;
        if (depthOrArray > ushort.MaxValue || desc.MipLevelCount > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(desc), "Texture depth/array and mip counts must fit D3D12 fields.");

        uint largestDimension = desc.Dimension switch
        {
            TextureDimension.Texture1D => desc.Width,
            TextureDimension.Texture2D => Math.Max(desc.Width, desc.Height),
            TextureDimension.Texture3D => Math.Max(desc.Width, Math.Max(desc.Height, desc.Depth)),
            _ => throw new ArgumentOutOfRangeException(nameof(desc)),
        };
        uint maximumMipCount = checked((uint)BitOperations.Log2(largestDimension) + 1);
        if (desc.MipLevelCount > maximumMipCount)
            throw new ArgumentOutOfRangeException(nameof(desc), "The Texture has more mip levels than its dimensions permit.");

        bool multisampled = desc.SampleCount != 1;
        if (multisampled &&
            (!BitOperations.IsPow2(desc.SampleCount) ||
             desc.Dimension != TextureDimension.Texture2D ||
             desc.MipLevelCount != 1 ||
             (desc.Usages & TextureUsages.Storage) != 0 ||
             FormatMappings.IsBlockCompressed(desc.Format)))
        {
            throw new ArgumentException("The multisampled Texture description is not representable by D3D12.", nameof(desc));
        }

        bool depthStencil = FormatMappings.IsDepthStencil(desc.Format);
        if (depthStencil && (desc.Usages & TextureUsages.ColorAttachment) != 0)
            throw new ArgumentException("A depth/stencil format cannot be a color attachment.", nameof(desc));
        if (!depthStencil && (desc.Usages & TextureUsages.DepthStencilAttachment) != 0)
            throw new ArgumentException("A color format cannot be a depth/stencil attachment.", nameof(desc));
        if (depthStencil && (desc.Usages & TextureUsages.Storage) != 0)
            throw new ArgumentException("D3D12 depth/stencil formats cannot be storage textures.", nameof(desc));
        if (depthStencil &&
            desc.Dimension == TextureDimension.Texture3D &&
            (desc.Usages & TextureUsages.DepthStencilAttachment) != 0)
            throw new ArgumentException("D3D12 does not support 3D depth/stencil attachments.", nameof(desc));
        if (FormatMappings.IsBlockCompressed(desc.Format) &&
            (desc.Usages & (TextureUsages.Storage |
                            TextureUsages.ColorAttachment |
                            TextureUsages.DepthStencilAttachment |
                            TextureUsages.ShadingRate |
                            TextureUsages.SamplerFeedback)) != 0)
        {
            throw new ArgumentException("A block-compressed format has an incompatible Texture usage.", nameof(desc));
        }

        DxgiFormat family = FormatMappings.ToTypelessFamily(desc.Format);
        for (int index = 0; index < desc.PermittedViewFormats.Length; index++)
        {
            Format permitted = desc.PermittedViewFormats[index];
            if (FormatMappings.ToTypelessFamily(permitted) != family)
            {
                throw new ArgumentException(
                    "Every permitted view format must belong to the Texture format family.",
                    nameof(desc));
            }
            for (int previous = 0; previous < index; previous++)
            {
                if (desc.PermittedViewFormats[previous] == permitted)
                    throw new ArgumentException("Permitted view formats must be unique.", nameof(desc));
            }
        }
    }

    private static void ValidateTextureSubresource(
        in TextureDesc desc,
        uint mipLevel,
        uint arrayLayer,
        TextureAspects aspect)
    {
        if (mipLevel >= desc.MipLevelCount)
            throw new ArgumentOutOfRangeException(nameof(mipLevel));
        if (desc.Dimension == TextureDimension.Texture3D)
        {
            if (arrayLayer != 0)
                throw new ArgumentOutOfRangeException(nameof(arrayLayer));
        }
        else if (arrayLayer >= desc.ArrayLayerCount)
        {
            throw new ArgumentOutOfRangeException(nameof(arrayLayer));
        }
        _ = FormatMappings.PlaneIndex(desc.Format, aspect);
    }

    private static void ValidatePlacement(
        D3D12Heap heap,
        ulong offset,
        in MemoryRequirements requirements,
        SomeEngine.Graphics.HeapFlags requiredClass,
        bool shareable)
    {
        if (offset % requirements.Alignment != 0 ||
            offset > heap.Info.Size ||
            requirements.Size > heap.Info.Size - offset)
        {
            throw new ArgumentOutOfRangeException(
                nameof(offset),
                "The placed resource range is misaligned or escapes the Heap.");
        }
        SomeEngine.Graphics.HeapFlags classes = heap.Info.Flags &
            (SomeEngine.Graphics.HeapFlags.Buffers |
             SomeEngine.Graphics.HeapFlags.Textures |
             SomeEngine.Graphics.HeapFlags.Attachments);
        bool unrestricted = classes == SomeEngine.Graphics.HeapFlags.None ||
            BitOperations.PopCount((uint)classes) > 1;
        if (!unrestricted && (classes & requiredClass) == 0)
            throw new ArgumentException("The Heap class is incompatible with the placed resource.", nameof(heap));
        if (shareable && (heap.Info.Flags & SomeEngine.Graphics.HeapFlags.Shareable) == 0)
            throw new ArgumentException("A shareable resource requires a shareable Heap.", nameof(heap));
    }

    private static ResourceFlags ToResourceFlags(BufferUsages usages)
    {
        ResourceFlags result = ResourceFlags.None;
        if ((usages & (BufferUsages.ShaderWrite |
                       BufferUsages.AccelerationStructure |
                       BufferUsages.StreamOutput)) != 0)
            result |= ResourceFlags.AllowUnorderedAccess;
        if ((usages & BufferUsages.AccelerationStructure) != 0)
            result |= ResourceFlags.RaytracingAccelerationStructure;
        return result;
    }

    private static ResourceFlags ToResourceFlags(TextureUsages usages)
    {
        ResourceFlags result = ResourceFlags.None;
        if ((usages & TextureUsages.ColorAttachment) != 0)
            result |= ResourceFlags.AllowRenderTarget;
        if ((usages & TextureUsages.DepthStencilAttachment) != 0)
            result |= ResourceFlags.AllowDepthStencil;
        if ((usages & (TextureUsages.Storage | TextureUsages.SamplerFeedback)) != 0)
            result |= ResourceFlags.AllowUnorderedAccess;
        if ((usages & TextureUsages.DepthStencilAttachment) != 0 &&
            (usages & TextureUsages.Sampled) == 0)
            result |= ResourceFlags.DenyShaderResource;
        return result;
    }

    private static HeapProperties CreateHeapProperties(
        MemoryType memoryType,
        uint creationNodeMask,
        uint visibleNodeMask) =>
        new(
            memoryType switch
            {
                MemoryType.DeviceLocal => HeapType.Default,
                MemoryType.Upload => HeapType.Upload,
                MemoryType.Readback => HeapType.Readback,
                _ => throw new ArgumentOutOfRangeException(nameof(memoryType)),
            },
            CpuPageProperty.Unknown,
            MemoryPool.Unknown,
            creationNodeMask,
            visibleNodeMask);

    private static NativeHeapFlags ToNativeHeapFlags(SomeEngine.Graphics.HeapFlags flags)
    {
        NativeHeapFlags result = NativeHeapFlags.None;
        SomeEngine.Graphics.HeapFlags classes =
            flags & (SomeEngine.Graphics.HeapFlags.Buffers |
                     SomeEngine.Graphics.HeapFlags.Textures |
                     SomeEngine.Graphics.HeapFlags.Attachments);
        result |= classes switch
        {
            SomeEngine.Graphics.HeapFlags.Buffers => NativeHeapFlags.AllowOnlyBuffers,
            SomeEngine.Graphics.HeapFlags.Textures => NativeHeapFlags.AllowOnlyNonRTDSTextures,
            SomeEngine.Graphics.HeapFlags.Attachments => NativeHeapFlags.AllowOnlyRTDSTextures,
            _ => NativeHeapFlags.None,
        };
        if ((flags & SomeEngine.Graphics.HeapFlags.Shareable) != 0)
            result |= NativeHeapFlags.Shared;
        return result;
    }

    private static ResourceDesc1 ToDescription1(in NativeResourceDesc description) =>
        new(
            description.Dimension,
            description.Alignment,
            description.Width,
            description.Height,
            description.DepthOrArraySize,
            description.MipLevels,
            description.Format,
            description.SampleDesc,
            description.Layout,
            description.Flags);

    private static BarrierLayout InitialLayout(
        MemoryType memoryType,
        NativeResourceDimension dimension)
    {
        // Enhanced-barrier resource creation requires UNDEFINED for every buffer,
        // including buffers backed by upload and readback heaps. Heap restrictions
        // still define their usable access; buffer barriers do not carry layouts.
        if (dimension == NativeResourceDimension.Buffer)
            return BarrierLayout.Undefined;

        return memoryType switch
        {
            MemoryType.Upload => BarrierLayout.GenericRead,
            MemoryType.Readback => BarrierLayout.CopyDest,
            MemoryType.DeviceLocal => BarrierLayout.Undefined,
            _ => throw new ArgumentOutOfRangeException(nameof(memoryType)),
        };
    }

    private static ResourceStates InitialLegacyState(MemoryType memoryType) => memoryType switch
    {
        MemoryType.DeviceLocal => ResourceStates.Common,
        MemoryType.Upload => ResourceStates.GenericRead,
        MemoryType.Readback => ResourceStates.CopyDest,
        _ => throw new ArgumentOutOfRangeException(nameof(memoryType)),
    };

    private static (PipelineSync Sync, ResourceAccess Access) InitialBufferAccess(
        MemoryType memoryType) =>
        memoryType switch
        {
            MemoryType.DeviceLocal => (PipelineSync.None, ResourceAccess.NoAccess),
            MemoryType.Upload => (PipelineSync.None, ResourceAccess.Common),
            MemoryType.Readback => (PipelineSync.Copy, ResourceAccess.CopyDestination),
            _ => throw new ArgumentOutOfRangeException(nameof(memoryType)),
        };

    private static TextureInfo CreateTextureInfo(
        in TextureDesc desc,
        ulong allocationOffset,
        ulong allocationSize) =>
        new(
            desc.Dimension,
            desc.Width,
            desc.Height,
            desc.Depth,
            desc.MipLevelCount,
            desc.ArrayLayerCount,
            desc.SampleCount,
            desc.Format,
            desc.Usages,
            MemoryType.DeviceLocal,
            desc.PermittedViewFormats,
            allocationOffset,
            allocationSize);

    private static DxgiFormat[] CreateCastableFormats(in TextureDesc desc)
    {
        if (desc.PermittedViewFormats.IsEmpty)
            return [];
        DxgiFormat[] result = new DxgiFormat[desc.PermittedViewFormats.Length];
        for (int index = 0; index < result.Length; index++)
            result[index] = FormatMappings.ToDxgi(desc.PermittedViewFormats[index]);
        return result;
    }

    private static void EnsureAllocationInfo(
        in ResourceAllocationInfo allocation,
        string resourceType)
    {
        if (allocation.SizeInBytes == ulong.MaxValue)
        {
            throw new GraphicsException(
                GraphicsError.NativeFailure,
                $"Direct3D 12 rejected the {resourceType} description.");
        }
    }

    private sealed partial class D3D12Heap : Heap
    {
        private readonly D3D12Device _device;
        private readonly object _resourcesGate = new();
        private readonly HashSet<GraphicsObject> _resources =
            new(ReferenceEqualityComparer.Instance);
        private readonly NativeLease _native;
        private int _released;

        internal D3D12Heap(
            D3D12Device device,
            ID3D12Heap* native,
            in HeapInfo info,
            string? label,
            bool ownsReference = true)
            : base(device, info, label)
        {
            _device = device;
            _native = new NativeLease((IUnknown*)native, ownsReference);
        }

        internal ID3D12Heap* Native => (ID3D12Heap*)_native.Pointer;
        internal NativeLease NativeLifetime => _native;
        internal D3D12Device NativeDevice => _device;

        internal void RegisterResource(GraphicsObject resource)
        {
            lock (_resourcesGate)
            {
                ThrowIfDisposed();
                _resources.Add(resource);
            }
        }

        internal void UnregisterResource(GraphicsObject resource)
        {
            lock (_resourcesGate)
                _resources.Remove(resource);
        }

        internal override void Release(bool fromParent)
        {
            if (Interlocked.Exchange(ref _released, 1) != 0)
                return;

            GraphicsObject[] resources;
            lock (_resourcesGate)
                resources = [.. _resources];
            foreach (GraphicsObject resource in resources)
                resource.DisposeFromParent();
            lock (_resourcesGate)
                _resources.Clear();

            _native.Release();
            _device.UnregisterChild(this);
        }
    }

    private sealed partial class D3D12Buffer : Buffer
    {
        private readonly D3D12Device _device;
        private readonly D3D12Heap? _heap;
        private readonly ChildRegistry _views = new();
        private readonly NativeLease _native;
        private D3D12MappingLease? _mapping;
        private long _mappingSequence;
        private int _mapped;
        private int _released;

        internal D3D12Buffer(
            D3D12Device device,
            D3D12Heap? heap,
            NativeResource* native,
            in BufferInfo info,
            PipelineSync initialSync,
            ResourceAccess initialAccess,
            string? label,
            QueueType? initialQueueType = null,
            bool ownsReference = true)
            : base(device, heap, info, initialSync, initialAccess, label, initialQueueType)
        {
            _device = device;
            _heap = heap;
            _native = new NativeLease(
                (IUnknown*)native,
                ownsReference,
                heap?.NativeLifetime);
        }

        internal NativeResource* Native => (NativeResource*)_native.Pointer;
        internal NativeLease NativeLifetime => _native;
        internal D3D12SparseState? SparseState { get; set; }
        internal void RegisterView(GraphicsObject view) => _views.Register(this, view);
        internal void UnregisterView(GraphicsObject view) => _views.Unregister(view);

        internal MappedBuffer Map(MapType type, BufferRange range)
        {
            ThrowIfDisposed();
            _device.ThrowIfUnavailable();
            if (Interlocked.CompareExchange(ref _mapped, 1, 0) != 0)
                throw new InvalidOperationException("The Buffer already has an active mapping.");

            try
            {
                if (Volatile.Read(ref _mappingSequence) == long.MaxValue)
                {
                    throw new InvalidOperationException(
                        "The Buffer mapping sequence domain is exhausted.");
                }
                ulong sequence = checked((ulong)Interlocked.Increment(ref _mappingSequence));
                NativeRange readRange = type == MapType.Write
                    ? default
                    : new NativeRange
                    {
                        Begin = checked((nuint)range.Offset),
                        End = checked((nuint)(range.Offset + range.Size)),
                    };
                void* basePointer = null;
                NativeCall.ThrowIfFailed(
                    Native->Map(0, &readRange, &basePointer),
                    "ID3D12Resource::Map");
                _native.Retain();
                D3D12MappingLease lease = new(this, type, range, sequence);
                MappedBuffer result = new(
                    lease,
                    (nint)((byte*)basePointer + range.Offset),
                    checked((int)range.Size));
                Volatile.Write(ref _mapping, lease);
                return result;
            }
            catch
            {
                Volatile.Write(ref _mapped, 0);
                throw;
            }
        }

        internal void EndMapping(
            D3D12MappingLease mapping,
            MapType type,
            in BufferRange range)
        {
            NativeRange writtenRange = type == MapType.Read
                ? default
                : new NativeRange
                {
                    Begin = checked((nuint)range.Offset),
                    End = checked((nuint)(range.Offset + range.Size)),
                };
            Native->Unmap(0, &writtenRange);
            _ = Interlocked.CompareExchange(ref _mapping, null, mapping);
            Volatile.Write(ref _mapped, 0);
            _native.Release();
        }

        internal override void Release(bool fromParent)
        {
            if (Interlocked.Exchange(ref _released, 1) != 0)
                return;
            Volatile.Read(ref _mapping)?.Dispose();
            _views.DisposeAll();
            SparseState?.Dispose();
            _native.Release();
            _heap?.UnregisterResource(this);
            _device.UnregisterChild(this);
        }
    }

    private sealed partial class D3D12Texture : Texture
    {
        private readonly D3D12TextureResource _resource;

        internal D3D12Texture(
            D3D12Device device,
            D3D12Heap? heap,
            NativeResource* native,
            TextureInfo info,
            string? label,
            PipelineSync initialSync = PipelineSync.None,
            ResourceAccess initialAccess = ResourceAccess.NoAccess,
            SomeEngine.Graphics.TextureLayout initialLayout = SomeEngine.Graphics.TextureLayout.Undefined,
            QueueType? initialQueueType = null,
            bool ownsReference = true)
            : base(
                device,
                heap,
                info,
                initialSync,
                initialAccess,
                initialLayout,
                label,
                initialQueueType)
        {
            _resource = new D3D12TextureResource(
                this,
                device,
                heap,
                native,
                ownsReference);
        }

        internal D3D12TextureResource NativeResource => _resource;
        internal NativeResource* Native => _resource.Native;
        internal NativeLease NativeLifetime => _resource.NativeLifetime;
        internal D3D12SparseState? SparseState
        {
            get => _resource.SparseState;
            set => _resource.SparseState = value;
        }
        internal void RegisterView(GraphicsObject view) => _resource.RegisterView(view);
        internal void UnregisterView(GraphicsObject view) => _resource.UnregisterView(view);

        internal override void Release(bool fromParent) => _resource.Release();
    }

    private sealed partial class D3D12TextureResource
    {
        private readonly D3D12Device _device;
        private readonly D3D12Heap? _heap;
        private readonly ChildRegistry _views = new();
        private readonly NativeLease _native;
        private int _released;

        internal D3D12TextureResource(
            Texture owner,
            D3D12Device device,
            D3D12Heap? heap,
            NativeResource* native,
            bool ownsReference = true,
            NativeLease? dependency = null)
        {
            Owner = owner;
            _device = device;
            _heap = heap;
            _native = new NativeLease(
                (IUnknown*)native,
                ownsReference,
                dependency ?? heap?.NativeLifetime);
        }

        internal Texture Owner { get; }
        internal D3D12Device Device => _device;
        internal TextureInfo Info => Owner.Info;
        internal NativeResource* Native => (NativeResource*)_native.Pointer;
        internal NativeLease NativeLifetime => _native;
        internal D3D12SparseState? SparseState { get; set; }
        internal void RegisterView(GraphicsObject view) => _views.Register(Owner, view);
        internal void UnregisterView(GraphicsObject view) => _views.Unregister(view);

        internal void Release()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0)
                return;
            _views.DisposeAll();
            SparseState?.Dispose();
            _native.Release();
            _heap?.UnregisterResource(Owner);
            _device.UnregisterChild(Owner);
        }
    }

    private sealed class D3D12MappingLease : MappingLease
    {
        private readonly D3D12Buffer _buffer;

        internal D3D12MappingLease(
            D3D12Buffer buffer,
            MapType type,
            in BufferRange range,
            ulong sequence)
            : base(buffer, type, range, sequence)
        {
            _buffer = buffer;
        }

        protected override void FlushCore(in BufferRange range)
        {
        }

        protected override void InvalidateCore(in BufferRange range)
        {
        }

        protected override void UnmapCore() => _buffer.EndMapping(this, Type, Range);
    }

    private sealed class ChildRegistry
    {
        private readonly object _gate = new();
        private readonly HashSet<GraphicsObject> _children =
            new(ReferenceEqualityComparer.Instance);

        internal void Register(GraphicsObject parent, GraphicsObject child)
        {
            lock (_gate)
            {
                parent.ThrowIfDisposed();
                _children.Add(child);
            }
        }

        internal void Unregister(GraphicsObject child)
        {
            lock (_gate)
                _children.Remove(child);
        }

        internal void DisposeAll()
        {
            GraphicsObject[] children;
            lock (_gate)
                children = [.. _children];
            foreach (GraphicsObject child in children)
                child.DisposeFromParent();
            lock (_gate)
                _children.Clear();
        }
    }

    private sealed class NativeLease
    {
        private nint _pointer;
        private readonly bool _ownsReference;
        private NativeLease? _dependency;
        private int _references = 1;

        internal NativeLease(
            IUnknown* pointer,
            bool ownsReference,
            NativeLease? dependency = null)
        {
            _pointer = (nint)pointer;
            _ownsReference = ownsReference;
            _dependency = dependency;
            dependency?.Retain();
        }

        internal nint Pointer => Volatile.Read(ref _pointer);

        internal void Retain()
        {
            int current = Volatile.Read(ref _references);
            while (current > 0)
            {
                int exchanged = Interlocked.CompareExchange(
                    ref _references,
                    checked(current + 1),
                    current);
                if (exchanged == current)
                    return;
                current = exchanged;
            }
            throw new ObjectDisposedException(nameof(NativeLease));
        }

        internal void Release()
        {
            if (Interlocked.Decrement(ref _references) != 0)
                return;
            nint pointer = Interlocked.Exchange(ref _pointer, 0);
            if (_ownsReference && pointer != 0)
                _ = ((IUnknown*)pointer)->Release();
            Interlocked.Exchange(ref _dependency, null)?.Release();
        }
    }

    private static partial class NativeCast
    {
        internal static D3D12Heap Heap(Heap value)
        {
#if DEBUG
            return (D3D12Heap)value;
#else
            return System.Runtime.CompilerServices.Unsafe.As<Heap, D3D12Heap>(ref value);
#endif
        }

        internal static D3D12Buffer Buffer(Buffer value)
        {
#if DEBUG
            return (D3D12Buffer)value;
#else
            return System.Runtime.CompilerServices.Unsafe.As<Buffer, D3D12Buffer>(ref value);
#endif
        }

        internal static D3D12TextureResource Texture(Texture value)
        {
#if DEBUG
            return value switch
            {
                D3D12Texture texture => texture.NativeResource,
                D3D12SamplerFeedbackTexture feedback => feedback.NativeResource,
                _ => throw new InvalidCastException("The Texture does not belong to this D3D12 backend."),
            };
#else
            if ((value.Info.Usages & TextureUsages.SamplerFeedback) != 0)
            {
                return System.Runtime.CompilerServices.Unsafe
                    .As<Texture, D3D12SamplerFeedbackTexture>(ref value)
                    .NativeResource;
            }
            return System.Runtime.CompilerServices.Unsafe
                .As<Texture, D3D12Texture>(ref value)
                .NativeResource;
#endif
        }
    }
}
