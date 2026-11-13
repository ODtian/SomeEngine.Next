using Silk.NET.Direct3D12;
using NativeResourceDesc = Silk.NET.Direct3D12.ResourceDesc;

namespace SomeEngine.Graphics.Direct3D12;
internal sealed unsafe partial class D3D12Backend
{
    public void CopyBuffer(CommandContext context, in BufferCopy copy)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(RhiTrace.RhiCommandsAndOther);
        D3D12CommandContext command = ((D3D12CommandContext)context);
        command.BeginPublicCall();
        D3D12Buffer source = RequireBuffer(copy.Source);
        D3D12Buffer destination = RequireBuffer(copy.Destination);
        CommandTransferTrace.CopyBufferRegion(command, command.List, destination.Native, copy.DestinationOffset, source.Native, copy.SourceOffset, copy.Size);
    }

    public void CopyBufferToTexture(CommandContext context, in BufferTextureCopy copy)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(RhiTrace.RhiCommandsAndOther);
        D3D12CommandContext command = ((D3D12CommandContext)context);
        command.BeginPublicCall();
        D3D12Buffer source = RequireBuffer(copy.Buffer);
        D3D12TextureResource destination = RequireTexture(copy.Texture);
        TextureCopyLocation bufferLocation = CreateBufferCopyLocation(command.NativeDevice, source, destination, copy);
        TextureCopyLocation textureLocation = CreateTextureCopyLocation(destination, copy.MipLevel, copy.ArrayLayer, copy.Aspect);
        Box sourceBox = new(0, 0, 0, copy.Width, copy.Height, copy.Depth);
        command.PrepareSwapchainUses(1);
        command.RecordSwapchainImage(destination);
        CommandTransferTrace.CopyTextureRegion(command, command.List, &textureLocation, copy.X, copy.Y, copy.Z, &bufferLocation, &sourceBox);
    }

    public void CopyTextureToBuffer(CommandContext context, in BufferTextureCopy copy)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(RhiTrace.RhiCommandsAndOther);
        D3D12CommandContext command = ((D3D12CommandContext)context);
        command.BeginPublicCall();
        D3D12Buffer destination = RequireBuffer(copy.Buffer);
        D3D12TextureResource source = RequireTexture(copy.Texture);
        TextureCopyLocation bufferLocation = CreateBufferCopyLocation(command.NativeDevice, destination, source, copy);
        TextureCopyLocation textureLocation = CreateTextureCopyLocation(source, copy.MipLevel, copy.ArrayLayer, copy.Aspect);
        Box sourceBox = new(copy.X, copy.Y, copy.Z, checked(copy.X + copy.Width), checked(copy.Y + copy.Height), checked(copy.Z + copy.Depth));
        command.PrepareSwapchainUses(1);
        command.RecordSwapchainImage(source);
        CommandTransferTrace.CopyTextureRegion(command, command.List, &bufferLocation, 0, 0, 0, &textureLocation, &sourceBox);
    }

    public void CopyTexture(CommandContext context, in TextureCopy copy)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(RhiTrace.RhiCommandsAndOther);
        D3D12CommandContext command = ((D3D12CommandContext)context);
        command.BeginPublicCall();
        D3D12TextureResource source = RequireTexture(copy.Source);
        D3D12TextureResource destination = RequireTexture(copy.Destination);
        bool entireSubresource = source.Info.SampleCount > 1 || copy.SourceAspect != TextureAspects.Color;
        TextureCopyLocation sourceLocation = CreateTextureCopyLocation(source, copy.SourceMipLevel, copy.SourceArrayLayer, copy.SourceAspect);
        TextureCopyLocation destinationLocation = CreateTextureCopyLocation(destination, copy.DestinationMipLevel, copy.DestinationArrayLayer, copy.DestinationAspect);
        Box sourceBox = new(
            copy.SourceX,
            copy.SourceY,
            copy.SourceZ,
            checked(copy.SourceX + copy.Width),
            checked(copy.SourceY + copy.Height),
            checked(copy.SourceZ + copy.Depth)
        );
        command.PrepareSwapchainUses(2);
        command.RecordSwapchainImage(source);
        command.RecordSwapchainImage(destination);
        CommandTransferTrace.CopyTextureRegion(
            command,
            command.List,
            &destinationLocation,
            copy.DestinationX,
            copy.DestinationY,
            copy.DestinationZ,
            &sourceLocation,
            entireSubresource ? null : &sourceBox
        );
    }

    public void ResolveTexture(CommandContext context, in TextureResolve resolve)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(RhiTrace.RhiCommandsAndOther);
        D3D12CommandContext command = ((D3D12CommandContext)context);
        command.BeginPublicCall();
        D3D12TextureResource source = RequireTexture(resolve.Source);
        D3D12TextureResource destination = RequireTexture(resolve.Destination);
        ResolveMode resolveMode = ToResolveMode(resolve.Type, resolve.Format);
        uint sourceSubresource = NativeSubresource(source.Info, resolve.SourceMipLevel, resolve.SourceArrayLayer, TextureAspects.Color);
        uint destinationSubresource = NativeSubresource(destination.Info, resolve.DestinationMipLevel, resolve.DestinationArrayLayer, TextureAspects.Color);
        uint sourceWidth = MipDimension(source.Info.Width, resolve.SourceMipLevel);
        uint sourceHeight = MipDimension(source.Info.Height, resolve.SourceMipLevel);
        uint destinationWidth = MipDimension(destination.Info.Width, resolve.DestinationMipLevel);
        uint destinationHeight = MipDimension(destination.Info.Height, resolve.DestinationMipLevel);
        command.PrepareSwapchainUses(2);
        command.RecordSwapchainImage(source);
        command.RecordSwapchainImage(destination);
        CommandTransferTrace.ResolveSubresourceRegion(
            command,
            command.List,
            destination.Native,
            destinationSubresource,
            0,
            0,
            source.Native,
            sourceSubresource,
            null,
            FormatMappings.ToDxgi(resolve.Format),
            resolveMode
        );
    }

    public void ClearBuffer(CommandContext context, Buffer buffer, in BufferRange range, uint value = 0)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(RhiTrace.RhiCommandsAndOther);
        D3D12CommandContext command = ((D3D12CommandContext)context);
        command.BeginPublicCall();
        D3D12Buffer destination = RequireBuffer(buffer);
        BufferRange resolved = range.Expand(destination.Info.Size);
        command.PrepareOrdinaryData(resolved.Size);
        D3D12OrdinaryDataReservation upload = command.ReserveTransientOrdinaryData(resolved.Size);
        upload.CommitPattern(value, resolved.Size);
        CommandTransferTrace.CopyBufferRegion(command, command.List, destination.Native, resolved.Offset, upload.Resource, upload.Offset, resolved.Size);
    }

    public void ClearTexture(
        CommandContext context,
        Texture texture,
        in TextureSubresourceRange range,
        in System.Numerics.Vector4 color
    )
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(RhiTrace.RhiCommandsAndOther);
        D3D12CommandContext command = ((D3D12CommandContext)context);
        command.BeginPublicCall();
        D3D12TextureResource destination = RequireTexture(texture);
        float* values = stackalloc float[4]
        {
            color.X,
            color.Y,
            color.Z,
            color.W
        };
        uint descriptorCount = checked(range.MipLevelCount * (destination.Info.Dimension == TextureDimension.Texture3D ? 1u : range.ArrayLayerCount));
        command.PrepareSwapchainUses(1);
        command.PrepareAttachmentDescriptors(descriptorCount, 0);
        command.PrepareTransientObjects(1);
        command.RecordSwapchainImage(destination);
        D3D12CpuDescriptorRange descriptors = command.AllocateTemporaryAttachmentDescriptors(DescriptorHeapType.Rtv, descriptorCount);
        uint descriptorIndex = 0;
        for (uint mip = range.FirstMipLevel; mip < range.FirstMipLevel + range.MipLevelCount; mip++)
        {
            if (destination.Info.Dimension == TextureDimension.Texture3D)
            {
                TextureSubresourceRange single = new(mip, 1, range.FirstArrayLayer, range.ArrayLayerCount, TextureAspects.Color);
                ColorAttachmentViewDesc description = new(texture, single, destination.Info.Format, TextureViewDimension.Texture3D);
                CpuDescriptorHandle descriptor = descriptors[descriptorIndex++];
                WriteColorAttachmentView(command.NativeDevice, destination, description, descriptor);
                CommandExecutionTrace.ClearRenderTargetView(command, command.List, descriptor, values, 0, null);
                continue;
            }

            uint layerEnd = destination.Info.Dimension == TextureDimension.Texture3D ? range.FirstArrayLayer + 1 : range.FirstArrayLayer + range.ArrayLayerCount;
            for (uint layer = range.FirstArrayLayer; layer < layerEnd; layer++)
            {
                TextureSubresourceRange single = new(mip, 1, layer, 1, TextureAspects.Color);
                ColorAttachmentViewDesc description = new(texture, single, destination.Info.Format, ToAttachmentDimension(destination.Info));
                CpuDescriptorHandle descriptor = descriptors[descriptorIndex++];
                WriteColorAttachmentView(command.NativeDevice, destination, description, descriptor);
                CommandExecutionTrace.ClearRenderTargetView(command, command.List, descriptor, values, 0, null);
            }
        }
    }

    public void ClearDepthStencil(
        CommandContext context,
        Texture texture,
        in TextureSubresourceRange range,
        float depth = 1,
        byte stencil = 0
    )
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(RhiTrace.RhiCommandsAndOther);
        D3D12CommandContext command = ((D3D12CommandContext)context);
        command.BeginPublicCall();
        D3D12TextureResource destination = RequireTexture(texture);
        ClearFlags flags = 0;
        if ((range.Aspects & TextureAspects.Depth) != 0)
            flags |= ClearFlags.Depth;
        if ((range.Aspects & TextureAspects.Stencil) != 0)
            flags |= ClearFlags.Stencil;
        uint descriptorCount = checked(range.MipLevelCount * (destination.Info.Dimension == TextureDimension.Texture3D ? 1u : range.ArrayLayerCount));
        command.PrepareSwapchainUses(1);
        command.PrepareAttachmentDescriptors(0, descriptorCount);
        command.PrepareTransientObjects(1);
        command.RecordSwapchainImage(destination);
        D3D12CpuDescriptorRange descriptors = command.AllocateTemporaryAttachmentDescriptors(DescriptorHeapType.Dsv, descriptorCount);
        uint descriptorIndex = 0;
        for (uint mip = range.FirstMipLevel; mip < range.FirstMipLevel + range.MipLevelCount; mip++)
        {
            uint layerEnd = destination.Info.Dimension == TextureDimension.Texture3D ? range.FirstArrayLayer + 1 : range.FirstArrayLayer + range.ArrayLayerCount;
            for (uint layer = range.FirstArrayLayer; layer < layerEnd; layer++)
            {
                TextureSubresourceRange single = new(mip, 1, layer, 1, range.Aspects);
                DepthStencilViewDesc description = new(texture, single, destination.Info.Format, ToAttachmentDimension(destination.Info));
                CpuDescriptorHandle descriptor = descriptors[descriptorIndex++];
                WriteDepthStencilView(command.NativeDevice, destination, description, descriptor);
                CommandExecutionTrace.ClearDepthStencilView(command, command.List, descriptor, flags, depth, stencil, 0, null);
            }
        }
    }

    private static TextureCopyLocation CreateBufferCopyLocation(D3D12Device device, D3D12Buffer buffer, D3D12TextureResource texture, in BufferTextureCopy copy)
    {
        NativeResourceDesc description = ResourceTrace.GetDesc(texture.Native);
        uint plane = FormatMappings.PlaneIndex(texture.Info.Format, copy.Aspect);
        PlacedSubresourceFootprint footprint = GetNativeCopyFootprint(device, description, plane, copy.Width, copy.Height, copy.Depth, copy.BufferOffset, out _, out _, out ulong totalSize);
        ulong required = checked(footprint.Offset + totalSize);
        TextureCopyLocation location = new()
        {
            PResource = buffer.Native,
            Type = TextureCopyType.PlacedFootprint,
        };
        location.PlacedFootprint = footprint;
        return location;
    }

    private static TextureCopyLocation CreateTextureCopyLocation(D3D12TextureResource texture, uint mip, uint layer, TextureAspects aspect)
    {
        TextureCopyLocation location = new()
        {
            PResource = texture.Native,
            Type = TextureCopyType.SubresourceIndex,
        };
        location.SubresourceIndex = NativeSubresource(texture.Info, mip, layer, aspect);
        return location;
    }

    private static uint NativeSubresource(TextureInfo info, uint mip, uint layer, TextureAspects aspect)
    {
        uint arrayLayer = info.Dimension == TextureDimension.Texture3D ? 0u : layer;
        uint plane = FormatMappings.PlaneIndex(info.Format, aspect);
        return checked(mip + arrayLayer * info.MipLevelCount + plane * info.MipLevelCount * info.ArrayLayerCount);
    }

    private static ResolveMode ToResolveMode(ResolveType type, Format format)
    {
        return type switch
        {
            ResolveType.Average => ResolveMode.Average,
            ResolveType.Minimum => ResolveMode.Min,
            ResolveType.Maximum => ResolveMode.Max,
            ResolveType.SampleZero => default!,
            _ => default!,
        };
    }

    private static uint MipDimension(uint value, uint mip) => Math.Max(1u, value >> checked((int)mip));
    private static TextureViewDimension ToAttachmentDimension(TextureInfo info) => info.Dimension switch
    {
        TextureDimension.Texture1D when info.ArrayLayerCount == 1 => TextureViewDimension.Texture1D,
        TextureDimension.Texture1D => TextureViewDimension.Texture1DArray,
        TextureDimension.Texture2D when info.SampleCount > 1 && info.ArrayLayerCount == 1 => TextureViewDimension.Texture2DMultisampled,
        TextureDimension.Texture2D when info.SampleCount > 1 => TextureViewDimension.Texture2DMultisampledArray,
        TextureDimension.Texture2D when info.ArrayLayerCount == 1 => TextureViewDimension.Texture2D,
        TextureDimension.Texture2D => TextureViewDimension.Texture2DArray,
        TextureDimension.Texture3D => TextureViewDimension.Texture3D,
        _ => default!,
    };
    private readonly record struct D3D12CpuDescriptorRange(CpuDescriptorHandle Start, uint Increment, uint Count)
    {
        internal CpuDescriptorHandle this[uint index] => index < Count ? new CpuDescriptorHandle(Start.Ptr + checked((nuint)(index * Increment))) : default!;
    }

    private sealed partial class D3D12CommandContext
    {
        internal D3D12CpuDescriptorRange AllocateTemporaryAttachmentDescriptors(DescriptorHeapType type, uint count) => Recording.AllocateTemporaryAttachmentDescriptors(type, count);
    }

    private sealed partial class D3D12CommandBuffer
    {
        private CpuDescriptorHandle _temporaryRtvStart;
        private CpuDescriptorHandle _temporaryDsvStart;
        private ID3D12DescriptorHeap* _temporaryRtvHeap;
        private ID3D12DescriptorHeap* _temporaryDsvHeap;
        private uint _temporaryRtvCapacity;
        private uint _temporaryDsvCapacity;
        private uint _temporaryRtvUsed;
        private uint _temporaryDsvUsed;
        internal D3D12CpuDescriptorRange AllocateTemporaryAttachmentDescriptors(DescriptorHeapType type, uint count)
        {
            ref ID3D12DescriptorHeap* heap = ref (type == DescriptorHeapType.Rtv ? ref _temporaryRtvHeap : ref _temporaryDsvHeap);
            ref uint capacity = ref (type == DescriptorHeapType.Rtv ? ref _temporaryRtvCapacity : ref _temporaryDsvCapacity);
            ref uint used = ref (type == DescriptorHeapType.Rtv ? ref _temporaryRtvUsed : ref _temporaryDsvUsed);
            uint required = checked(used + count);
            if (heap is null || capacity < required)
            {
                DescriptorHeapDesc description = new(type, required, DescriptorHeapFlags.None, _context.NativeNodeMask);
                ID3D12DescriptorHeap* replacement = null;
                Guid iid = ID3D12DescriptorHeap.Guid;
                ThrowIfFailed(
                    _context.NativeDevice,
                    DeviceResourceTrace.CreateDescriptorHeap(_context.NativeDevice.Native, &description, &iid, (void**)&replacement),
                    NativeOperationType.Ordinary,
                    "ID3D12Device::CreateDescriptorHeap(command attachment arena)"
                );
                ID3D12DescriptorHeap* previous = heap;
                heap = replacement;
                if (type == DescriptorHeapType.Rtv)
                    _temporaryRtvStart = ResourceTrace.GetCPUDescriptorHandleForHeapStart(heap);
                else
                    _temporaryDsvStart = ResourceTrace.GetCPUDescriptorHandleForHeapStart(heap);
                capacity = required;
                if (previous is not null)
                    _transientObjects.Add((nint)previous);
            }

            uint first = used;
            used = required;
            CpuDescriptorHandle start = type == DescriptorHeapType.Rtv ? _temporaryRtvStart : _temporaryDsvStart;
            uint increment = _context.NativeDevice.DescriptorIncrement(type);
            return new D3D12CpuDescriptorRange(new CpuDescriptorHandle(start.Ptr + checked((nuint)(first * increment))), increment, count);
        }

        internal void ResetTemporaryAttachmentDescriptors()
        {
            _temporaryRtvUsed = 0;
            _temporaryDsvUsed = 0;
        }

        private void PrepareTemporaryAttachmentCapacity(uint rtvCount, uint dsvCount)
        {
            PrepareTemporaryAttachmentCapacity(DescriptorHeapType.Rtv, checked(_temporaryRtvUsed + rtvCount));
            PrepareTemporaryAttachmentCapacity(DescriptorHeapType.Dsv, checked(_temporaryDsvUsed + dsvCount));
        }

        private void PrepareTemporaryAttachmentCapacity(DescriptorHeapType type, uint required)
        {
            if (required == 0)
                return;
            ref ID3D12DescriptorHeap* heap = ref (type == DescriptorHeapType.Rtv ? ref _temporaryRtvHeap : ref _temporaryDsvHeap);
            ref uint capacity = ref (type == DescriptorHeapType.Rtv ? ref _temporaryRtvCapacity : ref _temporaryDsvCapacity);
            if (heap is not null && capacity >= required)
                return;
            DescriptorHeapDesc description = new(type, required, DescriptorHeapFlags.None, _context.NativeNodeMask);
            ID3D12DescriptorHeap* replacement = null;
            Guid iid = ID3D12DescriptorHeap.Guid;
            ThrowIfFailed(
                _context.NativeDevice,
                DeviceResourceTrace.CreateDescriptorHeap(_context.NativeDevice.Native, &description, &iid, (void**)&replacement),
                NativeOperationType.Ordinary,
                "ID3D12Device::CreateDescriptorHeap(command attachment arena)"
            );
            ID3D12DescriptorHeap* previous = heap;
            heap = replacement;
            if (type == DescriptorHeapType.Rtv)
                _temporaryRtvStart = ResourceTrace.GetCPUDescriptorHandleForHeapStart(heap);
            else
                _temporaryDsvStart = ResourceTrace.GetCPUDescriptorHandleForHeapStart(heap);
            capacity = required;
            if (previous is not null)
                _transientObjects.Add((nint)previous);
        }

        internal void ReleaseTemporaryAttachmentDescriptors()
        {
            ID3D12DescriptorHeap* rtv = _temporaryRtvHeap;
            _temporaryRtvHeap = null;
            _temporaryRtvStart = default;
            if (rtv is not null)
                _ = ResourceTrace.Release(rtv);
            ID3D12DescriptorHeap* dsv = _temporaryDsvHeap;
            _temporaryDsvHeap = null;
            _temporaryDsvStart = default;
            if (dsv is not null)
                _ = ResourceTrace.Release(dsv);
            _temporaryRtvCapacity = 0;
            _temporaryDsvCapacity = 0;
            ResetTemporaryAttachmentDescriptors();
        }
    }
}
