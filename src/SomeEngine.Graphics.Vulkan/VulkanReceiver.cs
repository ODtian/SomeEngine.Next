namespace SomeEngine.Graphics.Vulkan;

internal sealed unsafe partial class VulkanBackend
{
    bool IGraphicsBackend.TryEnumerateAdapters(
        in AdapterEnumerationOptions options,
        Span<AdapterInfo> destination,
        out int requiredCount) =>
        TryEnumerateAdapters(options, destination, out requiredCount);

    RhiDevice IGraphicsBackend.CreateDevice(in DeviceDesc desc) => CreateDevice(desc);

    RhiQueue IGraphicsBackend.GetQueue(RhiDevice device, QueueType type, uint index) =>
        GetQueue(device, type, index);

    void IGraphicsBackend.CollectCompleted(RhiDevice device) => CollectCompleted(device);

    bool IGraphicsBackend.IsComplete(in QueueCompletion completion) => IsComplete(completion);
    WaitStatus IGraphicsBackend.WaitCpu(in QueueCompletion completion, TimeSpan timeout) =>
        WaitCpu(completion, timeout);

    RhiMemoryRequirements IGraphicsBackend.GetBufferMemoryRequirements(
        RhiDevice device,
        in BufferDesc desc,
        MemoryType memoryType) =>
        GetBufferMemoryRequirements(device, desc, memoryType);

    RhiMemoryRequirements IGraphicsBackend.GetTextureMemoryRequirements(
        RhiDevice device,
        in TextureDesc desc) =>
        GetTextureMemoryRequirements(device, desc);

    TextureCopyFootprint IGraphicsBackend.GetTextureCopyFootprint(
        RhiDevice device,
        in TextureDesc desc,
        in BufferTextureCopy copy,
        ulong requestedBufferOffset) =>
        GetTextureCopyFootprint(device, desc, copy, requestedBufferOffset);

    RhiHeap IGraphicsBackend.CreateHeap(RhiDevice device, in HeapDesc desc) =>
        CreateHeap(device, desc);

    RhiBuffer IGraphicsBackend.CreateBuffer(
        RhiDevice device,
        in BufferDesc desc,
        MemoryType memoryType) =>
        CreateBuffer(device, desc, memoryType);

    RhiBuffer IGraphicsBackend.CreatePlacedBuffer(
        RhiDevice device,
        RhiHeap heap,
        ulong offset,
        in BufferDesc desc) =>
        CreatePlacedBuffer(device, heap, offset, desc);

    RhiTexture IGraphicsBackend.CreateTexture(RhiDevice device, in TextureDesc desc) =>
        CreateTexture(device, desc);

    RhiTexture IGraphicsBackend.CreatePlacedTexture(
        RhiDevice device,
        RhiHeap heap,
        ulong offset,
        in TextureDesc desc) =>
        CreatePlacedTexture(device, heap, offset, desc);

    BufferCbv IGraphicsBackend.CreateBufferCbv(RhiDevice device, in BufferCbvDesc desc) =>
        CreateBufferCbv(device, desc);
    BufferSrv IGraphicsBackend.CreateBufferSrv(RhiDevice device, in BufferSrvDesc desc) =>
        CreateBufferSrv(device, desc);
    BufferUav IGraphicsBackend.CreateBufferUav(RhiDevice device, in BufferUavDesc desc) =>
        CreateBufferUav(device, desc);
    TextureSrv IGraphicsBackend.CreateTextureSrv(RhiDevice device, in TextureSrvDesc desc) =>
        CreateTextureSrv(device, desc);
    TextureUav IGraphicsBackend.CreateTextureUav(RhiDevice device, in TextureUavDesc desc) =>
        CreateTextureUav(device, desc);
    ColorAttachmentView IGraphicsBackend.CreateColorAttachmentView(
        RhiDevice device,
        in ColorAttachmentViewDesc desc) =>
        CreateColorAttachmentView(device, desc);
    DepthStencilView IGraphicsBackend.CreateDepthStencilView(
        RhiDevice device,
        in DepthStencilViewDesc desc) =>
        CreateDepthStencilView(device, desc);
    RhiSampler IGraphicsBackend.CreateSampler(RhiDevice device, in SamplerDesc desc) =>
        CreateSampler(device, desc);
    MappedBuffer IGraphicsBackend.Map(RhiBuffer buffer, MapType type, in BufferRange range) =>
        Map(buffer, type, range);

