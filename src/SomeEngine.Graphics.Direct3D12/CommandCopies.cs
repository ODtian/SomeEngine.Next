using Silk.NET.Direct3D12;
using NativeResourceDesc = Silk.NET.Direct3D12.ResourceDesc;

namespace SomeEngine.Graphics.Direct3D12;

internal sealed unsafe partial class D3D12Backend
{
    public void CopyBuffer(CommandContext context, in BufferCopy copy)
    {
        D3D12CommandContext command = RequireCommandContext(context, nameof(context));
        D3D12Buffer source = RequireBuffer(copy.Source);
        D3D12Buffer destination = RequireBuffer(copy.Destination);
        _ = new BufferRange(copy.SourceOffset, copy.Size).Resolve(source.Info.Size);
        _ = new BufferRange(copy.DestinationOffset, copy.Size).Resolve(destination.Info.Size);
        command.PrepareCaptures(2, sparseGenerations: 2);
        command.Capture(source);
        command.Capture(destination);
        command.List->CopyBufferRegion(
            destination.Native,
            copy.DestinationOffset,
            source.Native,
            copy.SourceOffset,
            copy.Size);
    }

    public void CopyBufferToTexture(CommandContext context, in BufferTextureCopy copy)
    {
        D3D12CommandContext command = RequireCommandContext(context, nameof(context));
        D3D12Buffer source = RequireBuffer(copy.Buffer);
        D3D12TextureResource destination = RequireTexture(copy.Texture);
        TextureCopyLocation bufferLocation = CreateBufferCopyLocation(
            command.NativeDevice,
            source,
            destination,
            copy);
        TextureCopyLocation textureLocation = CreateTextureCopyLocation(
            destination,
            copy.MipLevel,
            copy.ArrayLayer,
            copy.Aspect);
        Box sourceBox = new(0, 0, 0, copy.Width, copy.Height, copy.Depth);
        command.PrepareCaptures(2, 0, 2);
        command.PrepareSwapchainUses(1);
        command.Capture(source);
        command.Capture(destination);
        command.List->CopyTextureRegion(
            &textureLocation,
            copy.X,
            copy.Y,
            copy.Z,
            &bufferLocation,
            &sourceBox);
    }

    public void CopyTextureToBuffer(CommandContext context, in BufferTextureCopy copy)
    {
        D3D12CommandContext command = RequireCommandContext(context, nameof(context));
        D3D12Buffer destination = RequireBuffer(copy.Buffer);
        D3D12TextureResource source = RequireTexture(copy.Texture);
        TextureCopyLocation bufferLocation = CreateBufferCopyLocation(
            command.NativeDevice,
            destination,
            source,
            copy);
        TextureCopyLocation textureLocation = CreateTextureCopyLocation(
            source,
            copy.MipLevel,
            copy.ArrayLayer,
            copy.Aspect);
        Box sourceBox = new(
            copy.X,
            copy.Y,
            copy.Z,
            checked(copy.X + copy.Width),
            checked(copy.Y + copy.Height),
            checked(copy.Z + copy.Depth));
        command.PrepareCaptures(2, 0, 2);
        command.PrepareSwapchainUses(1);
        command.Capture(source);
        command.Capture(destination);
        command.List->CopyTextureRegion(
            &bufferLocation,
            0,
            0,
            0,
            &textureLocation,
            &sourceBox);
    }

