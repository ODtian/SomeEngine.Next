namespace SomeEngine.RenderGraph;

public delegate void PassDeclaration<TStatic>(ref PassDefinition definition, ref TStatic staticData);

public delegate void RasterPassCallback<TStatic, TFrame>(
    ref RasterPassCommandScope commands,
    in TStatic staticData,
    in TFrame frameData);

public delegate void ComputePassCallback<TStatic, TFrame>(
    ref ComputePassCommandScope commands,
    in TStatic staticData,
    in TFrame frameData);

public delegate void CopyPassCallback<TStatic, TFrame>(
    ref CopyPassCommandScope commands,
    in TStatic staticData,
    in TFrame frameData);

public delegate void GeneralPassCallback<TStatic, TFrame>(
    ref GeneralPassCommandScope commands,
    in TStatic staticData,
    in TFrame frameData);

internal interface IStaticPassDataStorage<T>
{
    void SetStaticData(in T value);
}

internal interface IFramePassDataStorage<T>
{
    void SetFrameData(int frameSlot, in T value);
}

internal abstract class PassCallbackStorage
{
    internal abstract Type FrameDataType { get; }
    internal abstract void ClearFrameData(int frameSlot);
    internal abstract void Record(RenderGraphFrameState frame, int passIndex, CommandContext context);
}

internal sealed class RasterPassCallbackStorage<TStatic, TFrame> : PassCallbackStorage, IFramePassDataStorage<TFrame>, IStaticPassDataStorage<TStatic>
{
    private TStatic _staticData;
    private readonly TFrame[] _frameData;
    private readonly RasterPassCallback<TStatic, TFrame> _callback;

    internal RasterPassCallbackStorage(
        in TStatic staticData,
        RasterPassCallback<TStatic, TFrame> callback,
        int frameCount)
    {
        _staticData = staticData;
        _callback = callback ?? throw new ArgumentNullException(nameof(callback));
        _frameData = new TFrame[frameCount];
    }

    internal override Type FrameDataType => typeof(TFrame);
    public void SetStaticData(in TStatic value) => _staticData = value;

    public void SetFrameData(int frameSlot, in TFrame value) => _frameData[frameSlot] = value;
    internal override void ClearFrameData(int frameSlot) => _frameData[frameSlot] = default!;
    internal override void Record(RenderGraphFrameState frame, int passIndex, CommandContext context)
    {
        var scope = new RasterPassCommandScope(frame, passIndex, context);
        _callback(ref scope, in _staticData, in _frameData[frame.FrameSlot]);
        scope.Finish();
    }
}

internal sealed class ComputePassCallbackStorage<TStatic, TFrame> : PassCallbackStorage, IFramePassDataStorage<TFrame>, IStaticPassDataStorage<TStatic>
{
    private TStatic _staticData;
    private readonly TFrame[] _frameData;
    private readonly ComputePassCallback<TStatic, TFrame> _callback;

    internal ComputePassCallbackStorage(
        in TStatic staticData,
        ComputePassCallback<TStatic, TFrame> callback,
        int frameCount)
    {
        _staticData = staticData;
        _callback = callback ?? throw new ArgumentNullException(nameof(callback));
        _frameData = new TFrame[frameCount];
    }

    internal override Type FrameDataType => typeof(TFrame);
    public void SetStaticData(in TStatic value) => _staticData = value;

    public void SetFrameData(int frameSlot, in TFrame value) => _frameData[frameSlot] = value;
    internal override void ClearFrameData(int frameSlot) => _frameData[frameSlot] = default!;
    internal override void Record(RenderGraphFrameState frame, int passIndex, CommandContext context)
    {
        var scope = new ComputePassCommandScope(frame, passIndex, context);
        _callback(ref scope, in _staticData, in _frameData[frame.FrameSlot]);
        scope.Finish();
    }
}

internal sealed class CopyPassCallbackStorage<TStatic, TFrame> : PassCallbackStorage, IFramePassDataStorage<TFrame>, IStaticPassDataStorage<TStatic>
{
    private TStatic _staticData;
    private readonly TFrame[] _frameData;
    private readonly CopyPassCallback<TStatic, TFrame> _callback;

    internal CopyPassCallbackStorage(
        in TStatic staticData,
        CopyPassCallback<TStatic, TFrame> callback,
        int frameCount)
    {
        _staticData = staticData;
        _callback = callback ?? throw new ArgumentNullException(nameof(callback));
        _frameData = new TFrame[frameCount];
    }

    internal override Type FrameDataType => typeof(TFrame);
    public void SetStaticData(in TStatic value) => _staticData = value;

    public void SetFrameData(int frameSlot, in TFrame value) => _frameData[frameSlot] = value;
    internal override void ClearFrameData(int frameSlot) => _frameData[frameSlot] = default!;
    internal override void Record(RenderGraphFrameState frame, int passIndex, CommandContext context)
    {
        var scope = new CopyPassCommandScope(frame, passIndex, context);
        _callback(ref scope, in _staticData, in _frameData[frame.FrameSlot]);
        scope.Finish();
    }
}

internal sealed class GeneralPassCallbackStorage<TStatic, TFrame> : PassCallbackStorage, IFramePassDataStorage<TFrame>, IStaticPassDataStorage<TStatic>
{
    private TStatic _staticData;
    private readonly TFrame[] _frameData;
    private readonly GeneralPassCallback<TStatic, TFrame> _callback;

    internal GeneralPassCallbackStorage(
        in TStatic staticData,
        GeneralPassCallback<TStatic, TFrame> callback,
        int frameCount)
    {
        _staticData = staticData;
        _callback = callback ?? throw new ArgumentNullException(nameof(callback));
        _frameData = new TFrame[frameCount];
    }

    internal override Type FrameDataType => typeof(TFrame);
    public void SetStaticData(in TStatic value) => _staticData = value;

    public void SetFrameData(int frameSlot, in TFrame value) => _frameData[frameSlot] = value;
    internal override void ClearFrameData(int frameSlot) => _frameData[frameSlot] = default!;
    internal override void Record(RenderGraphFrameState frame, int passIndex, CommandContext context)
    {
        var scope = new GeneralPassCommandScope(frame, passIndex, context);
        _callback(ref scope, in _staticData, in _frameData[frame.FrameSlot]);
        scope.Finish();
    }
}

