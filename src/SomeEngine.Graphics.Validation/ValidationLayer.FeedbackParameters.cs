namespace SomeEngine.Graphics.Validation;

public sealed partial class ValidationLayer
{
    private static void ValidateFeedbackBufferDestination(SamplerFeedbackTexture feedback, Buffer destination,
        in BufferRange destinationRange)
    {
        if (feedback.Description.Type != SamplerFeedbackType.MinimumMip || feedback.Info.ArrayLayerCount != 1)
            throw new ArgumentException("Buffer decoding uses a non-array MinimumMip feedback texture.", nameof(feedback));
        if ((destination.Info.Usages & BufferUsages.CopyDestination) == 0)
            throw new ArgumentException("The destination buffer requires CopyDestination usage.", nameof(destination));
        BufferRange range = destinationRange.Resolve(destination.Info.Size);
        ulong width = (feedback.Info.Width + feedback.Description.MipRegionWidth - 1) / feedback.Description.MipRegionWidth;
        ulong height = (feedback.Info.Height + feedback.Description.MipRegionHeight - 1) / feedback.Description.MipRegionHeight;
        if (range.Size < width * height || range.Offset > uint.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(destinationRange));
    }

    private static void ValidateFeedbackTextureDestination(SamplerFeedbackTexture feedback, Texture destination,
        in TextureSubresourceRange range)
    {
        TextureInfo info = destination.Info;
        uint mipEnd = checked(range.FirstMipLevel + range.MipLevelCount);
        uint layerEnd = checked(range.FirstArrayLayer + range.ArrayLayerCount);
        uint width = (feedback.Info.Width + feedback.Description.MipRegionWidth - 1) / feedback.Description.MipRegionWidth;
        uint height = (feedback.Info.Height + feedback.Description.MipRegionHeight - 1) / feedback.Description.MipRegionHeight;
        if (range.MipLevelCount == 0 || range.ArrayLayerCount == 0 || mipEnd > info.MipLevelCount ||
            layerEnd > info.ArrayLayerCount || layerEnd > feedback.Info.ArrayLayerCount ||
            range.Aspects is not (TextureAspects.Color or TextureAspects.Plane0) || info.Dimension != TextureDimension.Texture2D ||
            info.Format != Format.R8UInt || info.SampleCount != 1 || (info.Usages & TextureUsages.CopyDestination) == 0 ||
            info.Width < width || info.Height < height)
            throw new ArgumentException("The destination texture must contain the decoded R8UInt feedback subresources.", nameof(range));
        if (feedback.Description.Type == SamplerFeedbackType.MinimumMip &&
            (range.FirstMipLevel != 0 || range.MipLevelCount != 1))
            throw new ArgumentException("MinimumMip feedback decodes one mip level.", nameof(range));
        if (feedback.Description.Type == SamplerFeedbackType.MipRegionUsed && mipEnd > feedback.Info.MipLevelCount)
            throw new ArgumentException("The destination mip range requires matching feedback mips.", nameof(range));
    }
}
