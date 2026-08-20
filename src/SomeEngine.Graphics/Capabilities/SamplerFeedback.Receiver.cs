namespace SomeEngine.Graphics;

public partial interface IGraphicsBackend
{
    SamplerFeedbackTexture CreateSamplerFeedbackTexture(
        Device device,
        in SamplerFeedbackTextureDesc desc);

    SamplerFeedbackUav CreateSamplerFeedbackUav(
        Device device,
        SamplerFeedbackTexture texture,
        in TextureUavDesc desc);

    void ClearSamplerFeedback(
        CommandContext context,
        SamplerFeedbackUav feedback);

    void ResolveSamplerFeedback(
        CommandContext context,
        SamplerFeedbackTexture feedback,
        Buffer destination,
        in BufferRange destinationRange);

    void ResolveSamplerFeedback(
        CommandContext context,
        SamplerFeedbackTexture feedback,
        Texture destination,
        in TextureSubresourceRange destinationRange);
}
