namespace SomeEngine.RenderGraph;

internal sealed class RasterFrameOnlyPassCallbackStorage<TState> : PassCallbackStorage,
    IStaticPassDataStorage<TState>,
    IFramePassDataStorage<TState>
{
    private TState _declarationState;
    private readonly TState[] _frameData;
    private readonly bool[] _hasFrameData;
    private readonly RasterFrameCallback<TState> _callback;

    internal RasterFrameOnlyPassCallbackStorage(
        in TState declarationState,
        RasterFrameCallback<TState> callback,
        int frameCount)
    {
        _declarationState = declarationState;
        _callback = callback ?? throw new ArgumentNullException(nameof(callback));
        _frameData = new TState[frameCount];
        _hasFrameData = new bool[frameCount];
    }

    internal override Type FrameDataType => typeof(TState);
    public void SetStaticData(in TState value) => _declarationState = value;
    public void SetFrameData(int frameSlot, in TState value)
    {
        _frameData[frameSlot] = value;
        _hasFrameData[frameSlot] = true;
    }
    internal override void ClearFrameData(int frameSlot)
    {
        _frameData[frameSlot] = default!;
        _hasFrameData[frameSlot] = false;
    }

    internal override void Record(
        RenderGraphFrameState frame,
        int passIndex,
        CommandContext context)
    {
        var scope = new RasterPassCommandScope(frame, passIndex, context);
        ref readonly TState state = ref _hasFrameData[frame.FrameSlot]
            ? ref _frameData[frame.FrameSlot]
            : ref _declarationState;
        _callback(ref scope, in state);
        scope.Finish();
    }
}

internal sealed class ComputeFrameOnlyPassCallbackStorage<TState> : PassCallbackStorage,
    IStaticPassDataStorage<TState>,
    IFramePassDataStorage<TState>
{
    private TState _declarationState;
    private readonly TState[] _frameData;
    private readonly bool[] _hasFrameData;
    private readonly ComputeFrameCallback<TState> _callback;

    internal ComputeFrameOnlyPassCallbackStorage(
        in TState declarationState,
        ComputeFrameCallback<TState> callback,
        int frameCount)
    {
        _declarationState = declarationState;
        _callback = callback ?? throw new ArgumentNullException(nameof(callback));
        _frameData = new TState[frameCount];
        _hasFrameData = new bool[frameCount];
    }

    internal override Type FrameDataType => typeof(TState);
    public void SetStaticData(in TState value) => _declarationState = value;
    public void SetFrameData(int frameSlot, in TState value)
    {
        _frameData[frameSlot] = value;
        _hasFrameData[frameSlot] = true;
    }
    internal override void ClearFrameData(int frameSlot)
    {
        _frameData[frameSlot] = default!;
        _hasFrameData[frameSlot] = false;
    }

    internal override void Record(
        RenderGraphFrameState frame,
        int passIndex,
        CommandContext context)
    {
        var scope = new ComputePassCommandScope(frame, passIndex, context);
        ref readonly TState state = ref _hasFrameData[frame.FrameSlot]
            ? ref _frameData[frame.FrameSlot]
            : ref _declarationState;
        _callback(ref scope, in state);
        scope.Finish();
    }
}

internal sealed class CopyFrameOnlyPassCallbackStorage<TState> : PassCallbackStorage,
    IStaticPassDataStorage<TState>,
    IFramePassDataStorage<TState>
{
    private TState _declarationState;
    private readonly TState[] _frameData;
    private readonly bool[] _hasFrameData;
    private readonly CopyFrameCallback<TState> _callback;

    internal CopyFrameOnlyPassCallbackStorage(
        in TState declarationState,
        CopyFrameCallback<TState> callback,
        int frameCount)
    {
        _declarationState = declarationState;
        _callback = callback ?? throw new ArgumentNullException(nameof(callback));
        _frameData = new TState[frameCount];
        _hasFrameData = new bool[frameCount];
    }

    internal override Type FrameDataType => typeof(TState);
    public void SetStaticData(in TState value) => _declarationState = value;
    public void SetFrameData(int frameSlot, in TState value)
    {
        _frameData[frameSlot] = value;
        _hasFrameData[frameSlot] = true;
    }
    internal override void ClearFrameData(int frameSlot)
    {
        _frameData[frameSlot] = default!;
        _hasFrameData[frameSlot] = false;
    }

    internal override void Record(
        RenderGraphFrameState frame,
        int passIndex,
        CommandContext context)
    {
        var scope = new CopyPassCommandScope(frame, passIndex, context);
        ref readonly TState state = ref _hasFrameData[frame.FrameSlot]
            ? ref _frameData[frame.FrameSlot]
            : ref _declarationState;
        _callback(ref scope, in state);
        scope.Finish();
    }
}

internal sealed class GeneralFrameOnlyPassCallbackStorage<TState> : PassCallbackStorage,
    IStaticPassDataStorage<TState>,
    IFramePassDataStorage<TState>
{
    private TState _declarationState;
    private readonly TState[] _frameData;
    private readonly bool[] _hasFrameData;
    private readonly GeneralFrameCallback<TState> _callback;

    internal GeneralFrameOnlyPassCallbackStorage(
        in TState declarationState,
        GeneralFrameCallback<TState> callback,
        int frameCount)
    {
        _declarationState = declarationState;
        _callback = callback ?? throw new ArgumentNullException(nameof(callback));
        _frameData = new TState[frameCount];
        _hasFrameData = new bool[frameCount];
    }

    internal override Type FrameDataType => typeof(TState);
    public void SetStaticData(in TState value) => _declarationState = value;
    public void SetFrameData(int frameSlot, in TState value)
    {
        _frameData[frameSlot] = value;
        _hasFrameData[frameSlot] = true;
    }
    internal override void ClearFrameData(int frameSlot)
    {
        _frameData[frameSlot] = default!;
        _hasFrameData[frameSlot] = false;
    }

    internal override void Record(
        RenderGraphFrameState frame,
        int passIndex,
        CommandContext context)
    {
        var scope = new GeneralPassCommandScope(frame, passIndex, context);
        ref readonly TState state = ref _hasFrameData[frame.FrameSlot]
            ? ref _frameData[frame.FrameSlot]
            : ref _declarationState;
        _callback(ref scope, in state);
        scope.Finish();
    }
}
