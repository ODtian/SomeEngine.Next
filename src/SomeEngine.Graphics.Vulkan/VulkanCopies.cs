namespace SomeEngine.Graphics.Vulkan;
internal sealed unsafe partial class VulkanBackend
{
    internal void CopyBuffer(CommandContext context, in BufferCopy copy)
    {
        VulkanCommandContext command = ((VulkanCommandContext)context);
        VulkanDevice device = (VulkanDevice)command.Device;
        VulkanBuffer source = RequireBuffer(device, copy.Source, nameof(copy));
        VulkanBuffer destination = RequireBuffer(device, copy.Destination, nameof(copy));
        BufferCopy2 region = new()
        {
            SType = StructureType.BufferCopy2,
            SrcOffset = copy.SourceOffset,
            DstOffset = copy.DestinationOffset,
            Size = copy.Size,
        };
        CopyBufferInfo2 info = new()
        {
            SType = StructureType.CopyBufferInfo2,
            SrcBuffer = source.Native,
            DstBuffer = destination.Native,
            RegionCount = 1,
            PRegions = &region,
        };
        Api.CmdCopyBuffer2(command.NativeRecording, &info);
    }

    internal void CopyBufferToTexture(CommandContext context, in BufferTextureCopy copy)
    {
        VulkanCommandContext command = ((VulkanCommandContext)context);
        VulkanDevice device = (VulkanDevice)command.Device;
        VulkanBuffer source = RequireBuffer(device, copy.Buffer, nameof(copy));
        VulkanTexture destination = RequireTexture(device, copy.Texture, nameof(copy));
        BufferImageCopy2 region = CreateBufferImageCopy(copy, destination.Info);
        command.RecordSwapchainUse(destination);
        CopyBufferToImageInfo2 info = new()
        {
            SType = StructureType.CopyBufferToImageInfo2,
            SrcBuffer = source.Native,
            DstImage = destination.Native,
            DstImageLayout = ImageLayout.TransferDstOptimal,
            RegionCount = 1,
            PRegions = &region,
        };
        Api.CmdCopyBufferToImage2(command.NativeRecording, &info);
    }

    internal void CopyTextureToBuffer(CommandContext context, in BufferTextureCopy copy)
    {
        VulkanCommandContext command = ((VulkanCommandContext)context);
        VulkanDevice device = (VulkanDevice)command.Device;
        VulkanBuffer destination = RequireBuffer(device, copy.Buffer, nameof(copy));
        VulkanTexture source = RequireTexture(device, copy.Texture, nameof(copy));
        BufferImageCopy2 region = CreateBufferImageCopy(copy, source.Info);
        command.RecordSwapchainUse(source);
        CopyImageToBufferInfo2 info = new()
        {
            SType = StructureType.CopyImageToBufferInfo2,
            SrcImage = source.Native,
            SrcImageLayout = ImageLayout.TransferSrcOptimal,
            DstBuffer = destination.Native,
            RegionCount = 1,
            PRegions = &region,
        };
        Api.CmdCopyImageToBuffer2(command.NativeRecording, &info);
    }

    internal void CopyTexture(CommandContext context, in TextureCopy copy)
    {
        VulkanCommandContext command = ((VulkanCommandContext)context);
        VulkanDevice device = (VulkanDevice)command.Device;
        VulkanTexture source = RequireTexture(device, copy.Source, nameof(copy));
        VulkanTexture destination = RequireTexture(device, copy.Destination, nameof(copy));
        command.RecordSwapchainUse(source);
        command.RecordSwapchainUse(destination);
        ImageCopy2 region = new()
        {
            SType = StructureType.ImageCopy2,
            SrcSubresource = new ImageSubresourceLayers(ToNative(copy.SourceAspect), copy.SourceMipLevel, copy.SourceArrayLayer, 1),
            SrcOffset = new Offset3D(checked((int)copy.SourceX), checked((int)copy.SourceY), checked((int)copy.SourceZ)),
            DstSubresource = new ImageSubresourceLayers(ToNative(copy.DestinationAspect), copy.DestinationMipLevel, copy.DestinationArrayLayer, 1),
            DstOffset = new Offset3D(checked((int)copy.DestinationX), checked((int)copy.DestinationY), checked((int)copy.DestinationZ)),
            Extent = new Extent3D(copy.Width, copy.Height, copy.Depth),
        };
        CopyImageInfo2 info = new()
        {
            SType = StructureType.CopyImageInfo2,
            SrcImage = source.Native,
            SrcImageLayout = ImageLayout.TransferSrcOptimal,
            DstImage = destination.Native,
            DstImageLayout = ImageLayout.TransferDstOptimal,
            RegionCount = 1,
            PRegions = &region,
        };
        Api.CmdCopyImage2(command.NativeRecording, &info);
    }

