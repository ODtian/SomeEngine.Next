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

internal sealed unsafe partial class D3D12Backend
{
    public MemoryRequirements GetBufferMemoryRequirements(
        Device device,
        in BufferDesc desc,
        MemoryType memoryType = MemoryType.DeviceLocal)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(RhiTrace.RhiCommandsAndOther);
        D3D12Device nativeDevice = RequireDevice(device, nameof(device));
        nativeDevice.ThrowIfUnavailable();
        NativeResourceDesc native = CreateBufferDescription(desc);

        (_, uint visibleNodeMask) = nativeDevice.ResolveResourcePlacement(
            desc.NodePlacement,
            nameof(desc));
        ResourceAllocationInfo allocation = DeviceQueryTrace.GetResourceAllocationInfo(
nativeDevice.Native, visibleNodeMask,
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
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(RhiTrace.RhiCommandsAndOther);
        D3D12Device nativeDevice = RequireDevice(device, nameof(device));
        nativeDevice.ThrowIfUnavailable();
        NativeResourceDesc native = CreateTextureDescription(desc);
        (_, uint visibleNodeMask) = nativeDevice.ResolveResourcePlacement(
            desc.NodePlacement,
            nameof(desc));
        ResourceAllocationInfo allocation = DeviceQueryTrace.GetResourceAllocationInfo(
nativeDevice.Native, visibleNodeMask,
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
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(RhiTrace.RhiCommandsAndOther);
        D3D12Device nativeDevice = RequireDevice(device, nameof(device));
        nativeDevice.ThrowIfUnavailable();

        NativeResourceDesc native = CreateTextureDescription(desc);
        (uint creationNodeMask, uint visibleNodeMask) = nativeDevice.ResolveResourcePlacement(
            desc.NodePlacement,
            nameof(desc));
        uint plane = FormatMappings.PlaneIndex(desc.Format, copy.Aspect);

        PlacedSubresourceFootprint footprint = GetNativeCopyFootprint(
            nativeDevice,
            native,
            plane,
            copy.Width,
            copy.Height,
            copy.Depth,
            requestedBufferOffset,
            out uint rows,
            out ulong rowSize,
            out ulong totalSize);
        return new TextureCopyFootprint(
            footprint.Offset,
            footprint.Footprint.RowPitch,
            rows,
            rowSize,
            totalSize)
        {
            BlockHeight = FormatMappings.IsBlockCompressed(desc.Format) ? 4u : 1u,
        };
    }

    private static PlacedSubresourceFootprint GetNativeCopyFootprint(
        D3D12Device device,
        in NativeResourceDesc description,
        uint plane,
        uint width,
        uint height,
        uint depth,
        ulong baseOffset,
        out uint rowCount,
        out ulong rowSize,
        out ulong totalSize)
    {
        NativeResourceDesc native = description;
        native.Width = width;
        native.Height = native.Dimension == NativeResourceDimension.Texture1D ? 1u : height;
        native.DepthOrArraySize = checked((ushort)(
            native.Dimension == NativeResourceDimension.Texture3D ? depth : 1u));
        native.MipLevels = 1;
        PlacedSubresourceFootprint footprint = default;
        uint nativeRowCount = 0;
        ulong nativeRowSize = 0;
        ulong nativeTotalSize = 0;
        DeviceQueryTrace.GetCopyableFootprints(
                device.Native, &native,
                    plane,
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
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(RhiTrace.RhiResourceCreation);
        D3D12Device nativeDevice = RequireDevice(device, nameof(device));
        nativeDevice.ThrowIfUnavailable();

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
        // Resource contents are undefined until the caller initializes them.
        // Avoid duplicating that work when allocating private heap storage.
        if ((desc.Flags & SomeEngine.Graphics.HeapFlags.Shareable) == 0)
            nativeDescription.Flags |= NativeHeapFlags.CreateNotZeroed;

        ID3D12Heap* native = null;
        Guid iid = ID3D12Heap.Guid;
        ThrowIfFailed(
            nativeDevice,
                DeviceResourceTrace.CreateHeap(nativeDevice.Native, &nativeDescription, &iid, (void**)&native),
            NativeOperationType.Ordinary,
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
            _ = ResourceTrace.Release(native);
            throw;
        }
        SetNativeName(heap.Native, desc.Label ?? "D3D12 Heap");
        nativeDevice.RegisterChild(heap);
        return heap;
    }

    public Buffer CreateBuffer(
        Device device,
        in BufferDesc desc,
        MemoryType memoryType = MemoryType.DeviceLocal)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(RhiTrace.RhiResourceCreation);
        D3D12Device nativeDevice = RequireDevice(device, nameof(device));
        nativeDevice.ThrowIfUnavailable();
        NativeResourceDesc nativeDescription = CreateBufferDescription(desc);

        (uint creationNodeMask, uint visibleNodeMask) = nativeDevice.ResolveResourcePlacement(
            desc.NodePlacement,
            nameof(desc));
        MemoryRequirements requirements = GetBufferMemoryRequirements(device, desc, memoryType);
        (PipelineSync sync, ResourceAccess access) = InitialBufferAccess(memoryType);
        BufferInfo info = new(
            desc.Size,
            desc.Usages,
            memoryType,
            0,
            requirements.Size,
            creationNodeMask,
            visibleNodeMask);
        bool shareable = (desc.Usages & BufferUsages.Shareable) != 0;
        NativeResourceData? pooled = TryCreatePooledResource(
            nativeDevice,
            memoryType,
            ResourceHeapClass.Buffers,
            poolEligible: !shareable,
            creationNodeMask,
            visibleNodeMask,
            requirements,
            nativeDescription,
            ReadOnlySpan<DxgiFormat>.Empty);
        D3D12Buffer buffer;
        if (pooled is not null)
        {
            try
            {
                buffer = new D3D12Buffer(
                    nativeDevice,
                    pooled.Value,
                    info,
                    sync,
                    access,
                    desc.Label);
            }
            catch
            {
                pooled.Value.Release();
                throw;
            }
        }
        else
        {
            NativeResource* native = CreateCommittedResource(
                nativeDevice,
                memoryType,
                shareable,
                creationNodeMask,
                visibleNodeMask,
                nativeDescription,
                ReadOnlySpan<DxgiFormat>.Empty);
            try
            {
                buffer = new D3D12Buffer(
                    nativeDevice,
                    heap: null,
                    native,
                    info,
                    sync,
                    access,
                    desc.Label);
            }
            catch
            {
                _ = ResourceTrace.Release(native);
                throw;
            }
        }
        SetNativeName(buffer.Native, desc.Label ?? $"{memoryType} Buffer");
        nativeDevice.RegisterChild(buffer);
        return buffer;
    }

    public Buffer CreatePlacedBuffer(
        Device device,
        Heap heap,
        ulong offset,
        in BufferDesc desc)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(RhiTrace.RhiResourceCreation);
        D3D12Device nativeDevice = RequireDevice(device, nameof(device));
        D3D12Heap nativeHeap = RequireHeap(heap);
        nativeDevice.ThrowIfUnavailable();

        NativeResourceDesc nativeDescription = CreateBufferDescription(desc);

        ResourceAllocationInfo allocation = DeviceQueryTrace.GetResourceAllocationInfo(
nativeDevice.Native, nativeHeap.Info.VisibleNodeMask,
            1,
            &nativeDescription);
        EnsureAllocationInfo(allocation, "Buffer");
        MemoryRequirements requirements = new(
            allocation.SizeInBytes,
            allocation.Alignment,
            SomeEngine.Graphics.HeapFlags.Buffers |
            ((desc.Usages & BufferUsages.Shareable) != 0
                ? SomeEngine.Graphics.HeapFlags.Shareable
                : SomeEngine.Graphics.HeapFlags.None));

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
                    requirements.Size,
                    nativeHeap.Info.CreationNodeMask,
                    nativeHeap.Info.VisibleNodeMask),
                sync,
                access,
                desc.Label);
        }
        catch
        {
            _ = ResourceTrace.Release(native);
            throw;
        }
        SetNativeName(buffer.Native, desc.Label ?? "Placed Buffer");
        nativeDevice.RegisterChild(buffer);
        return buffer;
    }

    public Texture CreateTexture(Device device, in TextureDesc desc)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(RhiTrace.RhiResourceCreation);
        D3D12Device nativeDevice = RequireDevice(device, nameof(device));
        nativeDevice.ThrowIfUnavailable();
        NativeResourceDesc nativeDescription = CreateTextureDescription(desc);
        (uint creationNodeMask, uint visibleNodeMask) = nativeDevice.ResolveResourcePlacement(
            desc.NodePlacement,
            nameof(desc));
        MemoryRequirements requirements = GetTextureMemoryRequirements(device, desc);
        TextureInfo info = CreateTextureInfo(
            desc,
            0,
            requirements.Size,
            creationNodeMask,
            visibleNodeMask);
        DxgiFormat[] castableFormats = CreateCastableFormats(desc);
        bool shareable = (desc.Usages & TextureUsages.Shareable) != 0;
        ResourceHeapClass heapClass =
            (desc.Usages & (TextureUsages.ColorAttachment |
                            TextureUsages.DepthStencilAttachment)) != 0
                ? ResourceHeapClass.Attachments
                : ResourceHeapClass.Textures;
        NativeResourceData? pooled = TryCreatePooledResource(
            nativeDevice,
            MemoryType.DeviceLocal,
            heapClass,
            poolEligible: !shareable,
            creationNodeMask,
            visibleNodeMask,
            requirements,
            nativeDescription,
            castableFormats);
        D3D12Texture texture;
        if (pooled is not null)
        {
            try
            {
                texture = new D3D12Texture(
                    nativeDevice,
                    pooled.Value,
                    info,
                    desc.Label);
            }
            catch
            {
                pooled.Value.Release();
                throw;
            }
        }
        else
        {
            NativeResource* native = CreateCommittedResource(
                nativeDevice,
                MemoryType.DeviceLocal,
                shareable,
                creationNodeMask,
                visibleNodeMask,
                nativeDescription,
                castableFormats);
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
                _ = ResourceTrace.Release(native);
                throw;
            }
        }
        SetNativeName(texture.Native, desc.Label ?? "DeviceLocal Texture");
        nativeDevice.RegisterChild(texture);
        return texture;
    }

    public Texture CreatePlacedTexture(
        Device device,
        Heap heap,
        ulong offset,
        in TextureDesc desc)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(RhiTrace.RhiResourceCreation);
        D3D12Device nativeDevice = RequireDevice(device, nameof(device));
        D3D12Heap nativeHeap = RequireHeap(heap);
        nativeDevice.ThrowIfUnavailable();

        NativeResourceDesc nativeDescription = CreateTextureDescription(desc);

        ResourceAllocationInfo allocation = DeviceQueryTrace.GetResourceAllocationInfo(
nativeDevice.Native, nativeHeap.Info.VisibleNodeMask,
            1,
            &nativeDescription);
        EnsureAllocationInfo(allocation, "Texture");
        SomeEngine.Graphics.HeapFlags compatibleFlags = SomeEngine.Graphics.HeapFlags.Textures;
        if ((desc.Usages & (TextureUsages.ColorAttachment |
                           TextureUsages.DepthStencilAttachment)) != 0)
            compatibleFlags |= SomeEngine.Graphics.HeapFlags.Attachments;
        if ((desc.Usages & TextureUsages.Shareable) != 0)
            compatibleFlags |= SomeEngine.Graphics.HeapFlags.Shareable;
        MemoryRequirements requirements = new(
            allocation.SizeInBytes,
            allocation.Alignment,
            compatibleFlags);
        SomeEngine.Graphics.HeapFlags requiredClass =
            (desc.Usages & (TextureUsages.ColorAttachment |
                            TextureUsages.DepthStencilAttachment)) != 0
                ? SomeEngine.Graphics.HeapFlags.Attachments
                : SomeEngine.Graphics.HeapFlags.Textures;

        TextureInfo info = CreateTextureInfo(
            desc,
            offset,
            requirements.Size,
            nativeHeap.Info.CreationNodeMask,
            nativeHeap.Info.VisibleNodeMask);
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
            _ = ResourceTrace.Release(native);
            throw;
        }
        SetNativeName(texture.Native, desc.Label ?? "Placed Texture");
        nativeDevice.RegisterChild(texture);
        return texture;
    }

    public MappedBuffer Map(Buffer buffer, MapType type, in BufferRange range)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(RhiTrace.RhiCommandsAndOther);
        D3D12Buffer nativeBuffer = RequireBuffer(buffer);
        BufferRange resolved = range.Expand(nativeBuffer.Info.Size);
        NativeMemoryChecks.Range(nativeBuffer.Info.Size, resolved.Offset, resolved.Size);

        return nativeBuffer.Map(type, resolved, NativeMemoryChecks.SpanLength(resolved.Size));
    }

    private static NativeResource* CreateCommittedResource(
        D3D12Device device,
        MemoryType memoryType,
        bool shareable,
        uint creationNodeMask,
        uint visibleNodeMask,
        in NativeResourceDesc description,
        ReadOnlySpan<DxgiFormat> castableFormats)
    {
        HeapProperties properties = CreateHeapProperties(
            memoryType,
            creationNodeMask,
            visibleNodeMask);
        NativeHeapFlags heapFlags = shareable ? NativeHeapFlags.Shared : NativeHeapFlags.CreateNotZeroed;
        NativeResource* resource = null;
        Guid iid = NativeResource.Guid;

        if (device.EnhancedBarriers)
        {
            ResourceDesc1 description1 = ToDescription1(description);
            BarrierLayout layout = InitialLayout(memoryType, description.Dimension);
            fixed (DxgiFormat* formats = castableFormats)
            {
                ThrowIfFailed(
                    device,
                        DeviceResourceTrace.CreateCommittedResource3(
                    device.Native, &properties,
                        heapFlags,
                        &description1,
                        layout,
                        null,
                        null,
                        (uint)castableFormats.Length,
                        formats,
                        &iid,
                        (void**)&resource),
                    NativeOperationType.Ordinary,
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
            int createResult = DeviceResourceTrace.CreateCommittedResource(
device.Native, &properties,
                heapFlags,
                &copy,
                initialState,
                null,
                &iid,
                (void**)&resource);
            ThrowIfFailed(
                device,
                createResult,
                NativeOperationType.Ordinary,
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
        => CreatePlacedResource(
            device,
            heap.Native,
            offset,
            heap.Info.MemoryType,
            description,
            castableFormats);

    private static NativeResource* CreatePlacedResource(
        D3D12Device device,
        ID3D12Heap* heap,
        ulong offset,
        MemoryType memoryType,
        in NativeResourceDesc description,
        ReadOnlySpan<DxgiFormat> castableFormats)
    {
        NativeResource* resource = null;
        Guid iid = NativeResource.Guid;

        if (device.EnhancedBarriers)
        {
            ResourceDesc1 description1 = ToDescription1(description);
            BarrierLayout layout = InitialLayout(memoryType, description.Dimension);
            fixed (DxgiFormat* formats = castableFormats)
            {
                ThrowIfFailed(
                    device,
                        DeviceResourceTrace.CreatePlacedResource2(
                    device.Native, heap,
                        offset,
                        &description1,
                        layout,
                        null,
                        (uint)castableFormats.Length,
                        formats,
                        &iid,
                        (void**)&resource),
                    NativeOperationType.Ordinary,
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
                : InitialLegacyState(memoryType);
            int createResult = DeviceResourceTrace.CreatePlacedResource(
device.Native, heap,
                offset,
                &copy,
                initialState,
                null,
                &iid,
                (void**)&resource);
            ThrowIfFailed(
                device,
                createResult,
                NativeOperationType.Ordinary,
                "ID3D12Device::CreatePlacedResource");
        }

        return resource;
    }

    private static NativeResourceDesc CreateBufferDescription(in BufferDesc desc)
    {
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

        NativeResourceDimension dimension = desc.Dimension switch
        {
            TextureDimension.Texture1D => NativeResourceDimension.Texture1D,
            TextureDimension.Texture2D => NativeResourceDimension.Texture2D,
            TextureDimension.Texture3D => NativeResourceDimension.Texture3D,
            _ => default,
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
                _ => default,
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
            _ => default,
        };
    }

    private static ResourceStates InitialLegacyState(MemoryType memoryType) => memoryType switch
    {
        MemoryType.DeviceLocal => ResourceStates.Common,
        MemoryType.Upload => ResourceStates.GenericRead,
        MemoryType.Readback => ResourceStates.CopyDest,
        _ => default,
    };

    private static (PipelineSync Sync, ResourceAccess Access) InitialBufferAccess(
        MemoryType memoryType) =>
        memoryType switch
        {
            MemoryType.DeviceLocal => (PipelineSync.None, ResourceAccess.NoAccess),
            MemoryType.Upload => (PipelineSync.None, ResourceAccess.NoAccess),
            MemoryType.Readback => (PipelineSync.Copy, ResourceAccess.CopyDestination),
            _ => default,
        };

    private static TextureInfo CreateTextureInfo(
        in TextureDesc desc,
        ulong allocationOffset,
        ulong allocationSize,
        uint creationNodeMask,
        uint visibleNodeMask) =>
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
            allocationSize,
            creationNodeMask,
            visibleNodeMask);

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
        private readonly NativeResourceData _native;

        internal D3D12Heap(
            D3D12Device device,
            ID3D12Heap* native,
            in HeapInfo info,
            string? label,
            bool ownsReference = true)
            : base(device, info, label)
        {
            _device = device;
            _native = new NativeResourceData((IUnknown*)native, ownsReference);
        }

        internal ID3D12Heap* Native => (ID3D12Heap*)_native.Pointer;
        internal NativeResourceData NativeData => _native;
        internal D3D12Device NativeDevice => _device;

        internal override void Release(bool fromParent)
        {
            _native.Release();
            _device.UnregisterChild(this);
        }
    }

    private sealed class D3D12Buffer : Buffer
    {
        private readonly Lock _constantBufferGate = new();
        private Dictionary<BufferRange, DescriptorAllocation>? _constantBufferDescriptors;

        internal DescriptorAllocation GetConstantBufferDescriptor(in BufferRange requested)
        {
            BufferRange range = requested.IsWhole ? new BufferRange(0, Info.Size) : requested;
            lock (_constantBufferGate)
            {
                if (_constantBufferDescriptors is { } descriptors &&
                    descriptors.TryGetValue(range, out DescriptorAllocation? existing))
                    return existing;
                DescriptorAllocation descriptor = _device.GetResourceDescriptors(
                    _device.ResolveResourceHomeNodeIndex(Info.CreationNodeMask)).Allocate();
                try
                {
                    WriteBufferCbv(_device, this, new BufferCbvDesc(this, range), descriptor.Cpu);
                    (_constantBufferDescriptors ??= []).Add(range, descriptor);
                    return descriptor;
                }
                catch
                {
                    descriptor.Release();
                    throw;
                }
            }
        }

        private void ReleaseConstantBufferDescriptors()
        {
            lock (_constantBufferGate)
            {
                if (_constantBufferDescriptors is null) return;
                foreach (DescriptorAllocation descriptor in _constantBufferDescriptors.Values) descriptor.Release();
                _constantBufferDescriptors.Clear();
            }
        }

        private readonly D3D12Device _device;
        private readonly NativeResourceData _native;
        private D3D12SparseState? _sparseState;
        private readonly D3D12MappingHandle _mapping;
        internal override MappingHandle? Mapping => _mapping;

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
            _mapping = new D3D12MappingHandle(this);
            _native = new NativeResourceData(
                (IUnknown*)native,
                ownsReference,
                heap?.NativeData);
        }

        internal D3D12Buffer(
            D3D12Device device,
            NativeResourceData native,
            in BufferInfo info,
            PipelineSync initialSync,
            ResourceAccess initialAccess,
            string? label)
            : base(device, heap: null, info, initialSync, initialAccess, label)
        {
            _device = device;
            _mapping = new D3D12MappingHandle(this);
            _native = native;
        }

        internal NativeResource* Native => (NativeResource*)_native.Pointer;
        internal NativeResourceData NativeData => _native;
        internal D3D12MemoryAllocation? MemoryAllocation => _native.MemoryAllocation;
        internal D3D12SparseState? SparseState
        {
            get => _sparseState;
            set => _sparseState = value;
        }

        internal MappedBuffer Map(MapType type, BufferRange range, int length)
        {

            _device.ThrowIfUnavailable();

            ulong rangeEnd = checked(range.Offset + range.Size);
            NativeRange completeRange = new()
            {
                Begin = checked((nuint)range.Offset),
                End = checked((nuint)rangeEnd),
            };
            NativeRange readRange;
            NativeRange writtenRange;
            switch (type)
            {
                case MapType.Read:
                    readRange = completeRange;
                    writtenRange = default;
                    break;
                case MapType.Write:
                    readRange = default;
                    writtenRange = completeRange;
                    break;
                case MapType.ReadWrite:
                    readRange = completeRange;
                    writtenRange = completeRange;
                    break;
                default:
                    readRange = default;
                    writtenRange = default;
                    break;
            }

            ulong sequence = _mapping.PrepareNextSequence();
            _native.Retain();
            try
            {
                _mapping.Prepare(writtenRange);
            }
            catch
            {
                _native.Release();
                throw;
            }

            bool nativeMapAccepted = false;
            try
            {
                void* basePointer = null;
                ThrowIfFailed(
                    _device,
                        ResourceTrace.Map(Native, 0, &readRange, &basePointer),
                    NativeOperationType.Ordinary,
                    "ID3D12Resource::Map");
                nativeMapAccepted = true;
                MappedBuffer result = new(
                    _mapping,
                    (nint)((byte*)basePointer + range.Offset),
                    length,
                    sequence);
                _mapping.PublishMapping(sequence, range);
                return result;
            }
            catch
            {
                if (nativeMapAccepted)
                {
                    NativeRange noWrites = default;
                    ResourceTrace.Unmap(Native, 0, &noWrites);
                }
                _native.Release();
                throw;
            }
        }

        internal void EndMapping(in NativeRange writtenRange)
        {
            NativeRange nativeWrittenRange = writtenRange;
            ResourceTrace.Unmap(Native, 0, &nativeWrittenRange);
            _native.Release();
        }

        internal override void Release(bool fromParent)
        {
            _mapping.DisposeCurrent();
            _sparseState?.Dispose();
            ReleaseConstantBufferDescriptors();
            _native.Release();
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

        internal D3D12Texture(
            D3D12Device device,
            NativeResourceData native,
            TextureInfo info,
            string? label)
            : base(
                device,
                heap: null,
                info,
                PipelineSync.None,
                ResourceAccess.NoAccess,
                SomeEngine.Graphics.TextureLayout.Undefined,
                label)
        {
            _resource = new D3D12TextureResource(this, device, native);
        }

        internal D3D12TextureResource NativeResource => _resource;
        internal NativeResource* Native => _resource.Native;
        internal NativeResourceData NativeData => _resource.NativeData;
        internal D3D12SparseState? SparseState
        {
            get => _resource.SparseState;
            set => _resource.SparseState = value;
        }
        internal override void Release(bool fromParent) => _resource.Release();
    }

    private sealed partial class D3D12TextureResource
    {
        private readonly D3D12Device _device;
        private readonly NativeResourceData _native;
        private D3D12SparseState? _sparseState;

        internal D3D12TextureResource(
            Texture texture,
            D3D12Device device,
            D3D12Heap? heap,
            NativeResource* native,
            bool ownsReference = true,
            NativeResourceData? dependency = null)
        {
            Texture = texture;
            _device = device;
            _native = new NativeResourceData(
                (IUnknown*)native,
                ownsReference,
                dependency ?? heap?.NativeData);
        }

        internal D3D12TextureResource(
            Texture texture,
            D3D12Device device,
            NativeResourceData native)
        {
            Texture = texture;
            _device = device;
            _native = native;
        }

        internal Texture Texture { get; }
        internal D3D12Device Device => _device;
        internal TextureInfo Info => Texture.Info;
        internal NativeResource* Native => (NativeResource*)_native.Pointer;
        internal NativeResourceData NativeData => _native;
        internal D3D12MemoryAllocation? MemoryAllocation => _native.MemoryAllocation;
        internal D3D12SparseState? SparseState
        {
            get => _sparseState;
            set => _sparseState = value;
        }

        internal void Release()
        {
            _sparseState?.Dispose();
            _native.Release();
            _device.UnregisterChild(Texture);
        }
    }

    private sealed class D3D12MappingHandle : MappingHandle
    {
        private readonly D3D12Buffer _buffer;
        private NativeRange _writtenRange;

        internal D3D12MappingHandle(D3D12Buffer buffer)
            : base(buffer)
        {
            _buffer = buffer;
        }

        internal void Prepare(in NativeRange writtenRange) =>
            _writtenRange = writtenRange;

        internal void PublishMapping(ulong sequence, in BufferRange range) =>
            base.Publish(sequence, range);

        protected override void FlushCore(in BufferRange range)
        {
        }

        protected override void InvalidateCore(in BufferRange range)
        {
        }

        protected override void UnmapCore() => _buffer.EndMapping(_writtenRange);
    }

    private readonly struct NativeResourceData : IEquatable<NativeResourceData>
    {
        private readonly bool _ownsReference;
        private readonly NativeResourceData[] _dependencies;
        private readonly D3D12MemoryAllocation? _allocation;
        internal nint Pointer { get; }
        internal D3D12MemoryAllocation? MemoryAllocation => _allocation;

        internal NativeResourceData(IUnknown* pointer, bool ownsReference,
            NativeResourceData? dependency = null, D3D12MemoryAllocation? allocation = null)
            : this(pointer, ownsReference, dependency is { } value ? [value] : [], allocation) { }

        internal NativeResourceData(IUnknown* pointer, bool ownsReference, NativeResourceData[] dependencies)
            : this(pointer, ownsReference, dependencies, null) { }

        private NativeResourceData(IUnknown* pointer, bool ownsReference,
            NativeResourceData[] dependencies, D3D12MemoryAllocation? allocation)
        {
            Pointer = (nint)pointer;
            _ownsReference = ownsReference;
            _dependencies = dependencies;
            _allocation = allocation;
            foreach (NativeResourceData dependency in dependencies) dependency.Retain();
        }

        public bool Equals(NativeResourceData other) => Pointer == other.Pointer;
        public override bool Equals(object? value) => value is NativeResourceData other && Equals(other);
        public override int GetHashCode() => Pointer.GetHashCode();
        public static bool operator ==(NativeResourceData left, NativeResourceData right) => left.Equals(right);
        public static bool operator !=(NativeResourceData left, NativeResourceData right) => !left.Equals(right);

        internal void Retain()
        {
            if (_ownsReference && Pointer != 0) _ = ((IUnknown*)Pointer)->AddRef();
            _allocation?.Retain();
            foreach (NativeResourceData dependency in _dependencies) dependency.Retain();
        }

        internal void Release()
        {
            if (_ownsReference && Pointer != 0) _ = ResourceTrace.Release((IUnknown*)Pointer);
            _allocation?.Release();
            foreach (NativeResourceData dependency in _dependencies) dependency.Release();
        }
    }

    private static partial class RequireD3D12
    {
        internal static D3D12Heap Heap(Heap value) =>(D3D12Heap)value;

        internal static D3D12Buffer Buffer(Buffer value) =>(D3D12Buffer)value;

        internal static D3D12TextureResource Texture(Texture value) =>value is D3D12SamplerFeedbackTexture feedback ? feedback.NativeResource : ((D3D12Texture)value).NativeResource;
    }
}