    public void CopyTexture(CommandContext context, in TextureCopy copy)
    {
        D3D12CommandContext command = RequireCommandContext(context, nameof(context));
        D3D12TextureResource source = RequireTexture(copy.Source);
        D3D12TextureResource destination = RequireTexture(copy.Destination);
        ValidateTextureRegion(
            source.Info,
            copy.SourceMipLevel,
            copy.SourceArrayLayer,
            copy.SourceAspect,
            copy.SourceX,
            copy.SourceY,
            copy.SourceZ,
            copy.Width,
            copy.Height,
            copy.Depth);
        ValidateTextureRegion(
            destination.Info,
            copy.DestinationMipLevel,
            copy.DestinationArrayLayer,
            copy.DestinationAspect,
            copy.DestinationX,
            copy.DestinationY,
            copy.DestinationZ,
            copy.Width,
            copy.Height,
            copy.Depth);
        if (source.Info.SampleCount != destination.Info.SampleCount ||
            FormatMappings.ToTypelessFamily(source.Info.Format) !=
            FormatMappings.ToTypelessFamily(destination.Info.Format))
        {
            throw new ArgumentException(
                "Texture copies require matching sample counts and compatible format families.",
                nameof(copy));
        }
        TextureCopyLocation sourceLocation = CreateTextureCopyLocation(
            source,
            copy.SourceMipLevel,
            copy.SourceArrayLayer,
            copy.SourceAspect);
        TextureCopyLocation destinationLocation = CreateTextureCopyLocation(
            destination,
            copy.DestinationMipLevel,
            copy.DestinationArrayLayer,
            copy.DestinationAspect);
        Box sourceBox = new(
            copy.SourceX,
            copy.SourceY,
            copy.SourceZ,
            checked(copy.SourceX + copy.Width),
            checked(copy.SourceY + copy.Height),
            checked(copy.SourceZ + copy.Depth));
        command.PrepareCaptures(2, sparseGenerations: 2);
        command.PrepareSwapchainUses(2);
        command.Capture(source);
        command.Capture(destination);
        command.List->CopyTextureRegion(
            &destinationLocation,
            copy.DestinationX,
            copy.DestinationY,
            copy.DestinationZ,
            &sourceLocation,
            &sourceBox);
    }

    public void ResolveTexture(CommandContext context, in TextureResolve resolve)
    {
        D3D12CommandContext command = RequireCommandContext(context, nameof(context));
        D3D12TextureResource source = RequireTexture(resolve.Source);
        D3D12TextureResource destination = RequireTexture(resolve.Destination);
        ResolveMode resolveMode = ToResolveMode(resolve.Type, resolve.Format);
        uint sourceSubresource = NativeSubresource(
            source.Info,
            resolve.SourceMipLevel,
            resolve.SourceArrayLayer,
            TextureAspects.Color);
        uint destinationSubresource = NativeSubresource(
            destination.Info,
            resolve.DestinationMipLevel,
            resolve.DestinationArrayLayer,
            TextureAspects.Color);
        uint sourceWidth = MipDimension(source.Info.Width, resolve.SourceMipLevel);
        uint sourceHeight = MipDimension(source.Info.Height, resolve.SourceMipLevel);
        uint destinationWidth = MipDimension(destination.Info.Width, resolve.DestinationMipLevel);
        uint destinationHeight = MipDimension(destination.Info.Height, resolve.DestinationMipLevel);
        if (source.Info.Dimension != TextureDimension.Texture2D ||
            destination.Info.Dimension != TextureDimension.Texture2D ||
            source.Info.SampleCount <= 1 ||
            destination.Info.SampleCount != 1 ||
            sourceWidth != destinationWidth ||
            sourceHeight != destinationHeight ||
            FormatMappings.ToTypelessFamily(source.Info.Format) !=
            FormatMappings.ToTypelessFamily(destination.Info.Format) ||
            FormatMappings.ToTypelessFamily(resolve.Format) !=
            FormatMappings.ToTypelessFamily(source.Info.Format))
        {
            throw new ArgumentException(
                "Resolve requires equally sized compatible 2D subresources from multisampled to single-sampled storage.",
                nameof(resolve));
        }
        command.PrepareCaptures(2, sparseGenerations: 2);
        command.PrepareSwapchainUses(2);
        command.Capture(source);
        command.Capture(destination);
        command.List->ResolveSubresourceRegion(
            destination.Native,
            destinationSubresource,
            0,
            0,
            source.Native,
            sourceSubresource,
            null,
            FormatMappings.ToDxgi(resolve.Format),
            resolveMode);
    }

    public void ClearBuffer(
        CommandContext context,
        Buffer buffer,
        in BufferRange range,
        uint value = 0)
    {
        D3D12CommandContext command = RequireCommandContext(context, nameof(context));
        D3D12Buffer destination = RequireBuffer(buffer);
        BufferRange resolved = range.Resolve(destination.Info.Size);
        command.PrepareCaptures(1, sparseGenerations: 1);
        command.PrepareOrdinaryData(resolved.Size);
        D3D12OrdinaryDataReservation upload =
            command.ReserveTransientOrdinaryData(resolved.Size);
        command.Capture(destination);
        upload.CommitPattern(value, resolved.Size);
        command.List->CopyBufferRegion(
            destination.Native,
            resolved.Offset,
            upload.Resource,
            upload.Offset,
            resolved.Size);
    }