    internal void ResolveTexture(CommandContext context, in TextureResolve resolve)
    {
        VulkanCommandContext command = ((VulkanCommandContext)context);
        VulkanDevice device = (VulkanDevice)command.Device;
        VulkanTexture source = RequireTexture(device, resolve.Source, nameof(resolve));
        VulkanTexture destination = RequireTexture(device, resolve.Destination, nameof(resolve));
        command.RecordSwapchainUse(source);
        command.RecordSwapchainUse(destination);
        uint width = Math.Min(MipExtent(source.Info.Width, resolve.SourceMipLevel), MipExtent(destination.Info.Width, resolve.DestinationMipLevel));
        uint height = Math.Min(
            MipExtent(source.Info.Height, resolve.SourceMipLevel),
            MipExtent(destination.Info.Height, resolve.DestinationMipLevel)
        );
        ImageAspectFlags aspect = VulkanFormats.IsDepthStencil(resolve.Format) ? VulkanFormats.Aspects(resolve.Format) : ImageAspectFlags.ColorBit;
        ImageResolve2 region = new()
        {
            SType = StructureType.ImageResolve2,
            SrcSubresource = new ImageSubresourceLayers(aspect, resolve.SourceMipLevel, resolve.SourceArrayLayer, 1),
            DstSubresource = new ImageSubresourceLayers(aspect, resolve.DestinationMipLevel, resolve.DestinationArrayLayer, 1),
            Extent = new Extent3D(width, height, 1),
        };
        ResolveImageInfo2 info = new()
        {
            SType = StructureType.ResolveImageInfo2,
            SrcImage = source.Native,
            SrcImageLayout = ImageLayout.TransferSrcOptimal,
            DstImage = destination.Native,
            DstImageLayout = ImageLayout.TransferDstOptimal,
            RegionCount = 1,
            PRegions = &region,
        };
        Api.CmdResolveImage2(command.NativeRecording, &info);
    }

    internal void ClearBuffer(CommandContext context, RhiBuffer buffer, in BufferRange range, uint value = 0)
    {
        VulkanCommandContext command = ((VulkanCommandContext)context);
        VulkanBuffer native = RequireBuffer((VulkanDevice)command.Device, buffer, nameof(buffer));
        BufferRange resolved = range.Expand(native.Info.Size);
        Api.CmdFillBuffer(command.NativeRecording, native.Native, resolved.Offset, resolved.Size, value);
    }

    internal void ClearTexture(CommandContext context, RhiTexture texture, in TextureSubresourceRange range, in Vector4 color)
    {
        VulkanCommandContext command = ((VulkanCommandContext)context);
        VulkanTexture native = RequireTexture((VulkanDevice)command.Device, texture, nameof(texture));
        command.RecordSwapchainUse(native);
        ClearColorValue clear = default;
        clear.Float32_0 = color.X;
        clear.Float32_1 = color.Y;
        clear.Float32_2 = color.Z;
        clear.Float32_3 = color.W;
        ImageSubresourceRange nativeRange = ToNative(range);
        Api.CmdClearColorImage(command.NativeRecording, native.Native, ImageLayout.General, &clear, 1, &nativeRange);
    }

    internal void ClearDepthStencil(
        CommandContext context,
        RhiTexture texture,
        in TextureSubresourceRange range,
        float depth = 1,
        byte stencil = 0
    )
    {
        VulkanCommandContext command = ((VulkanCommandContext)context);
        VulkanTexture native = RequireTexture((VulkanDevice)command.Device, texture, nameof(texture));
        command.RecordSwapchainUse(native);
        ClearDepthStencilValue clear = new(depth, stencil);
        ImageSubresourceRange nativeRange = ToNative(range);
        Api.CmdClearDepthStencilImage(command.NativeRecording, native.Native, ImageLayout.General, &clear, 1, &nativeRange);
    }

    private static BufferImageCopy2 CreateBufferImageCopy(in BufferTextureCopy copy, TextureInfo texture)
    {
        (uint blockWidth, _, uint blockBytes) = VulkanFormats.GetBlockInfo(texture.Format);
        if (copy.Aspect == TextureAspects.Stencil)
            (blockWidth, blockBytes) = (1, 1);
        else if (copy.Aspect == TextureAspects.Depth && texture.Format == Format.D32FloatS8UInt)
            (blockWidth, blockBytes) = (1, 4);
        uint rowLength = copy.BufferRowPitch == 0 ? 0 : checked(copy.BufferRowPitch / blockBytes * blockWidth);
        return new BufferImageCopy2
        {
            SType = StructureType.BufferImageCopy2,
            BufferOffset = copy.BufferOffset,
            BufferRowLength = rowLength,
            BufferImageHeight = copy.BufferImageHeight,
            ImageSubresource = new ImageSubresourceLayers(ToNative(copy.Aspect), copy.MipLevel, copy.ArrayLayer, 1),
            ImageOffset = new Offset3D(checked((int)copy.X), checked((int)copy.Y), checked((int)copy.Z)),
            ImageExtent = new Extent3D(copy.Width, copy.Height, copy.Depth),
        };
    }

    internal static uint MipExtent(uint extent, uint mipLevel) => Math.Max(extent >> checked((int)mipLevel), 1u);
}
