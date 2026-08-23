namespace SomeEngine.Graphics.Vulkan;

internal sealed unsafe partial class VulkanBackend
{
    internal void CopyBuffer(CommandContext context, in BufferCopy copy)
    {
        VulkanCommandContext command = RequireCommandContext(context, nameof(context));
        VulkanDevice device = (VulkanDevice)command.Device;
        VulkanBuffer source = RequireBuffer(device, copy.Source, nameof(copy));
        VulkanBuffer destination = RequireBuffer(device, copy.Destination, nameof(copy));
        ValidateBufferCopy(copy, source, destination);
        command.Capture(source);
        command.Capture(destination);
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
        VulkanCommandContext command = RequireCommandContext(context, nameof(context));
        VulkanDevice device = (VulkanDevice)command.Device;
        VulkanBuffer source = RequireBuffer(device, copy.Buffer, nameof(copy));
        VulkanTexture destination = RequireTexture(device, copy.Texture, nameof(copy));
        BufferImageCopy2 region = CreateBufferImageCopy(copy, destination.Info);
        command.Capture(source);
        command.Capture(destination);
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
        VulkanCommandContext command = RequireCommandContext(context, nameof(context));
        VulkanDevice device = (VulkanDevice)command.Device;
        VulkanBuffer destination = RequireBuffer(device, copy.Buffer, nameof(copy));
        VulkanTexture source = RequireTexture(device, copy.Texture, nameof(copy));
        BufferImageCopy2 region = CreateBufferImageCopy(copy, source.Info);
        command.Capture(source);
        command.Capture(destination);
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
        VulkanCommandContext command = RequireCommandContext(context, nameof(context));
        VulkanDevice device = (VulkanDevice)command.Device;
        VulkanTexture source = RequireTexture(device, copy.Source, nameof(copy));
        VulkanTexture destination = RequireTexture(device, copy.Destination, nameof(copy));
        ValidateTextureCopy(copy, source.Info, destination.Info);
        command.Capture(source);
        command.Capture(destination);
        ImageCopy2 region = new()
        {
            SType = StructureType.ImageCopy2,
            SrcSubresource = new ImageSubresourceLayers(
                ToNative(copy.SourceAspect),
                copy.SourceMipLevel,
                copy.SourceArrayLayer,
                1),
            SrcOffset = new Offset3D(
                checked((int)copy.SourceX),
                checked((int)copy.SourceY),
                checked((int)copy.SourceZ)),
            DstSubresource = new ImageSubresourceLayers(
                ToNative(copy.DestinationAspect),
                copy.DestinationMipLevel,
                copy.DestinationArrayLayer,
                1),
            DstOffset = new Offset3D(
                checked((int)copy.DestinationX),
                checked((int)copy.DestinationY),
                checked((int)copy.DestinationZ)),
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
        VulkanCommandContext command = RequireCommandContext(context, nameof(context));
        VulkanDevice device = (VulkanDevice)command.Device;
        VulkanTexture source = RequireTexture(device, resolve.Source, nameof(resolve));
        VulkanTexture destination = RequireTexture(device, resolve.Destination, nameof(resolve));
        if (resolve.Type is ResolveType.Minimum or ResolveType.Maximum)
        {
            throw new NotSupportedException(
                "Minimum/Maximum resolves require dynamic-rendering depth/stencil resolve attachments.");
        }
        if (source.Info.SampleCount <= 1 || destination.Info.SampleCount != 1 ||
            resolve.SourceMipLevel >= source.Info.MipLevelCount ||
            resolve.DestinationMipLevel >= destination.Info.MipLevelCount ||
            resolve.SourceArrayLayer >= source.Info.ArrayLayerCount ||
            resolve.DestinationArrayLayer >= destination.Info.ArrayLayerCount)
            throw new ArgumentOutOfRangeException(nameof(resolve));
        command.Capture(source);
        command.Capture(destination);
        uint width = Math.Min(
            MipExtent(source.Info.Width, resolve.SourceMipLevel),
            MipExtent(destination.Info.Width, resolve.DestinationMipLevel));
        uint height = Math.Min(
            MipExtent(source.Info.Height, resolve.SourceMipLevel),
            MipExtent(destination.Info.Height, resolve.DestinationMipLevel));
        ImageAspectFlags aspect = VulkanFormats.IsDepthStencil(resolve.Format)
            ? VulkanFormats.Aspects(resolve.Format)
            : ImageAspectFlags.ColorBit;
        ImageResolve2 region = new()
        {
            SType = StructureType.ImageResolve2,
            SrcSubresource = new ImageSubresourceLayers(
                aspect,
                resolve.SourceMipLevel,
                resolve.SourceArrayLayer,
                1),
            DstSubresource = new ImageSubresourceLayers(
                aspect,
                resolve.DestinationMipLevel,
                resolve.DestinationArrayLayer,
                1),
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

    internal void ClearBuffer(
        CommandContext context,
        RhiBuffer buffer,
        in BufferRange range,
        uint value = 0)
    {
        VulkanCommandContext command = RequireCommandContext(context, nameof(context));
        VulkanBuffer native = RequireBuffer((VulkanDevice)command.Device, buffer, nameof(buffer));
        BufferRange resolved = range.Resolve(native.Info.Size);
        if ((resolved.Offset & 3) != 0 || (resolved.Size & 3) != 0)
            throw new ArgumentOutOfRangeException(nameof(range), "vkCmdFillBuffer ranges must be 4-byte aligned.");
        command.Capture(native);
        Api.CmdFillBuffer(
            command.NativeRecording,
            native.Native,
            resolved.Offset,
            resolved.Size,
            value);
    }

    internal void ClearTexture(
        CommandContext context,
        RhiTexture texture,
        in TextureSubresourceRange range,
        in Vector4 color)
    {
        VulkanCommandContext command = RequireCommandContext(context, nameof(context));
        VulkanTexture native = RequireTexture((VulkanDevice)command.Device, texture, nameof(texture));
        ValidateTextureRange(native.Info, range);
        if ((range.Aspects & ~TextureAspects.Color) != 0)
            throw new ArgumentException("ClearTexture requires the Color aspect.", nameof(range));
        command.Capture(native);
        ClearColorValue clear = default;
        clear.Float32_0 = color.X;
        clear.Float32_1 = color.Y;
        clear.Float32_2 = color.Z;
        clear.Float32_3 = color.W;
        ImageSubresourceRange nativeRange = ToNative(range);
        Api.CmdClearColorImage(
            command.NativeRecording,
            native.Native,
            ImageLayout.General,
            &clear,
            1,
            &nativeRange);
    }

    internal void ClearDepthStencil(
        CommandContext context,
        RhiTexture texture,
        in TextureSubresourceRange range,
        float depth = 1,
        byte stencil = 0)
    {
        VulkanCommandContext command = RequireCommandContext(context, nameof(context));
        VulkanTexture native = RequireTexture((VulkanDevice)command.Device, texture, nameof(texture));
        ValidateTextureRange(native.Info, range);
        if ((range.Aspects & (TextureAspects.Depth | TextureAspects.Stencil)) == 0)
            throw new ArgumentException("ClearDepthStencil requires a Depth or Stencil aspect.", nameof(range));
        if (!float.IsFinite(depth) || depth is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(depth));
        command.Capture(native);
        ClearDepthStencilValue clear = new(depth, stencil);
        ImageSubresourceRange nativeRange = ToNative(range);
        Api.CmdClearDepthStencilImage(
            command.NativeRecording,
            native.Native,
            ImageLayout.General,
            &clear,
            1,
            &nativeRange);
    }

    private static BufferImageCopy2 CreateBufferImageCopy(
        in BufferTextureCopy copy,
        TextureInfo texture)
    {
        ValidateBufferTextureCopy(copy, texture);
        (uint blockWidth, _, uint blockBytes) = VulkanFormats.GetBlockInfo(texture.Format);
        uint rowLength = copy.BufferRowPitch == 0
            ? 0
            : checked(copy.BufferRowPitch / blockBytes * blockWidth);
        if (copy.BufferRowPitch != 0 && copy.BufferRowPitch % blockBytes != 0)
            throw new ArgumentOutOfRangeException(nameof(copy.BufferRowPitch));
        return new BufferImageCopy2
        {
            SType = StructureType.BufferImageCopy2,
            BufferOffset = copy.BufferOffset,
            BufferRowLength = rowLength,
            BufferImageHeight = copy.BufferImageHeight,
            ImageSubresource = new ImageSubresourceLayers(
                ToNative(copy.Aspect),
                copy.MipLevel,
                copy.ArrayLayer,
                1),
            ImageOffset = new Offset3D(
                checked((int)copy.X),
                checked((int)copy.Y),
                checked((int)copy.Z)),
            ImageExtent = new Extent3D(copy.Width, copy.Height, copy.Depth),
        };
    }

    private static void ValidateBufferCopy(
        in BufferCopy copy,
        VulkanBuffer source,
        VulkanBuffer destination)
    {
        if (copy.Size == 0 || copy.SourceOffset > source.Info.Size ||
            copy.Size > source.Info.Size - copy.SourceOffset ||
            copy.DestinationOffset > destination.Info.Size ||
            copy.Size > destination.Info.Size - copy.DestinationOffset)
            throw new ArgumentOutOfRangeException(nameof(copy));
    }

    private static void ValidateBufferTextureCopy(
        in BufferTextureCopy copy,
        TextureInfo texture)
    {
        if (copy.Width == 0 || copy.Height == 0 || copy.Depth == 0 ||
            copy.MipLevel >= texture.MipLevelCount ||
            copy.ArrayLayer >= texture.ArrayLayerCount)
            throw new ArgumentOutOfRangeException(nameof(copy));
        uint width = MipExtent(texture.Width, copy.MipLevel);
        uint height = MipExtent(texture.Height, copy.MipLevel);
        uint depth = MipExtent(texture.Depth, copy.MipLevel);
        if (copy.X > width || copy.Width > width - copy.X ||
            copy.Y > height || copy.Height > height - copy.Y ||
            copy.Z > depth || copy.Depth > depth - copy.Z)
            throw new ArgumentOutOfRangeException(nameof(copy));
    }

    private static void ValidateTextureCopy(
        in TextureCopy copy,
        TextureInfo source,
        TextureInfo destination)
    {
        if (copy.Width == 0 || copy.Height == 0 || copy.Depth == 0 ||
            copy.SourceMipLevel >= source.MipLevelCount ||
            copy.DestinationMipLevel >= destination.MipLevelCount ||
            copy.SourceArrayLayer >= source.ArrayLayerCount ||
            copy.DestinationArrayLayer >= destination.ArrayLayerCount)
            throw new ArgumentOutOfRangeException(nameof(copy));
        uint sourceWidth = MipExtent(source.Width, copy.SourceMipLevel);
        uint sourceHeight = MipExtent(source.Height, copy.SourceMipLevel);
        uint sourceDepth = MipExtent(source.Depth, copy.SourceMipLevel);
        uint destinationWidth = MipExtent(destination.Width, copy.DestinationMipLevel);
        uint destinationHeight = MipExtent(destination.Height, copy.DestinationMipLevel);
        uint destinationDepth = MipExtent(destination.Depth, copy.DestinationMipLevel);
        if (copy.SourceX > sourceWidth || copy.Width > sourceWidth - copy.SourceX ||
            copy.SourceY > sourceHeight || copy.Height > sourceHeight - copy.SourceY ||
            copy.SourceZ > sourceDepth || copy.Depth > sourceDepth - copy.SourceZ ||
            copy.DestinationX > destinationWidth || copy.Width > destinationWidth - copy.DestinationX ||
            copy.DestinationY > destinationHeight || copy.Height > destinationHeight - copy.DestinationY ||
            copy.DestinationZ > destinationDepth || copy.Depth > destinationDepth - copy.DestinationZ)
            throw new ArgumentOutOfRangeException(nameof(copy));
    }

    internal static uint MipExtent(uint extent, uint mipLevel) =>
        Math.Max(extent >> checked((int)mipLevel), 1u);
}
