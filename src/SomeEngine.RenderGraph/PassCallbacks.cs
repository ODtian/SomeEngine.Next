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
    void SetFrameData(in T value);
}

internal abstract class PassCallbackStorage
{
    internal abstract Type FrameDataType { get; }
    internal abstract void ClearData();
    internal abstract void Record(RenderGraphFrameState frame, int passIndex, CommandContext context);
}

internal sealed class RasterPassCallbackStorage<TStatic, TFrame> : PassCallbackStorage, IFramePassDataStorage<TFrame>, IStaticPassDataStorage<TStatic>
{
    private TStatic _staticData;
    private TFrame _frameData = default!;
    private readonly RasterPassCallback<TStatic, TFrame> _callback;

    internal RasterPassCallbackStorage(
        in TStatic staticData,
        RasterPassCallback<TStatic, TFrame> callback)
    {
        _staticData = staticData;
        _callback = callback ?? throw new ArgumentNullException(nameof(callback));
    }

    internal override Type FrameDataType => typeof(TFrame);
    internal override void ClearData() { _frameData = default!; _staticData = default!; }
    public void SetStaticData(in TStatic value) => _staticData = value;

    public void SetFrameData(in TFrame value) => _frameData = value;
    internal override void Record(RenderGraphFrameState frame, int passIndex, CommandContext context)
    {
        var scope = new RasterPassCommandScope(frame, passIndex, context);
        _callback(ref scope, in _staticData, in _frameData);
        scope.Finish();
    }
}

internal sealed class ComputePassCallbackStorage<TStatic, TFrame> : PassCallbackStorage, IFramePassDataStorage<TFrame>, IStaticPassDataStorage<TStatic>
{
    private TStatic _staticData;
    private TFrame _frameData = default!;
    private readonly ComputePassCallback<TStatic, TFrame> _callback;

    internal ComputePassCallbackStorage(
        in TStatic staticData,
        ComputePassCallback<TStatic, TFrame> callback)
    {
        _staticData = staticData;
        _callback = callback ?? throw new ArgumentNullException(nameof(callback));
    }

    internal override Type FrameDataType => typeof(TFrame);
    internal override void ClearData() { _frameData = default!; _staticData = default!; }
    public void SetStaticData(in TStatic value) => _staticData = value;

    public void SetFrameData(in TFrame value) => _frameData = value;
    internal override void Record(RenderGraphFrameState frame, int passIndex, CommandContext context)
    {
        var scope = new ComputePassCommandScope(frame, passIndex, context);
        _callback(ref scope, in _staticData, in _frameData);
        scope.Finish();
    }
}

internal sealed class CopyPassCallbackStorage<TStatic, TFrame> : PassCallbackStorage, IFramePassDataStorage<TFrame>, IStaticPassDataStorage<TStatic>
{
    private TStatic _staticData;
    private TFrame _frameData = default!;
    private readonly CopyPassCallback<TStatic, TFrame> _callback;

    internal CopyPassCallbackStorage(
        in TStatic staticData,
        CopyPassCallback<TStatic, TFrame> callback)
    {
        _staticData = staticData;
        _callback = callback ?? throw new ArgumentNullException(nameof(callback));
    }

    internal override Type FrameDataType => typeof(TFrame);
    internal override void ClearData() { _frameData = default!; _staticData = default!; }
    public void SetStaticData(in TStatic value) => _staticData = value;

    public void SetFrameData(in TFrame value) => _frameData = value;
    internal override void Record(RenderGraphFrameState frame, int passIndex, CommandContext context)
    {
        var scope = new CopyPassCommandScope(frame, passIndex, context);
        _callback(ref scope, in _staticData, in _frameData);
        scope.Finish();
    }
}

internal sealed class GeneralPassCallbackStorage<TStatic, TFrame> : PassCallbackStorage, IFramePassDataStorage<TFrame>, IStaticPassDataStorage<TStatic>
{
    private TStatic _staticData;
    private TFrame _frameData = default!;
    private readonly GeneralPassCallback<TStatic, TFrame> _callback;

    internal GeneralPassCallbackStorage(
        in TStatic staticData,
        GeneralPassCallback<TStatic, TFrame> callback)
    {
        _staticData = staticData;
        _callback = callback ?? throw new ArgumentNullException(nameof(callback));
    }

    internal override Type FrameDataType => typeof(TFrame);
    internal override void ClearData() { _frameData = default!; _staticData = default!; }
    public void SetStaticData(in TStatic value) => _staticData = value;

    public void SetFrameData(in TFrame value) => _frameData = value;
    internal override void Record(RenderGraphFrameState frame, int passIndex, CommandContext context)
    {
        var scope = new GeneralPassCommandScope(frame, passIndex, context);
        _callback(ref scope, in _staticData, in _frameData);
        scope.Finish();
    }
}

