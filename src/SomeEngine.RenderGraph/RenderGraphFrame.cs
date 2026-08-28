namespace SomeEngine.RenderGraph;

using System.Runtime.InteropServices;

public ref struct RenderGraphFrame
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

    public ulong StructureVersion => State.Graph.StructureVersion;

    public void SetPassEnabled(GraphPassId pass, bool enabled) => State.SetPassEnabled(pass, enabled);

    public void SetPassData<T>(GraphPassId pass, in T value) => State.SetPassData(pass, value);

    public void SetBufferRange(GraphBufferAccessId access, in BufferRange range) =>
        State.SetBufferRange(access, range);

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

    public GraphBufferId Upload(
        scoped ReadOnlySpan<byte> data,
        BufferUsages usages,
        string? label = null) =>
        State.Upload(data, usages, label);

    public GraphBufferId Upload<T>(
        scoped ReadOnlySpan<T> data,
        BufferUsages usages,
        string? label = null)
        where T : unmanaged =>
        State.Upload(MemoryMarshal.AsBytes(data), usages, label);

    public GraphBufferId Upload<T>(
        in T value,
        BufferUsages usages,
        string? label = null)
        where T : unmanaged
    {
        ReadOnlySpan<T> values = MemoryMarshal.CreateReadOnlySpan(ref Unsafe.AsRef(in value), 1);
        return State.Upload(MemoryMarshal.AsBytes(values), usages, label);
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
        RasterFrameCallback<TState> callback) =>
        State.AddRasterPass(label, queue, state, options, declaration, callback, -1);

    public GraphPassId AddRasterPass<TState>(
        string label,
        in PassQueueSelection queue,
        in TState state,
        in PassOptions options,
        Pipeline pipeline,
        VariableLayoutReflection parameterLayout,
        ReadOnlySpan<GraphParameterResourceBinding> parameterBindings,
        PassDeclaration<TState> declaration,
        RasterFrameCallback<TState> callback) =>
        State.AddRasterPass(label, queue, state, options, pipeline,
            parameterLayout, parameterBindings, declaration, callback, -1);

    public GraphPassId AddComputePass<TState>(
        string label,
        in PassQueueSelection queue,
        in TState state,
        in PassOptions options,
        PassDeclaration<TState> declaration,
        ComputeFrameCallback<TState> callback) =>
        State.AddComputePass(label, queue, state, options, declaration, callback, -1);

    public GraphPassId AddComputePass<TState>(
        string label,
        in PassQueueSelection queue,
        in TState state,
        in PassOptions options,
        Pipeline pipeline,
        VariableLayoutReflection parameterLayout,
        ReadOnlySpan<GraphParameterResourceBinding> parameterBindings,
        PassDeclaration<TState> declaration,
        ComputeFrameCallback<TState> callback) =>
        State.AddComputePass(label, queue, state, options, pipeline,
            parameterLayout, parameterBindings, declaration, callback, -1);

    public GraphPassId AddCopyPass<TState>(
        string label,
        in PassQueueSelection queue,
        in TState state,
        in PassOptions options,
        PassDeclaration<TState> declaration,
        CopyFrameCallback<TState> callback) =>
        State.AddCopyPass(label, queue, state, options, declaration, callback, -1);

    public GraphPassId AddGeneralPass<TState>(
        string label,
        in PassQueueSelection queue,
        in TState state,
        in PassOptions options,
        PassDeclaration<TState> declaration,
        GeneralFrameCallback<TState> callback) =>
        State.AddGeneralPass(label, queue, state, options, declaration, callback, -1);

    public void OrderAfter(GraphPassId pass, GraphPassId predecessor) => State.OrderAfter(pass, predecessor);

    public RenderGraphFrameExtension BeginExtension(GraphExtensionPointId point) =>
        new(State, _identity, State.ResolveExtensionPoint(point));

    public int Execute(Span<QueueCompletion> destination)
    {
        RenderGraphFrameState state = State;
        try
        {
            int result = state.Execute(_identity, destination);
            _state = null;
            return result;
        }
        catch
        {
            _state = null;
            throw;
        }
    }

    public void Dispose()
    {
        RenderGraphFrameState? state = _state;
        _state = null;
        state?.Cancel(_identity);
    }
}
