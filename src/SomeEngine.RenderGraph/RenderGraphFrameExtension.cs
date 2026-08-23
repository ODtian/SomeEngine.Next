namespace SomeEngine.RenderGraph;

public ref struct RenderGraphFrameExtension
{
    private RenderGraphFrameState? _state;
    private readonly ulong _frameIdentity;
    private readonly int _extensionOrdinal;

    internal RenderGraphFrameExtension(
        RenderGraphFrameState state,
        ulong frameIdentity,
        int extensionOrdinal)
    {
        _state = state;
        _frameIdentity = frameIdentity;
        _extensionOrdinal = extensionOrdinal;
    }

    private RenderGraphFrameState State
    {
        get
        {
            RenderGraphFrameState state = _state
                ?? throw new InvalidOperationException("The render graph extension is no longer active.");
            state.EnsureLease(_frameIdentity);
            return state;
        }
    }

    public GraphBufferId CreateBuffer(in BufferDesc description, MemoryType memoryType = MemoryType.DeviceLocal) =>
        State.AddBuffer(description, memoryType, RenderGraphResourceOwnership.GraphOwned,
            RenderGraphResourceLifetime.PerFrame);

    public GraphTextureId CreateTexture(in TextureDesc description) =>
        State.AddTexture(description, RenderGraphResourceOwnership.GraphOwned,
            RenderGraphResourceLifetime.PerFrame);

    public GraphPassId AddRasterPass<TState>(
        string label,
        in PassQueueSelection queue,
        in TState state,
        in PassOptions options,
        PassDeclaration<TState> declaration,
        RasterFrameCallback<TState> callback) =>
        State.AddRasterPass(label, queue, state, options, declaration, callback, _extensionOrdinal);

    public GraphPassId AddComputePass<TState>(
        string label,
        in PassQueueSelection queue,
        in TState state,
        in PassOptions options,
        PassDeclaration<TState> declaration,
        ComputeFrameCallback<TState> callback) =>
        State.AddComputePass(label, queue, state, options, declaration, callback, _extensionOrdinal);

    public GraphPassId AddCopyPass<TState>(
        string label,
        in PassQueueSelection queue,
        in TState state,
        in PassOptions options,
        PassDeclaration<TState> declaration,
        CopyFrameCallback<TState> callback) =>
        State.AddCopyPass(label, queue, state, options, declaration, callback, _extensionOrdinal);

    public GraphPassId AddGeneralPass<TState>(
        string label,
        in PassQueueSelection queue,
        in TState state,
        in PassOptions options,
        PassDeclaration<TState> declaration,
        GeneralFrameCallback<TState> callback) =>
        State.AddGeneralPass(label, queue, state, options, declaration, callback, _extensionOrdinal);

    public void OrderAfter(GraphPassId pass, GraphPassId predecessor) => State.OrderAfter(pass, predecessor);

    public void Dispose() => _state = null;
}

