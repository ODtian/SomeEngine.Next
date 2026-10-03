namespace SomeEngine.RenderGraph;

using System.Runtime.InteropServices;

public ref partial struct RenderGraphFrame
{
    private RenderGraphFrameState? _state;
    private readonly ulong _identity;

    internal RenderGraphFrame(RenderGraphFrameState state)
    {
        _state = state;
        _identity = state.Identity;
    }

    private RenderGraphFrameState State
    {
        get
        {
            RenderGraphFrameState state = _state
                ?? throw new InvalidOperationException("The render graph frame is no longer active.");
            state.EnsureLease(_identity);
            return state;
        }
    }

    public ulong StructureVersion => State.StructureVersion;

    /// <summary>Identifies this CPU frame authoring lease.</summary>
    public ulong FrameIdentity => State.Identity;

    internal RenderGraph Owner => State.Graph;

    /// <summary>
    /// Retires an object after every frame submitted before this frame began has completed.
    /// The current frame must not reference <paramref name="value"/>.
    /// </summary>
    public void RetireAfterSubmittedFrames(IDisposable value)
    {
        ArgumentNullException.ThrowIfNull(value);
        State.RetireAfterSubmittedFrames(value);
    }

    /// <summary>
    /// Retires an object after this frame finishes and every queue submission visible at that
    /// point has completed. If this frame is cancelled, only earlier submitted work is awaited.
    /// </summary>
    public void RetireAfterFrame(IDisposable value)
    {
        ArgumentNullException.ThrowIfNull(value);
        State.RetireAfterFrame(value);
    }

    public void ObserveExecution(IRenderGraphFrameExecutionObserver observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        State.ObserveExecution(observer);
    }

    public PassDefinition GetPass(GraphPassId pass) => State.GetPass(pass);

    public void SetPassEnabled(GraphPassId pass, bool enabled) => State.SetPassEnabled(pass, enabled);

    public void SetPassOptions(GraphPassId pass, in PassOptions options) => State.SetPassOptions(pass, options);

    public void SetPassData<T>(GraphPassId pass, in T value) => State.SetPassData(pass, value);

    /// <summary>
    /// Updates immutable command data, leaving reusable commands unchanged when the value
    /// compares equal. Referenced data must also be immutable or declared through graph inputs.
    /// </summary>
    public void SetPassDataIfChanged<T>(GraphPassId pass, in T value) where T : struct, IEquatable<T> =>
        State.SetPassData(pass, value, compareValue: true);

    public void SetBufferRange(GraphBufferAccessId access, in BufferRange range) =>
        State.SetBufferRange(access, range);

    public void SetBufferRange(GraphBufferId buffer, in BufferRange range) =>
        State.SetBufferViewRanges(buffer, range);

    public void SetBufferRange(GraphBufferCbvId view, in BufferRange range) =>
        State.SetBufferRange(view.Value, GraphViewKind.BufferCbv, range);

    public void SetBufferRange(GraphBufferSrvId view, in BufferRange range) =>
        State.SetBufferRange(view.Value, GraphViewKind.BufferSrv, range);

    public void SetBufferRange(GraphBufferUavId view, in BufferRange range) =>
        State.SetBufferRange(view.Value, GraphViewKind.BufferUav, range);

    public void SetTextureRange(GraphTextureAccessId access, in TextureSubresourceRange range) =>
        State.SetTextureRange(access, range);

    public void BindExternalBuffer(
        GraphBufferId slot,
        Buffer buffer,
        scoped ReadOnlySpan<BufferBoundaryState> boundaryStates) =>
        State.BindExternalBuffer(slot, buffer, boundaryStates);

    public void BindExternalTexture(
        GraphTextureId slot,
        Texture texture,
        scoped ReadOnlySpan<TextureBoundaryState> boundaryStates) =>
        State.BindExternalTexture(slot, texture, boundaryStates);
    public void BindExternalTexture(
        GraphTextureId slot,
        in SwapchainImage image,
        Queue presentQueue) =>
        State.BindExternalTexture(slot, image, presentQueue);

    public GraphBufferId CreateBuffer(in BufferDesc description, MemoryType memoryType = MemoryType.DeviceLocal) =>
        State.AddBuffer(description, memoryType, RenderGraphResourceOwnership.GraphOwned,
            RenderGraphResourceLifetime.PerFrame);

    public GraphBufferSlice Upload(
        scoped ReadOnlySpan<byte> data,
        BufferUsages usages,
        string? label = null) =>
        State.Upload(data, usages, label, 4);

    /// <summary>Copies multiple byte segments into one contiguous upload range for this frame.</summary>
    public GraphBufferSlice Upload(
        scoped ReadOnlySpan<ReadOnlyMemory<byte>> segments,
        BufferUsages usages,
        string? label = null) =>
        State.Upload(segments, usages, label);

    /// <summary>
    /// Writes bytes directly into this frame's mapped upload range. The writer runs before
    /// this method returns and must fill the supplied byteCount bytes. Alignment padding is cleared.
    /// </summary>
    public GraphBufferSlice Upload<TData>(
        int byteCount,
        BufferUsages usages,
        TData data,
        System.Buffers.SpanAction<byte, TData> write,
        string? label = null) =>
        State.Upload(byteCount, usages, data, write, label);

    /// <summary>
    /// Writes a declared upload buffer for this frame. Each in-flight frame keeps its own
    /// mapped buffer, so updating bytes does not replace the buffer used by its commands.
    /// Data must cover the declaration; the GPU may only read this buffer.
    /// </summary>
    public void SetBufferData(GraphBufferId buffer, scoped ReadOnlySpan<byte> data) =>
        State.SetBufferData(buffer, data);

    public GraphBufferSlice Upload<T>(
        scoped ReadOnlySpan<T> data,
        BufferUsages usages,
        string? label = null)
        where T : unmanaged =>
        State.Upload(MemoryMarshal.AsBytes(data), usages, label, checked((uint)Unsafe.SizeOf<T>()));

    public GraphBufferSlice Upload<T>(
        in T value,
        BufferUsages usages,
        string? label = null)
        where T : unmanaged
    {
        ReadOnlySpan<T> values = MemoryMarshal.CreateReadOnlySpan(ref Unsafe.AsRef(in value), 1);
        return State.Upload(MemoryMarshal.AsBytes(values), usages, label, checked((uint)Unsafe.SizeOf<T>()));
    }

    public GraphTextureId CreateTexture(in TextureDesc description) =>
        State.AddTexture(description, RenderGraphResourceOwnership.GraphOwned,
            RenderGraphResourceLifetime.PerFrame);

    public GraphBufferId Import(Buffer buffer, scoped ReadOnlySpan<BufferBoundaryState> boundaryStates) =>
        State.Import(buffer, boundaryStates);

    public GraphTextureId Import(Texture texture, scoped ReadOnlySpan<TextureBoundaryState> boundaryStates) =>
        State.Import(texture, boundaryStates);

    public GraphTextureId Import(in SwapchainImage image, Queue presentQueue) =>
        State.Import(image, presentQueue);

    public GraphBufferId GetImported(Buffer buffer) => State.GetImported(buffer);

    public GraphTextureId GetImported(Texture texture) => State.GetImported(texture);

    public GraphBufferCbvId CreateBufferCbv(
        GraphBufferSlice buffer, BufferRange? range = null, string? label = null) =>
        CreateBufferCbv(buffer.Buffer, buffer.ResolveRange(range), label);

    public GraphBufferSrvId CreateBufferSrv(
        GraphBufferSlice buffer, BufferRange? range = null, Format? format = null,
        uint structureStride = 0, string? label = null) =>
        CreateBufferSrv(buffer.Buffer, buffer.ResolveRange(range), format, structureStride, label);

    public GraphBufferCbvId CreateBufferCbv(
        GraphBufferId buffer,
        BufferRange? range = null,
        string? label = null) =>
        new(State.AddBufferView(GraphViewKind.BufferCbv, buffer,
            range ?? BufferRange.Whole, null, 0, default, 0, label));

    public GraphBufferSrvId CreateBufferSrv(
        GraphBufferId buffer,
        BufferRange? range = null,
        Format? format = null,
        uint structureStride = 0,
        string? label = null) =>
        new(State.AddBufferView(GraphViewKind.BufferSrv, buffer,
            range ?? BufferRange.Whole, format, structureStride, default, 0, label));

    public GraphBufferUavId CreateBufferUav(
        GraphBufferId buffer,
        BufferRange? range = null,
        Format? format = null,
        uint structureStride = 0,
        GraphBufferId counterBuffer = default,
        ulong counterOffset = 0,
        string? label = null) =>
        new(State.AddBufferView(GraphViewKind.BufferUav, buffer,
            range ?? BufferRange.Whole, format, structureStride,
            counterBuffer.Value, counterOffset, label));

    public GraphTextureSrvId CreateTextureSrv(
        GraphTextureId texture,
        TextureSubresourceRange? range = null,
        Format? format = null,
        TextureViewDimension? dimension = null,
        string? label = null) =>
        new(State.AddTextureView(GraphViewKind.TextureSrv, texture,
            State.ResolveTextureRange(texture, range),
            State.ResolveTextureFormat(texture, format),
            State.ResolveTextureViewDimension(texture, dimension),
            false, false, label));

    public GraphTextureUavId CreateTextureUav(
        GraphTextureId texture,
        TextureSubresourceRange? range = null,
        Format? format = null,
        TextureViewDimension? dimension = null,
        string? label = null) =>
        new(State.AddTextureView(GraphViewKind.TextureUav, texture,
            State.ResolveTextureRange(texture, range),
            State.ResolveTextureFormat(texture, format),
            State.ResolveTextureViewDimension(texture, dimension),
            false, false, label));

    public GraphColorAttachmentViewId CreateColorAttachmentView(
        GraphTextureId texture,
        TextureSubresourceRange? range = null,
        Format? format = null,
        TextureViewDimension? dimension = null,
        string? label = null) =>
        new(State.AddTextureView(GraphViewKind.ColorAttachment, texture,
            State.ResolveTextureRange(texture, range),
            State.ResolveTextureFormat(texture, format),
            State.ResolveTextureViewDimension(texture, dimension),
            false, false, label));

    public GraphDepthStencilViewId CreateDepthStencilView(
        GraphTextureId texture,
        TextureSubresourceRange? range = null,
        Format? format = null,
        TextureViewDimension? dimension = null,
        bool readOnlyDepth = false,
        bool readOnlyStencil = false,
        string? label = null) =>
        new(State.AddTextureView(GraphViewKind.DepthStencil, texture,
            State.ResolveTextureRange(texture, range),
            State.ResolveTextureFormat(texture, format),
            State.ResolveTextureViewDimension(texture, dimension),
            readOnlyDepth, readOnlyStencil, label));

    public GraphPassId AddRasterPass<TState>(
        string label,
        in PassQueueSelection queue,
        in TState state,
        in PassOptions options,
        PassDeclaration<TState> declaration,
        RasterFrameCallback<TState> callback,
        GraphRegionId region = default) =>
        State.AddRasterPass(label, queue, state, options, declaration, callback, region);

    public GraphPassId AddRasterPass<TState>(
        string label,
        in PassQueueSelection queue,
        in TState state,
        in PassOptions options,
        Pipeline pipeline,
        VariableLayoutReflection parameterLayout,
        ReadOnlySpan<GraphParameterResourceBinding> parameterBindings,
        PassDeclaration<TState> declaration,
        RasterFrameCallback<TState> callback,
        GraphRegionId region = default) =>
        State.AddRasterPass(label, queue, state, options, pipeline,
            parameterLayout, parameterBindings, declaration, callback, region);

    public GraphPassId AddComputePass<TState>(
        string label,
        in PassQueueSelection queue,
        in TState state,
        in PassOptions options,
        PassDeclaration<TState> declaration,
        ComputeFrameCallback<TState> callback,
        GraphRegionId region = default) =>
        State.AddComputePass(label, queue, state, options, declaration, callback, region);

    public GraphPassId AddComputePass<TState>(
        string label,
        in PassQueueSelection queue,
        in TState state,
        in PassOptions options,
        Pipeline pipeline,
        VariableLayoutReflection parameterLayout,
        ReadOnlySpan<GraphParameterResourceBinding> parameterBindings,
        PassDeclaration<TState> declaration,
        ComputeFrameCallback<TState> callback,
        GraphRegionId region = default) =>
        State.AddComputePass(label, queue, state, options, pipeline,
            parameterLayout, parameterBindings, declaration, callback, region);

    public GraphPassId AddCopyPass<TState>(
        string label,
        in PassQueueSelection queue,
        in TState state,
        in PassOptions options,
        PassDeclaration<TState> declaration,
        CopyFrameCallback<TState> callback,
        GraphRegionId region = default) =>
        State.AddCopyPass(label, queue, state, options, declaration, callback, region);

    public GraphPassId AddGeneralPass<TState>(
        string label,
        in PassQueueSelection queue,
        in TState state,
        in PassOptions options,
        PassDeclaration<TState> declaration,
        GeneralFrameCallback<TState> callback,
        GraphRegionId region = default) =>
        State.AddGeneralPass(label, queue, state, options, declaration, callback, region);

    public void OrderAfter(GraphPassId pass, GraphPassId predecessor) => State.OrderAfter(pass, predecessor);

    public int Execute(Span<QueueCompletion> destination)
    {
        RenderGraphFrameState state = State;
        int result = state.Execute(_identity, destination);
        _state = null;
        return result;
    }

    public void Dispose()
    {
        RenderGraphFrameState? state = _state;
        _state = null;
        state?.Cancel(_identity);
    }

    public void SetBufferInput(GraphBufferInput input, GraphBufferSlice value) => State.SetBufferInput(input, value);

    public void SetTextureInput(GraphTextureInput input, GraphTextureId value) => State.SetTextureInput(input, value);

    /// <summary>Supplies an existing RHI binding after importing its resources into this frame.</summary>
    public void SetParameterInput(GraphParameterInput input, ResourceBinding value) => State.SetParameterInput(input, value);
}