    CommandContext IGraphicsBackend.CreateCommandContext(
        RhiDevice device,
        in CommandContextDesc desc) =>
        CreateCommandContext(device, desc);
    void IGraphicsBackend.Begin(CommandContext context, in CommandRecordingDesc desc) => Begin(context, desc);
    RecordedCommands IGraphicsBackend.End(CommandContext context) => End(context);
    RecordedBundle IGraphicsBackend.EndBundle(CommandContext context) => EndBundle(context);
    void IGraphicsBackend.Discard(CommandContext context) => Discard(context);
    void IGraphicsBackend.Barrier(CommandContext context, in MemoryBarrier barrier) => Barrier(context, barrier);
    void IGraphicsBackend.Barrier(CommandContext context, in BufferBarrier barrier) => Barrier(context, barrier);
    void IGraphicsBackend.Barrier(CommandContext context, in TextureBarrier barrier) => Barrier(context, barrier);
    void IGraphicsBackend.Barrier(CommandContext context, in AliasingBarrier barrier) => Barrier(context, barrier);
    void IGraphicsBackend.Barrier(CommandContext context, in QueueRelease barrier) => Barrier(context, barrier);
    void IGraphicsBackend.Barrier(CommandContext context, in QueueAcquire barrier) => Barrier(context, barrier);
    void IGraphicsBackend.Barrier(CommandContext context, in BarrierBatch barriers) => Barrier(context, barriers);
    void IGraphicsBackend.CopyBuffer(CommandContext context, in BufferCopy copy) => CopyBuffer(context, copy);
    void IGraphicsBackend.CopyBufferToTexture(CommandContext context, in BufferTextureCopy copy) => CopyBufferToTexture(context, copy);
    void IGraphicsBackend.CopyTextureToBuffer(CommandContext context, in BufferTextureCopy copy) => CopyTextureToBuffer(context, copy);
    void IGraphicsBackend.CopyTexture(CommandContext context, in TextureCopy copy) => CopyTexture(context, copy);
    void IGraphicsBackend.ResolveTexture(CommandContext context, in TextureResolve resolve) => ResolveTexture(context, resolve);
    void IGraphicsBackend.ClearBuffer(CommandContext context, RhiBuffer buffer, in BufferRange range, uint value) => ClearBuffer(context, buffer, range, value);
    void IGraphicsBackend.ClearTexture(CommandContext context, RhiTexture texture, in TextureSubresourceRange range, in Vector4 color) => ClearTexture(context, texture, range, color);
    void IGraphicsBackend.ClearDepthStencil(CommandContext context, RhiTexture texture, in TextureSubresourceRange range, float depth, byte stencil) => ClearDepthStencil(context, texture, range, depth, stencil);
    void IGraphicsBackend.BeginRendering(CommandContext context, in RenderingDesc desc) => BeginRendering(context, desc);
    void IGraphicsBackend.EndRendering(CommandContext context) => EndRendering(context);
    void IGraphicsBackend.SetPipeline(CommandContext context, Pipeline pipeline) => SetPipeline(context, pipeline);
    void IGraphicsBackend.SetPersistentParameterBindings(CommandContext context, PersistentParameterBindings bindings) => SetPersistentParameterBindings(context, bindings);
    void IGraphicsBackend.SetTransientParameterBindings(CommandContext context, in ParameterBlockBindings bindings) => SetTransientParameterBindings(context, bindings);
    void IGraphicsBackend.SetVertexBuffers(CommandContext context, uint firstSlot, ReadOnlySpan<VertexBufferBinding> bindings) => SetVertexBuffers(context, firstSlot, bindings);
    void IGraphicsBackend.SetIndexBuffer(CommandContext context, in IndexBufferBinding binding) => SetIndexBuffer(context, binding);
    void IGraphicsBackend.SetStreamOutputBuffers(CommandContext context, uint firstSlot, ReadOnlySpan<StreamOutputBufferBinding> bindings) => SetStreamOutputBuffers(context, firstSlot, bindings);
    void IGraphicsBackend.SetViewports(CommandContext context, ReadOnlySpan<Viewport> viewports) => SetViewports(context, viewports);
    void IGraphicsBackend.SetScissors(CommandContext context, ReadOnlySpan<ScissorRect> scissors) => SetScissors(context, scissors);
    void IGraphicsBackend.SetBlendConstants(CommandContext context, in Vector4 value) => SetBlendConstants(context, value);
    void IGraphicsBackend.SetStencilReference(CommandContext context, uint value) => SetStencilReference(context, value);
    void IGraphicsBackend.SetDepthBounds(CommandContext context, float minimum, float maximum) => SetDepthBounds(context, minimum, maximum);
    void IGraphicsBackend.SetDepthBias(CommandContext context, int bias, float clamp, float slopeScaledBias) => SetDepthBias(context, bias, clamp, slopeScaledBias);
    void IGraphicsBackend.SetPrimitiveTopology(CommandContext context, SomeEngine.Graphics.PrimitiveTopology topology) => SetPrimitiveTopology(context, topology);
    void IGraphicsBackend.SetStripCut(CommandContext context, StripCut stripCut) => SetStripCut(context, stripCut);
    void IGraphicsBackend.SetPredication(CommandContext context, RhiBuffer? buffer, ulong offset, PredicationOperation operation) => SetPredication(context, buffer, offset, operation);
    void IGraphicsBackend.Draw(CommandContext context, in DrawArguments arguments) => Draw(context, arguments);
    void IGraphicsBackend.DrawIndexed(CommandContext context, in DrawIndexedArguments arguments) => DrawIndexed(context, arguments);
    void IGraphicsBackend.Dispatch(CommandContext context, in DispatchArguments arguments) => Dispatch(context, arguments);
    void IGraphicsBackend.ExecuteBundle(CommandContext context, RecordedBundle bundle) => ExecuteBundle(context, bundle);
    void IGraphicsBackend.BeginEvent(CommandContext context, ReadOnlySpan<byte> utf8Label) => BeginEvent(context, utf8Label);
    void IGraphicsBackend.EndEvent(CommandContext context) => EndEvent(context);
    void IGraphicsBackend.SetMarker(CommandContext context, ReadOnlySpan<byte> utf8Label) => SetMarker(context, utf8Label);
    QueueCompletion IGraphicsBackend.Submit(RhiQueue queue, in QueueSubmitDesc desc) => Submit(queue, desc);

