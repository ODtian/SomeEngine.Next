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

public sealed partial class Graphics<TBackend>
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public CommandContext CreateCommandContext(Device device, in CommandContextDesc desc) =>
        Receiver.CreateCommandContext(device, desc);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Begin(CommandContext context, in CommandRecordingDesc desc = default) =>
        Receiver.Begin(context, desc);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public RecordedCommands End(CommandContext context) => Receiver.End(context);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public RecordedBundle EndBundle(CommandContext context) => Receiver.EndBundle(context);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Discard(CommandContext context) => Receiver.Discard(context);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Barrier(CommandContext context, in MemoryBarrier barrier) =>
        Receiver.Barrier(context, barrier);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Barrier(CommandContext context, in BufferBarrier barrier) =>
        Receiver.Barrier(context, barrier);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Barrier(CommandContext context, in TextureBarrier barrier) =>
        Receiver.Barrier(context, barrier);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Barrier(CommandContext context, in AliasingBarrier barrier) =>
        Receiver.Barrier(context, barrier);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Barrier(CommandContext context, in QueueRelease barrier) =>
        Receiver.Barrier(context, barrier);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Barrier(CommandContext context, in QueueAcquire barrier) =>
        Receiver.Barrier(context, barrier);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void CopyBuffer(CommandContext context, in BufferCopy copy) =>
        Receiver.CopyBuffer(context, copy);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void CopyBufferToTexture(CommandContext context, in BufferTextureCopy copy) =>
        Receiver.CopyBufferToTexture(context, copy);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void CopyTextureToBuffer(CommandContext context, in BufferTextureCopy copy) =>
        Receiver.CopyTextureToBuffer(context, copy);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void CopyTexture(CommandContext context, in TextureCopy copy) =>
        Receiver.CopyTexture(context, copy);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ResolveTexture(CommandContext context, in TextureResolve resolve) =>
        Receiver.ResolveTexture(context, resolve);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ClearBuffer(CommandContext context, Buffer buffer, in BufferRange range, uint value = 0) =>
        Receiver.ClearBuffer(context, buffer, range, value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ClearTexture(
        CommandContext context,
        Texture texture,
        in TextureSubresourceRange range,
        in Vector4 color) =>
        Receiver.ClearTexture(context, texture, range, color);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ClearDepthStencil(
        CommandContext context,
        Texture texture,
        in TextureSubresourceRange range,
        float depth = 1,
        byte stencil = 0) =>
        Receiver.ClearDepthStencil(context, texture, range, depth, stencil);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void BeginRendering(CommandContext context, in RenderingDesc desc) =>
        Receiver.BeginRendering(context, desc);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void EndRendering(CommandContext context) => Receiver.EndRendering(context);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetPipeline(CommandContext context, Pipeline pipeline) =>
        Receiver.SetPipeline(context, pipeline);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetPersistentParameterBindings(
        CommandContext context,
        PersistentParameterBindings bindings) =>
        Receiver.SetPersistentParameterBindings(context, bindings);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetTransientParameterBindings(
        CommandContext context,
        in ParameterBlockBindings bindings) =>
        Receiver.SetTransientParameterBindings(context, bindings);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetVertexBuffers(
        CommandContext context,
        uint firstSlot,
        ReadOnlySpan<VertexBufferBinding> bindings) =>
        Receiver.SetVertexBuffers(context, firstSlot, bindings);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetIndexBuffer(CommandContext context, in IndexBufferBinding binding) =>
        Receiver.SetIndexBuffer(context, binding);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetStreamOutputBuffers(
        CommandContext context,
        uint firstSlot,
        ReadOnlySpan<StreamOutputBufferBinding> bindings) =>
        Receiver.SetStreamOutputBuffers(context, firstSlot, bindings);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetViewports(CommandContext context, ReadOnlySpan<Viewport> viewports) =>
        Receiver.SetViewports(context, viewports);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetScissors(CommandContext context, ReadOnlySpan<ScissorRect> scissors) =>
        Receiver.SetScissors(context, scissors);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetBlendConstants(CommandContext context, in Vector4 value) =>
        Receiver.SetBlendConstants(context, value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetStencilReference(CommandContext context, uint value) =>
        Receiver.SetStencilReference(context, value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetDepthBounds(CommandContext context, float minimum, float maximum) =>
        Receiver.SetDepthBounds(context, minimum, maximum);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetDepthBias(CommandContext context, int bias, float clamp, float slopeScaledBias) =>
        Receiver.SetDepthBias(context, bias, clamp, slopeScaledBias);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetPrimitiveTopology(CommandContext context, PrimitiveTopology topology) =>
        Receiver.SetPrimitiveTopology(context, topology);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetStripCut(CommandContext context, StripCut stripCut) =>
        Receiver.SetStripCut(context, stripCut);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetPredication(
        CommandContext context,
        Buffer? buffer,
        ulong offset = 0,
        PredicationOperation operation = PredicationOperation.NotEqualZero) =>
        Receiver.SetPredication(context, buffer, offset, operation);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Draw(CommandContext context, in DrawArguments arguments) =>
        Receiver.Draw(context, arguments);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void DrawIndexed(CommandContext context, in DrawIndexedArguments arguments) =>
        Receiver.DrawIndexed(context, arguments);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Dispatch(CommandContext context, in DispatchArguments arguments) =>
        Receiver.Dispatch(context, arguments);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ExecuteBundle(CommandContext context, RecordedBundle bundle) =>
        Receiver.ExecuteBundle(context, bundle);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void BeginEvent(CommandContext context, ReadOnlySpan<byte> utf8Label) =>
        Receiver.BeginEvent(context, utf8Label);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void EndEvent(CommandContext context) => Receiver.EndEvent(context);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetMarker(CommandContext context, ReadOnlySpan<byte> utf8Label) =>
        Receiver.SetMarker(context, utf8Label);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public QueueCompletion Submit(Queue queue, in QueueSubmitDesc desc) =>
        Receiver.Submit(queue, desc);
}