    public void ClearTexture(
        CommandContext context,
        Texture texture,
        in TextureSubresourceRange range,
        in System.Numerics.Vector4 color)
    {
        D3D12CommandContext command = RequireCommandContext(context, nameof(context));
        D3D12TextureResource destination = RequireTexture(texture);
        if ((destination.Info.Usages & TextureUsages.ColorAttachment) == 0)
        {
            throw new ArgumentException(
                "ClearTexture requires a Texture created with ColorAttachment usage.",
                nameof(texture));
        }
        ResolveTextureRange(destination.Info, range, TextureAspects.Color);

        float* values = stackalloc float[4] { color.X, color.Y, color.Z, color.W };
        uint descriptorCount = checked(
            range.MipLevelCount *
            (destination.Info.Dimension == TextureDimension.Texture3D ? 1u : range.ArrayLayerCount));
        command.PrepareCaptures(1, sparseGenerations: 1);
        command.PrepareSwapchainUses(1);
        command.PrepareAttachmentDescriptors(descriptorCount, 0);
        command.PrepareTransientObjects(1);
        command.Capture(destination);
        D3D12CpuDescriptorRange descriptors = command.AllocateTemporaryAttachmentDescriptors(
            DescriptorHeapType.Rtv,
            descriptorCount);
        uint descriptorIndex = 0;
        for (uint mip = range.FirstMipLevel;
             mip < range.FirstMipLevel + range.MipLevelCount;
             mip++)
        {
            if (destination.Info.Dimension == TextureDimension.Texture3D)
            {
                TextureSubresourceRange single = new(
                    mip,
                    1,
                    range.FirstArrayLayer,
                    range.ArrayLayerCount,
                    TextureAspects.Color);
                ColorAttachmentViewDesc description = new(
                    texture,
                    single,
                    destination.Info.Format,
                    TextureViewDimension.Texture3D);
                CpuDescriptorHandle descriptor = descriptors[descriptorIndex++];
                WriteColorAttachmentView(
                    command.NativeDevice,
                    destination,
                    description,
                    descriptor);
                command.List->ClearRenderTargetView(
                    descriptor,
                    values,
                    0,
                    null);
                continue;
            }
            uint layerEnd = destination.Info.Dimension == TextureDimension.Texture3D
                ? range.FirstArrayLayer + 1
                : range.FirstArrayLayer + range.ArrayLayerCount;
            for (uint layer = range.FirstArrayLayer; layer < layerEnd; layer++)
            {
                TextureSubresourceRange single = new(mip, 1, layer, 1, TextureAspects.Color);
                ColorAttachmentViewDesc description = new(
                    texture,
                    single,
                    destination.Info.Format,
                    ToAttachmentDimension(destination.Info));
                CpuDescriptorHandle descriptor = descriptors[descriptorIndex++];
                WriteColorAttachmentView(
                    command.NativeDevice,
                    destination,
                    description,
                    descriptor);
                command.List->ClearRenderTargetView(
                    descriptor,
                    values,
                    0,
                    null);
            }
        }
    }

