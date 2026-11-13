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
            state.EnsureFrameActive(_identity);
            return state;
        }
    }

    public ulong StructureVersion => State.StructureVersion;

    /// <summary>Identifies this CPU frame authoring lease.</summary>
    public ulong FrameIdentity => State.Identity;

    internal RenderGraph Graph => State.Graph;

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

    /// <summary>Returns the object this feature uses to stage data in the current frame.</summary>
    public T GetFrameData<T>(object feature, Func<T> create) where T : class =>
        State.GetFrameData(feature, create);

    public PassDefinition GetPass(GraphPassId pass) => State.GetPass(pass);

    public void SetPassEnabled(GraphPassId pass, bool enabled) => State.SetPassEnabled(pass, enabled);

    /// <summary>Includes the enable state of the pass's containing regions.</summary>
    public bool IsPassEnabled(GraphPassId pass) => State.IsPassEnabled(pass);

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

    /// <summary>Sets the accessible ranges for every read descriptor bound to this buffer in this frame.</summary>
    public void SetBufferRanges(GraphBufferId buffer, BufferRangeSet ranges) =>
        State.SetBufferRanges(buffer, ranges);

    public void SetBufferRange(GraphBufferCbvId view, in BufferRange range) =>
        State.SetBufferRange(view.Value, GraphViewKind.BufferCbv, range);

    public void SetBufferRange(GraphBufferSrvId view, in BufferRange range) =>
        State.SetBufferRange(view.Value, GraphViewKind.BufferSrv, range);

    public void SetBufferRange(GraphBufferUavId view, in BufferRange range) =>
        State.SetBufferRange(view.Value, GraphViewKind.BufferUav, range);

    public void SetTextureRange(GraphTextureAccessId access, in TextureSubresourceRange range) =>
        State.SetTextureRange(access, range);

    public GraphBufferId CreateBuffer(in BufferDesc description, MemoryType memoryType = MemoryType.DeviceLocal) =>
        State.AddBuffer(description, memoryType, RenderGraphResourceSource.GraphCreated,
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
    /// Returns this frame's mapped bytes for a declared upload buffer. Fill the declaration
    /// before execution; its storage remains with the frame until GPU work completes.
    /// </summary>
    public Span<byte> GetBufferDataWriteSpan(GraphBufferId buffer) => State.GetBufferDataWriteSpan(buffer);

    /// <summary>Copies bytes covering a declared upload buffer into this frame's mapped storage.</summary>
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
        State.AddTexture(description, RenderGraphResourceSource.GraphCreated,
            RenderGraphResourceLifetime.PerFrame);

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
        GraphRegionId region = default, FrameSectionPosition position = FrameSectionPosition.End) =>
        State.AddRasterPass(label, queue, state, options, declaration, callback, region, position);

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
        GraphRegionId region = default, FrameSectionPosition position = FrameSectionPosition.End) =>
        State.AddRasterPass(label, queue, state, options, pipeline,
            parameterLayout, parameterBindings, declaration, callback, region, position);

    public GraphPassId AddComputePass<TState>(
        string label,
        in PassQueueSelection queue,
        in TState state,
        in PassOptions options,
        PassDeclaration<TState> declaration,
        ComputeFrameCallback<TState> callback,
        GraphRegionId region = default, FrameSectionPosition position = FrameSectionPosition.End) =>
        State.AddComputePass(label, queue, state, options, declaration, callback, region, position);

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
        GraphRegionId region = default, FrameSectionPosition position = FrameSectionPosition.End) =>
        State.AddComputePass(label, queue, state, options, pipeline,
            parameterLayout, parameterBindings, declaration, callback, region, position);

    public GraphPassId AddCopyPass<TState>(
        string label,
        in PassQueueSelection queue,
        in TState state,
        in PassOptions options,
        PassDeclaration<TState> declaration,
        CopyFrameCallback<TState> callback,
        GraphRegionId region = default, FrameSectionPosition position = FrameSectionPosition.End) =>
        State.AddCopyPass(label, queue, state, options, declaration, callback, region, position);

    public GraphPassId AddGeneralPass<TState>(
        string label,
        in PassQueueSelection queue,
        in TState state,
        in PassOptions options,
        PassDeclaration<TState> declaration,
        GeneralFrameCallback<TState> callback,
        GraphRegionId region = default, FrameSectionPosition position = FrameSectionPosition.End) =>
        State.AddGeneralPass(label, queue, state, options, declaration, callback, region, position);

    public void OrderAfter(GraphPassId pass, GraphPassId predecessor) => State.OrderAfter(pass, predecessor);

    /// <summary>Orders every pass bound to the input after the given pass.</summary>
    public void OrderAfter(GraphParameterInput input, GraphPassId predecessor) =>
        State.OrderAfter(input, predecessor, consumersAfter: true);

    /// <summary>Orders the given pass after every pass bound to the input.</summary>
    public void OrderAfter(GraphPassId pass, GraphParameterInput input) =>
        State.OrderAfter(input, pass, consumersAfter: false);

    public void Execute()
    {
        RenderGraphFrameState state = State;
        state.Execute(_identity, state.Resources.GetCompletionBuffer(state.Graph.MaximumQueueCompletionCount));
        _state = null;
    }

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

    /// <summary>Updates a retained shader connection. Its registered resources and binding remain in use until changed.</summary>
    public void SetPersistentParameterInput(GraphParameterInput input, ResourceBinding value) => State.SetPersistentParameterInput(input, value);

}
