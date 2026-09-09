namespace SomeEngine.RenderGraph;

internal sealed class RasterFrameOnlyPassCallbackStorage<TState> : PassCallbackStorage,
    IStaticPassDataStorage<TState>,
    IFramePassDataStorage<TState>
{
    private TState _declarationState;
    private readonly RasterFrameCallback<TState> _callback;

    internal RasterFrameOnlyPassCallbackStorage(
        in TState declarationState,
        RasterFrameCallback<TState> callback)
    {
        _declarationState = declarationState;
        _callback = callback ?? throw new ArgumentNullException(nameof(callback));
    }

    internal override Type FrameDataType => typeof(TState);
    internal override void ClearData() => _declarationState = default!;
    public void SetStaticData(in TState value) => _declarationState = value;
    public void SetFrameData(in TState value) => _declarationState = value;

    internal override void Record(
        RenderGraphFrameState frame,
        int passIndex,
        CommandContext context)
    {
        var scope = new RasterPassCommandScope(frame, passIndex, context);
        ref readonly TState state = ref _declarationState;
        _callback(ref scope, in state);
        scope.Finish();
    }
}

internal sealed class ComputeFrameOnlyPassCallbackStorage<TState> : PassCallbackStorage,
    IStaticPassDataStorage<TState>,
    IFramePassDataStorage<TState>
{
    private TState _declarationState;
    private readonly ComputeFrameCallback<TState> _callback;

    internal ComputeFrameOnlyPassCallbackStorage(
        in TState declarationState,
        ComputeFrameCallback<TState> callback)
    {
        _declarationState = declarationState;
        _callback = callback ?? throw new ArgumentNullException(nameof(callback));
    }

    internal override Type FrameDataType => typeof(TState);
    internal override void ClearData() => _declarationState = default!;
    public void SetStaticData(in TState value) => _declarationState = value;
    public void SetFrameData(in TState value) => _declarationState = value;

    internal override void Record(
        RenderGraphFrameState frame,
        int passIndex,
        CommandContext context)
    {
        var scope = new ComputePassCommandScope(frame, passIndex, context);
        ref readonly TState state = ref _declarationState;
        _callback(ref scope, in state);
        scope.Finish();
    }
}

internal sealed class CopyFrameOnlyPassCallbackStorage<TState> : PassCallbackStorage,
    IStaticPassDataStorage<TState>,
    IFramePassDataStorage<TState>
{
    private TState _declarationState;
    private readonly CopyFrameCallback<TState> _callback;

    internal CopyFrameOnlyPassCallbackStorage(
        in TState declarationState,
        CopyFrameCallback<TState> callback)
    {
        _declarationState = declarationState;
        _callback = callback ?? throw new ArgumentNullException(nameof(callback));
    }

    internal override Type FrameDataType => typeof(TState);
    internal override void ClearData() => _declarationState = default!;
    public void SetStaticData(in TState value) => _declarationState = value;
    public void SetFrameData(in TState value) => _declarationState = value;

    internal override void Record(
        RenderGraphFrameState frame,
        int passIndex,
        CommandContext context)
    {
        var scope = new CopyPassCommandScope(frame, passIndex, context);
        ref readonly TState state = ref _declarationState;
        _callback(ref scope, in state);
        scope.Finish();
    }
}

internal sealed class GeneralFrameOnlyPassCallbackStorage<TState> : PassCallbackStorage,
    IStaticPassDataStorage<TState>,
    IFramePassDataStorage<TState>
{
    private TState _declarationState;
    private readonly GeneralFrameCallback<TState> _callback;

    internal GeneralFrameOnlyPassCallbackStorage(
        in TState declarationState,
        GeneralFrameCallback<TState> callback)
    {
        _declarationState = declarationState;
        _callback = callback ?? throw new ArgumentNullException(nameof(callback));
    }

    internal override Type FrameDataType => typeof(TState);
    internal override void ClearData() => _declarationState = default!;
    public void SetStaticData(in TState value) => _declarationState = value;
    public void SetFrameData(in TState value) => _declarationState = value;

    internal override void Record(
        RenderGraphFrameState frame,
        int passIndex,
        CommandContext context)
    {
        var scope = new GeneralPassCommandScope(frame, passIndex, context);
        ref readonly TState state = ref _declarationState;
        _callback(ref scope, in state);
        scope.Finish();
    }
}