    public void ClearDepthStencil(
        CommandContext context,
        Texture texture,
        in TextureSubresourceRange range,
        float depth = 1,
        byte stencil = 0)
    {
        D3D12CommandContext command = RequireCommandContext(context, nameof(context));
        D3D12TextureResource destination = RequireTexture(texture);
        if ((destination.Info.Usages & TextureUsages.DepthStencilAttachment) == 0)
        {
            throw new ArgumentException(
                "ClearDepthStencil requires a Texture created with DepthStencilAttachment usage.",
                nameof(texture));
        }
        const TextureAspects clearAspects = TextureAspects.Depth | TextureAspects.Stencil;
        if ((range.Aspects & clearAspects) == 0 || (range.Aspects & ~clearAspects) != 0)
        {
            throw new ArgumentException(
                "ClearDepthStencil requires explicit Depth and/or Stencil aspects.",
                nameof(range));
        }
        ResolveTextureRange(
            destination.Info,
            range,
            TextureAspects.Depth | TextureAspects.Stencil);
        ClearFlags flags = 0;
        if ((range.Aspects & TextureAspects.Depth) != 0)
            flags |= ClearFlags.Depth;
        if ((range.Aspects & TextureAspects.Stencil) != 0)
            flags |= ClearFlags.Stencil;

        uint descriptorCount = checked(
            range.MipLevelCount *
            (destination.Info.Dimension == TextureDimension.Texture3D ? 1u : range.ArrayLayerCount));
        command.PrepareCaptures(1, sparseGenerations: 1);
        command.PrepareSwapchainUses(1);
        command.PrepareAttachmentDescriptors(0, descriptorCount);
        command.PrepareTransientObjects(1);
        command.Capture(destination);
        D3D12CpuDescriptorRange descriptors = command.AllocateTemporaryAttachmentDescriptors(
            DescriptorHeapType.Dsv,
            descriptorCount);
        uint descriptorIndex = 0;
        for (uint mip = range.FirstMipLevel;
             mip < range.FirstMipLevel + range.MipLevelCount;
             mip++)
        {
            uint layerEnd = destination.Info.Dimension == TextureDimension.Texture3D
                ? range.FirstArrayLayer + 1
                : range.FirstArrayLayer + range.ArrayLayerCount;
            for (uint layer = range.FirstArrayLayer; layer < layerEnd; layer++)
            {
                TextureSubresourceRange single = new(mip, 1, layer, 1, range.Aspects);
                DepthStencilViewDesc description = new(
                    texture,
                    single,
                    destination.Info.Format,
                    ToAttachmentDimension(destination.Info));
                CpuDescriptorHandle descriptor = descriptors[descriptorIndex++];
                WriteDepthStencilView(
                    command.NativeDevice,
                    destination,
                    description,
                    descriptor);
                command.List->ClearDepthStencilView(
                    descriptor,
                    flags,
                    depth,
                    stencil,
                    0,
                    null);
            }
        }
    }

    private static TextureCopyLocation CreateBufferCopyLocation(
        D3D12Device device,
        D3D12Buffer buffer,
        D3D12TextureResource texture,
        in BufferTextureCopy copy)
    {
        ValidateTextureRegion(
            texture.Info,
            copy.MipLevel,
            copy.ArrayLayer,
            copy.Aspect,
            copy.X,
            copy.Y,
            copy.Z,
            copy.Width,
            copy.Height,
            copy.Depth);
        if ((copy.BufferOffset & 511) != 0)
        {
            throw new ArgumentException(
                "A placed Texture footprint requires a 512-byte offset alignment.",
                nameof(copy));
        }

        NativeResourceDesc description = texture.Native->GetDesc();
        uint subresource = NativeSubresource(
            texture.Info,
            copy.MipLevel,
            copy.ArrayLayer,
            copy.Aspect);
        PlacedSubresourceFootprint footprint = GetNativeCopyFootprint(
            device,
            description,
            subresource,
            copy.BufferOffset,
            out _,
            out _,
            out ulong totalSize);
        if (copy.BufferRowPitch != footprint.Footprint.RowPitch)
        {
            throw new ArgumentException(
                "BufferRowPitch must equal the D3D12 copyable footprint row pitch.",
                nameof(copy));
        }
        if (copy.BufferImageHeight != 0 &&
            copy.BufferImageHeight != footprint.Footprint.Height)
        {
            throw new ArgumentException(
                "BufferImageHeight must be zero or equal the D3D12 copyable footprint height.",
                nameof(copy));
        }
        ulong required = checked(footprint.Offset + totalSize);
        if (required > buffer.Info.Size)
            throw new ArgumentOutOfRangeException(nameof(copy), "The copyable footprint escapes the Buffer.");
        TextureCopyLocation location = new()
        {
            PResource = buffer.Native,
            Type = TextureCopyType.PlacedFootprint,
        };
        location.PlacedFootprint = footprint;
        return location;
    }