    Pipeline IGraphicsBackend.CreateGraphicsPipeline(RhiDevice device, in GraphicsPipelineDesc desc, SomeEngine.Graphics.PipelineCache? cache) => CreateGraphicsPipeline(device, desc, cache);
    Task<Pipeline> IGraphicsBackend.CreateGraphicsPipelineAsync(RhiDevice device, in GraphicsPipelineDesc desc, SomeEngine.Graphics.PipelineCache? cache) => CreateGraphicsPipelineAsync(device, desc, cache);
    Pipeline IGraphicsBackend.CreateComputePipeline(RhiDevice device, in ComputePipelineDesc desc, SomeEngine.Graphics.PipelineCache? cache) => CreateComputePipeline(device, desc, cache);
    Task<Pipeline> IGraphicsBackend.CreateComputePipelineAsync(RhiDevice device, in ComputePipelineDesc desc, SomeEngine.Graphics.PipelineCache? cache) => CreateComputePipelineAsync(device, desc, cache);

    DescriptorTable IGraphicsBackend.CreateDescriptorTable(RhiDevice device, ReadOnlySpan<DescriptorSlotDesc> slots, string? label, uint nodeIndex, CancellationToken cancellationToken) => CreateDescriptorTable(device, slots, label, nodeIndex, cancellationToken);
    DescriptorIndex IGraphicsBackend.GetDescriptorIndex(DescriptorTable table, uint slot) => GetDescriptorIndex(table, slot);
    void IGraphicsBackend.WriteDescriptor(DescriptorTable table, uint slot, in ResourceBinding value) => WriteDescriptor(table, slot, value);
    PersistentParameterBindings IGraphicsBackend.CreatePersistentParameterBindings(RhiDevice device, Pipeline pipeline, in ParameterBlockBindings bindings, string? label) => CreatePersistentParameterBindings(device, pipeline, bindings, label);
    void IGraphicsBackend.UpdatePersistentParameterBindings(PersistentParameterBindings destination, in ParameterBlockBindings bindings) => UpdatePersistentParameterBindings(destination, bindings);
    void IGraphicsBackend.PublishDescriptors(RhiDevice device, uint nodeIndex, CancellationToken cancellationToken) => PublishDescriptors(device, nodeIndex, cancellationToken);
}
