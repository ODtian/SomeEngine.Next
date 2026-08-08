using System.Runtime.CompilerServices;

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

public sealed partial class Graphics<TBackend>
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public SamplerFeedbackTexture CreateSamplerFeedbackTexture(
        Device device,
        in SamplerFeedbackTextureDesc desc) =>
        Receiver.CreateSamplerFeedbackTexture(device, desc);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public SamplerFeedbackUav CreateSamplerFeedbackUav(
        Device device,
        SamplerFeedbackTexture texture,
        in TextureUavDesc desc) =>
        Receiver.CreateSamplerFeedbackUav(device, texture, desc);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ClearSamplerFeedback(
        CommandContext context,
        SamplerFeedbackUav feedback) =>
        Receiver.ClearSamplerFeedback(context, feedback);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ResolveSamplerFeedback(
        CommandContext context,
        SamplerFeedbackTexture feedback,
        Buffer destination,
        in BufferRange destinationRange) =>
        Receiver.ResolveSamplerFeedback(context, feedback, destination, destinationRange);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ResolveSamplerFeedback(
        CommandContext context,
        SamplerFeedbackTexture feedback,
        Texture destination,
        in TextureSubresourceRange destinationRange) =>
        Receiver.ResolveSamplerFeedback(context, feedback, destination, destinationRange);
}