    private static TextureCopyLocation CreateTextureCopyLocation(
        D3D12TextureResource texture,
        uint mip,
        uint layer,
        TextureAspects aspect)
    {
        TextureCopyLocation location = new()
        {
            PResource = texture.Native,
            Type = TextureCopyType.SubresourceIndex,
        };
        location.SubresourceIndex = NativeSubresource(texture.Info, mip, layer, aspect);
        return location;
    }

    private static uint NativeSubresource(
        TextureInfo info,
        uint mip,
        uint layer,
        TextureAspects aspect)
    {
        if (mip >= info.MipLevelCount)
            throw new ArgumentOutOfRangeException(nameof(mip));
        if (info.Dimension == TextureDimension.Texture3D && layer != 0)
            throw new ArgumentOutOfRangeException(nameof(layer));
        uint arrayLayer = info.Dimension == TextureDimension.Texture3D ? 0u : layer;
        if (arrayLayer >= info.ArrayLayerCount)
            throw new ArgumentOutOfRangeException(nameof(layer));
        uint plane = FormatMappings.PlaneIndex(info.Format, aspect);
        return checked(
            mip + arrayLayer * info.MipLevelCount +
            plane * info.MipLevelCount * info.ArrayLayerCount);
    }

    private static ResolveMode ToResolveMode(ResolveType type, Format format)
    {
        if (type == ResolveType.Average && FormatMappings.IsInteger(format))
        {
            throw new ArgumentException(
                "Average resolve is unavailable for integer formats.",
                nameof(format));
        }

        return type switch
        {
            ResolveType.Average => ResolveMode.Average,
            ResolveType.Minimum => ResolveMode.Min,
            ResolveType.Maximum => ResolveMode.Max,
            ResolveType.SampleZero => throw new NotSupportedException(
                "D3D12 has no native sample-zero multisample resolve mode."),
            _ => throw new ArgumentOutOfRangeException(nameof(type)),
        };
    }

    private static void ResolveTextureRange(
        TextureInfo info,
        in TextureSubresourceRange range,
        TextureAspects allowedAspects)
    {
        if (range.MipLevelCount == 0 ||
            range.FirstMipLevel >= info.MipLevelCount ||
            range.MipLevelCount > info.MipLevelCount - range.FirstMipLevel)
            throw new ArgumentOutOfRangeException(nameof(range));
        if (range.ArrayLayerCount == 0)
            throw new ArgumentOutOfRangeException(nameof(range));
        for (uint mip = range.FirstMipLevel;
             mip < range.FirstMipLevel + range.MipLevelCount;
             mip++)
        {
            uint layers = info.Dimension == TextureDimension.Texture3D
                ? MipDimension(info.Depth, mip)
                : info.ArrayLayerCount;
            if (range.FirstArrayLayer >= layers ||
                range.ArrayLayerCount > layers - range.FirstArrayLayer)
                throw new ArgumentOutOfRangeException(nameof(range));
        }
        if (range.Aspects == TextureAspects.None ||
            (range.Aspects & ~allowedAspects) != 0)
            throw new ArgumentException("The Texture range selects an incompatible aspect.", nameof(range));
        TextureAspects named = range.Aspects & (
            TextureAspects.Color |
            TextureAspects.Depth |
            TextureAspects.Stencil);
        TextureAspects selectedPlanes = range.Aspects & (
            TextureAspects.Plane0 |
            TextureAspects.Plane1 |
            TextureAspects.Plane2);
        if (named != TextureAspects.None && selectedPlanes != TextureAspects.None)
        {
            throw new ArgumentException(
                "A Texture range must use either named aspects or plane aspects.",
                nameof(range));
        }
        if (allowedAspects == TextureAspects.Color)
        {
            _ = FormatMappings.PlaneIndex(info.Format, range.Aspects);
        }
        else
        {
            uint planes = FormatMappings.PlaneCount(info.Format);
            TextureAspects available = planes == 1
                ? TextureAspects.Depth | TextureAspects.Plane0
                : TextureAspects.Depth | TextureAspects.Stencil |
                  TextureAspects.Plane0 | TextureAspects.Plane1;
            if ((range.Aspects & ~available) != 0 || !FormatMappings.IsDepthStencil(info.Format))
                throw new ArgumentException("The Texture range selects an unavailable depth/stencil aspect.", nameof(range));
        }
    }

