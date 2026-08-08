namespace SomeEngine.Graphics;

public enum SamplerFeedbackTier : byte
{
    Tier0_9,
    Tier1_0,
}

public sealed class SamplerFeedback : DeviceCapability
{
    private readonly Format[] _supportedFormats;

    internal SamplerFeedback(
        Device device,
        SamplerFeedbackTier tier,
        ReadOnlySpan<Format> supportedFormats,
        uint minimumMipRegionWidth,
        uint minimumMipRegionHeight,
        ulong feedbackMapAlignment)
        : base(device)
    {
        Tier = tier;
        _supportedFormats = supportedFormats.ToArray();
        MinimumMipRegionWidth = minimumMipRegionWidth;
        MinimumMipRegionHeight = minimumMipRegionHeight;
        FeedbackMapAlignment = feedbackMapAlignment;
    }

    public SamplerFeedbackTier Tier { get; }
    public ReadOnlySpan<Format> SupportedFormats => _supportedFormats;
    public uint MinimumMipRegionWidth { get; }
    public uint MinimumMipRegionHeight { get; }
    public ulong FeedbackMapAlignment { get; }
}

public enum SamplerFeedbackType : byte
{
    MinimumMip,
    MipRegionUsed,
}

public readonly record struct SamplerFeedbackTextureDesc(
    Texture SampledTexture,
    SamplerFeedbackType Type,
    uint MipRegionWidth,
    uint MipRegionHeight,
    string? Label = null);

public abstract class SamplerFeedbackTexture : Texture
{
    internal SamplerFeedbackTexture(
        Device device,
        TextureInfo info,
        in SamplerFeedbackTextureDesc description)
        : base(
            device,
            null,
            info,
            PipelineSync.None,
            ResourceAccess.NoAccess,
            TextureLayout.Undefined,
            description.Label)
    {
        Description = description;
    }

    public SamplerFeedbackTextureDesc Description { get; }
    public Texture SampledTexture => Description.SampledTexture;
}

public abstract class SamplerFeedbackUav : TextureUav
{
    internal SamplerFeedbackUav(
        Device device,
        in TextureUavDesc description,
        Texture sampledTexture)
        : base(device, description)
    {
        SampledTexture = sampledTexture;
    }

    public Texture SampledTexture { get; }
}
