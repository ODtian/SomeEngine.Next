namespace SomeEngine.Graphics.Validation;

public sealed partial class ValidationLayer
{
    private static void ValidateBufferRange(Buffer buffer, ulong offset, ulong size)
    {
        if (size == 0 || offset > buffer.Info.Size || size > buffer.Info.Size - offset)
            throw new ArgumentOutOfRangeException(nameof(size), "The buffer range exceeds the buffer.");
    }

    private static uint MipExtent(uint extent, uint mip) => Math.Max(1u, extent >> (int)mip);

    private static void ValidateTextureRegion(
        TextureInfo info, uint mip, uint layer, TextureAspects aspect,
        uint x, uint y, uint z, uint width, uint height, uint depth)
    {
        if (mip >= info.MipLevelCount || layer >= info.ArrayLayerCount)
            throw new ArgumentOutOfRangeException(nameof(mip));
        TextureAspects available = DefaultValidationAspects(info.Format);
        bool hasStencil = (available & TextureAspects.Stencil) != 0;
        if (aspect is not (TextureAspects.Color or TextureAspects.Depth or TextureAspects.Stencil or
            TextureAspects.Plane0 or TextureAspects.Plane1) ||
            (aspect == TextureAspects.Plane1 && !hasStencil) ||
            (aspect is TextureAspects.Color or TextureAspects.Depth or TextureAspects.Stencil &&
             (available & aspect) == 0))
        {
            throw new ArgumentOutOfRangeException(nameof(aspect));
        }
        uint mipWidth = MipExtent(info.Width, mip);
        uint mipHeight = info.Dimension == TextureDimension.Texture1D ? 1 : MipExtent(info.Height, mip);
        uint mipDepth = info.Dimension == TextureDimension.Texture3D ? MipExtent(info.Depth, mip) : 1;
        if (width == 0 || height == 0 || depth == 0 ||
            x > mipWidth || width > mipWidth - x ||
            y > mipHeight || height > mipHeight - y ||
            z > mipDepth || depth > mipDepth - z)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "The texture copy exceeds the selected mip.");
        }
        if (info.Format >= Format.BC1UNorm &&
            (x % 4 != 0 || y % 4 != 0 ||
             (width % 4 != 0 && x + width != mipWidth) ||
             (height % 4 != 0 && y + height != mipHeight)))
        {
            throw new ArgumentException("The compressed texture copy requires block aligned coordinates.");
        }
    }

    private static TextureInfo TextureInfoFromDescription(in TextureDesc desc) =>
        new(desc.Dimension, desc.Width, desc.Height, desc.Depth, desc.MipLevelCount,
            desc.ArrayLayerCount, desc.SampleCount, desc.Format, desc.Usages,
            MemoryType.DeviceLocal, desc.PermittedViewFormats, 0, 0, 1, 1);

    private void ValidateBufferTextureCopy(in BufferTextureCopy copy)
    {
        TextureInfo info = copy.Texture.Info;
        ValidateTextureRegion(info, copy.MipLevel, copy.ArrayLayer, copy.Aspect,
            copy.X, copy.Y, copy.Z, copy.Width, copy.Height, copy.Depth);
        Device device = copy.Buffer.Device;
        TextureDesc desc = new(info.Dimension, info.Width, info.Height, info.Depth,
            info.MipLevelCount, info.ArrayLayerCount, info.SampleCount, info.Format, info.Usages);
        TextureCopyFootprint footprint = Backend.GetTextureCopyFootprint(device, desc, copy);
        DeviceLimits limits = device.Capabilities.Limits;
        if (copy.BufferOffset % limits.TextureDataPlacementAlignment != 0 ||
            (copy.BufferRowPitch != 0 &&
             (copy.BufferRowPitch % limits.TextureDataPitchAlignment != 0 || copy.BufferRowPitch < footprint.RowSize)) ||
            (copy.BufferImageHeight != 0 && copy.BufferImageHeight < copy.Height))
        {
            throw new ArgumentException("The texture buffer copy has invalid pitch or placement alignment.", nameof(copy));
        }
        BufferRange range = footprint.GetBufferRange(copy);
        ValidateBufferRange(copy.Buffer, range.Offset, range.Size);
    }

    private void ValidateRenderingAttachments(in RenderingDesc desc)
    {
        foreach (ref readonly ColorAttachmentDesc attachment in desc.Colors)
        {
            RequireOnDevice(attachment.View.Device, attachment.View.Description.Texture, "Color texture");
            ValidateRenderingExtent(attachment.View.Description.Texture.Info, attachment.View.Description.Range, desc);
            if (attachment.Load == LoadType.Discard || attachment.Store == StoreType.Discard)
                ValidateDiscardRange(attachment.View.Description.Texture, attachment.View.Description.Range);
            if (attachment.ResolveView is { } resolve)
            {
                RequireOnDevice(attachment.View.Device, resolve, "Color resolve attachment");
                TextureInfo source = attachment.View.Description.Texture.Info;
                TextureInfo target = resolve.Description.Texture.Info;
                if (source.SampleCount <= 1 || target.SampleCount != 1 ||
                    attachment.View.Description.Format != resolve.Description.Format ||
                    attachment.View.Description.Range.ArrayLayerCount != resolve.Description.Range.ArrayLayerCount)
                    throw new ArgumentException("The color resolve attachments have incompatible formats or sample counts.");
                ValidateRenderingExtent(target, resolve.Description.Range, desc);
            }
        }
        if (desc.DepthStencil is { } depthStencil)
            ValidateRenderingExtent(depthStencil.View.Description.Texture.Info, depthStencil.View.Description.Range, desc);
    }

    private static void ValidateRenderingExtent(TextureInfo info, in TextureSubresourceRange range, in RenderingDesc desc)
    {
        uint width = MipExtent(info.Width, range.FirstMipLevel);
        uint height = MipExtent(info.Height, range.FirstMipLevel);
        if (desc.X > width || desc.Width > width - desc.X ||
            desc.Y > height || desc.Height > height - desc.Y)
            throw new ArgumentOutOfRangeException(nameof(desc), "The rendering area exceeds an attachment.");
    }

    private static void ValidateDiscardRange(Texture texture, in TextureSubresourceRange range)
    {
        if (texture.Device.Capabilities.CompileTarget != SlangShaderSharp.SlangCompileTarget.Dxil ||
            texture.Info.Dimension != TextureDimension.Texture3D)
            return;
        for (uint mip = range.FirstMipLevel; mip < range.FirstMipLevel + range.MipLevelCount; mip++)
            if (range.FirstArrayLayer != 0 || range.ArrayLayerCount != MipExtent(texture.Info.Depth, mip))
                throw new NotSupportedException("Direct3D 12 discards select the complete depth of each 3D texture mip.");
    }

    private static void ValidateTextureClearRange(TextureInfo info, in TextureSubresourceRange range, TextureAspects allowed)
    {
        if (range.MipLevelCount == 0 || range.FirstMipLevel >= info.MipLevelCount ||
            range.MipLevelCount > info.MipLevelCount - range.FirstMipLevel || range.ArrayLayerCount == 0)
            throw new ArgumentOutOfRangeException(nameof(range));
        if (range.Aspects == TextureAspects.None || (range.Aspects & ~allowed) != 0)
            throw new ArgumentException("The clear range selects unavailable texture aspects.", nameof(range));
        for (uint mip = range.FirstMipLevel; mip < range.FirstMipLevel + range.MipLevelCount; mip++)
        {
            uint layers = info.Dimension == TextureDimension.Texture3D ? MipExtent(info.Depth, mip) : info.ArrayLayerCount;
            if (range.FirstArrayLayer >= layers || range.ArrayLayerCount > layers - range.FirstArrayLayer)
                throw new ArgumentOutOfRangeException(nameof(range));
        }
    }
}