    private static void ValidateTextureRegion(
        TextureInfo info,
        uint mip,
        uint layer,
        TextureAspects aspect,
        uint x,
        uint y,
        uint z,
        uint width,
        uint height,
        uint depth)
    {
        _ = NativeSubresource(info, mip, layer, aspect);
        if (width == 0 || height == 0 || depth == 0)
            throw new ArgumentOutOfRangeException(nameof(width), "A Texture copy extent must be nonzero.");
        uint mipWidth = MipDimension(info.Width, mip);
        uint mipHeight = info.Dimension == TextureDimension.Texture1D
            ? 1u
            : MipDimension(info.Height, mip);
        uint mipDepth = info.Dimension == TextureDimension.Texture3D
            ? MipDimension(info.Depth, mip)
            : 1u;
        if (x > mipWidth || width > mipWidth - x ||
            y > mipHeight || height > mipHeight - y ||
            z > mipDepth || depth > mipDepth - z)
            throw new ArgumentOutOfRangeException(nameof(width), "The Texture copy region escapes the mip extent.");

        FormatMappings.GetCopyBlockInfo(
            info.Format,
            out uint blockWidth,
            out uint blockHeight,
            out _);
        if (blockWidth != 1 &&
            (x % blockWidth != 0 ||
             y % blockHeight != 0 ||
             (width % blockWidth != 0 && x + width != mipWidth) ||
             (height % blockHeight != 0 && y + height != mipHeight)))
        {
            throw new ArgumentException("A block-compressed copy region is not block aligned.");
        }
    }

    private static uint MipDimension(uint value, uint mip) =>
        Math.Max(1u, value >> checked((int)mip));

    private static TextureViewDimension ToAttachmentDimension(TextureInfo info) =>
        info.Dimension switch
        {
            TextureDimension.Texture1D when info.ArrayLayerCount == 1 =>
                TextureViewDimension.Texture1D,
            TextureDimension.Texture1D => TextureViewDimension.Texture1DArray,
            TextureDimension.Texture2D when info.SampleCount > 1 && info.ArrayLayerCount == 1 =>
                TextureViewDimension.Texture2DMultisampled,
            TextureDimension.Texture2D when info.SampleCount > 1 =>
                TextureViewDimension.Texture2DMultisampledArray,
            TextureDimension.Texture2D when info.ArrayLayerCount == 1 =>
                TextureViewDimension.Texture2D,
            TextureDimension.Texture2D => TextureViewDimension.Texture2DArray,
            TextureDimension.Texture3D => TextureViewDimension.Texture3D,
            _ => throw new ArgumentOutOfRangeException(nameof(info)),
        };

    private readonly record struct D3D12CpuDescriptorRange(
        CpuDescriptorHandle Start,
        uint Increment,
        uint Count)
    {
        internal CpuDescriptorHandle this[uint index] => index < Count
            ? new CpuDescriptorHandle(Start.Ptr + checked((nuint)(index * Increment)))
            : throw new ArgumentOutOfRangeException(nameof(index));
    }

    private sealed partial class D3D12CommandContext
    {
        internal D3D12CpuDescriptorRange AllocateTemporaryAttachmentDescriptors(
            DescriptorHeapType type,
            uint count) =>
            Recording.AllocateTemporaryAttachmentDescriptors(type, count);
    }

    private sealed partial class D3D12CommandSlot
    {
        private ID3D12DescriptorHeap* _temporaryRtvHeap;
        private ID3D12DescriptorHeap* _temporaryDsvHeap;
        private uint _temporaryRtvCapacity;
        private uint _temporaryDsvCapacity;
        private uint _temporaryRtvUsed;
        private uint _temporaryDsvUsed;

