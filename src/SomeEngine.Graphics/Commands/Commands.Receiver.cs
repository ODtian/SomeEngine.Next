using System.Numerics;
using System.Runtime.CompilerServices;

namespace SomeEngine.Graphics;

public partial interface IGraphicsBackend
{
    CommandContext CreateCommandContext(Device device, in CommandContextDesc desc);
    void Begin(CommandContext context, in CommandRecordingDesc desc = default);
    RecordedCommands End(CommandContext context);
    RecordedBundle EndBundle(CommandContext context);
    void Discard(CommandContext context);

    void Barrier(CommandContext context, in MemoryBarrier barrier);
    void Barrier(CommandContext context, in BufferBarrier barrier);
    void Barrier(CommandContext context, in TextureBarrier barrier);
    void Barrier(CommandContext context, in AliasingBarrier barrier);
    void Barrier(CommandContext context, in QueueRelease barrier);
    void Barrier(CommandContext context, in QueueAcquire barrier);

    void CopyBuffer(CommandContext context, in BufferCopy copy);
    void CopyBufferToTexture(CommandContext context, in BufferTextureCopy copy);
    void CopyTextureToBuffer(CommandContext context, in BufferTextureCopy copy);
    void CopyTexture(CommandContext context, in TextureCopy copy);
    void ResolveTexture(CommandContext context, in TextureResolve resolve);
    void ClearBuffer(CommandContext context, Buffer buffer, in BufferRange range, uint value = 0);
    void ClearTexture(
        CommandContext context,
        Texture texture,
        in TextureSubresourceRange range,
        in Vector4 color);
    void ClearDepthStencil(
        CommandContext context,
        Texture texture,
        in TextureSubresourceRange range,
        float depth = 1,
        byte stencil = 0);

    void BeginRendering(CommandContext context, in RenderingDesc desc);
    void EndRendering(CommandContext context);

    void SetPipeline(CommandContext context, Pipeline pipeline);
    void SetPersistentParameterBindings(
        CommandContext context,
        PersistentParameterBindings bindings);
    void SetTransientParameterBindings(
        CommandContext context,
        in ParameterBlockBindings bindings);
    void SetVertexBuffers(
        CommandContext context,
        uint firstSlot,
        ReadOnlySpan<VertexBufferBinding> bindings);
    void SetIndexBuffer(CommandContext context, in IndexBufferBinding binding);
    void SetStreamOutputBuffers(
        CommandContext context,
        uint firstSlot,
        ReadOnlySpan<StreamOutputBufferBinding> bindings);
    void SetViewports(CommandContext context, ReadOnlySpan<Viewport> viewports);
    void SetScissors(CommandContext context, ReadOnlySpan<ScissorRect> scissors);
    void SetBlendConstants(CommandContext context, in Vector4 value);
    void SetStencilReference(CommandContext context, uint value);
    void SetDepthBounds(CommandContext context, float minimum, float maximum);
    void SetDepthBias(CommandContext context, int bias, float clamp, float slopeScaledBias);
    void SetPrimitiveTopology(CommandContext context, PrimitiveTopology topology);
    void SetStripCut(CommandContext context, StripCut stripCut);
    void SetPredication(
        CommandContext context,
        Buffer? buffer,
        ulong offset = 0,
        PredicationOperation operation = PredicationOperation.NotEqualZero);

    void Draw(CommandContext context, in DrawArguments arguments);
    void DrawIndexed(CommandContext context, in DrawIndexedArguments arguments);
    void Dispatch(CommandContext context, in DispatchArguments arguments);
    void ExecuteBundle(CommandContext context, RecordedBundle bundle);

    void BeginEvent(CommandContext context, ReadOnlySpan<byte> utf8Label);
    void EndEvent(CommandContext context);
    void SetMarker(CommandContext context, ReadOnlySpan<byte> utf8Label);

    QueueCompletion Submit(Queue queue, in QueueSubmitDesc desc);
}