        internal D3D12CpuDescriptorRange AllocateTemporaryAttachmentDescriptors(
            DescriptorHeapType type,
            uint count)
        {
            if (count == 0 || type is not (DescriptorHeapType.Rtv or DescriptorHeapType.Dsv))
                throw new ArgumentOutOfRangeException(nameof(count));
            ref ID3D12DescriptorHeap* heap = ref (type == DescriptorHeapType.Rtv
                ? ref _temporaryRtvHeap
                : ref _temporaryDsvHeap);
            ref uint capacity = ref (type == DescriptorHeapType.Rtv
                ? ref _temporaryRtvCapacity
                : ref _temporaryDsvCapacity);
            ref uint used = ref (type == DescriptorHeapType.Rtv
                ? ref _temporaryRtvUsed
                : ref _temporaryDsvUsed);
            uint required = checked(used + count);
            if (heap is null || capacity < required)
            {
                DescriptorHeapDesc description = new(
                    type,
                    required,
                    DescriptorHeapFlags.None,
                    _context.NativeNodeMask);
                ID3D12DescriptorHeap* replacement = null;
                Guid iid = ID3D12DescriptorHeap.Guid;
                ThrowIfFailed(
                    _context.NativeDevice,
                    _context.NativeDevice.Native->CreateDescriptorHeap(
                        &description,
                        &iid,
                        (void**)&replacement),
                    NativeOperationType.Ordinary,
                    "ID3D12Device::CreateDescriptorHeap(command attachment arena)");
                ID3D12DescriptorHeap* previous = heap;
                heap = replacement;
                capacity = required;
                if (previous is not null)
                    _transientObjects.Add((nint)previous);
            }

            uint first = used;
            used = required;
            CpuDescriptorHandle start = heap->GetCPUDescriptorHandleForHeapStart();
            uint increment = _context.NativeDevice.Native
                ->GetDescriptorHandleIncrementSize(type);
            return new D3D12CpuDescriptorRange(
                new CpuDescriptorHandle(start.Ptr + checked((nuint)(first * increment))),
                increment,
                count);
        }

        internal void ResetTemporaryAttachmentDescriptors()
        {
            _temporaryRtvUsed = 0;
            _temporaryDsvUsed = 0;
        }

        private void PrepareTemporaryAttachmentCapacity(uint rtvCount, uint dsvCount)
        {
            PrepareTemporaryAttachmentCapacity(
                DescriptorHeapType.Rtv,
                checked(_temporaryRtvUsed + rtvCount));
            PrepareTemporaryAttachmentCapacity(
                DescriptorHeapType.Dsv,
                checked(_temporaryDsvUsed + dsvCount));
        }

        private void PrepareTemporaryAttachmentCapacity(
            DescriptorHeapType type,
            uint required)
        {
            if (required == 0)
                return;
            ref ID3D12DescriptorHeap* heap = ref (type == DescriptorHeapType.Rtv
                ? ref _temporaryRtvHeap
                : ref _temporaryDsvHeap);
            ref uint capacity = ref (type == DescriptorHeapType.Rtv
                ? ref _temporaryRtvCapacity
                : ref _temporaryDsvCapacity);
            if (heap is not null && capacity >= required)
                return;

            DescriptorHeapDesc description = new(
                type,
                required,
                DescriptorHeapFlags.None,
                _context.NativeNodeMask);
            ID3D12DescriptorHeap* replacement = null;
            Guid iid = ID3D12DescriptorHeap.Guid;
            ThrowIfFailed(
                _context.NativeDevice,
                _context.NativeDevice.Native->CreateDescriptorHeap(
                    &description,
                    &iid,
                    (void**)&replacement),
                NativeOperationType.Ordinary,
                "ID3D12Device::CreateDescriptorHeap(command attachment arena)");
            ID3D12DescriptorHeap* previous = heap;
            heap = replacement;
            capacity = required;
            if (previous is not null)
                _transientObjects.Add((nint)previous);
        }

        internal void ReleaseTemporaryAttachmentDescriptors()
        {
            ID3D12DescriptorHeap* rtv = _temporaryRtvHeap;
            _temporaryRtvHeap = null;
            if (rtv is not null)
                _ = rtv->Release();
            ID3D12DescriptorHeap* dsv = _temporaryDsvHeap;
            _temporaryDsvHeap = null;
            if (dsv is not null)
                _ = dsv->Release();
            _temporaryRtvCapacity = 0;
            _temporaryDsvCapacity = 0;
            ResetTemporaryAttachmentDescriptors();
        }
    }
}
