using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.Graphics;
using SomeEngine.Render.Components;
using SomeEngine.Render.Systems;
using SomeEngine.RenderGraph;
using SomeEngine.Resources;

namespace SomeEngine.Render.Features.Decals;

/// <summary>Bins projected material volumes into an independent screen and depth grid.</summary>
public sealed class MaterialDecalBinning : IDisposable, IGraphReadbackReceiver
{
    private MaterialDecalBinningSettings _settings;
    private readonly global::SomeEngine.RenderGraph.RenderGraph _graph;
    private readonly RenderPipelines _pipelines;
    private Shader _sharedShader;
    private Shader _itemShader;
    private readonly uint _width, _height, _viewCount;
    private readonly MaterialDecalBinningView[] _views;
    private MaterialDecalVolume[] _volumeValues;
    private uint _decalCapacity;
    private readonly RenderPrepareSystems _prepareSystems;
    private readonly int _prepareSystemIndex;
    private readonly GraphBufferInput _volumes;
    private readonly GraphBufferInput _viewParameters;
    private readonly GraphBufferSrvId _volumesView;
    private readonly GraphBufferSrvId _viewParametersView;
    private readonly GraphBufferSrvId[] _inputViews;
    private readonly IndirectCommandLayout _dispatchLayout;
    private ScreenBinningResources _binning;

    public uint CellCountPerView { get; private set; }
    public GraphRegionId Region { get; }
    public GraphBufferId Cells => _binning.Cells;
    public GraphBufferId Indices => _binning.Indices;
    public GraphBufferId Diagnostics => _binning.Diagnostics;
    public GraphBufferInput Volumes => _volumes;
    public GraphBufferInput ViewParameters => _viewParameters;
    public GraphPassId CompletePass => _binning.CompletePass;
    public IEnumerable<BufferDesc> ResourceDescriptions => _binning.Buffers.Select(_graph.GetBufferDescription);
    public uint LastFrameOverflowCells { get; private set; }
    public uint RequiredListLength { get; private set; }
    public uint ListCapacity { get; private set; }
    public ulong FeedbackFrameIdentity { get; private set; }

    public MaterialDecalBinning(global::SomeEngine.RenderGraph.RenderGraph graph, RenderPipelines pipelines,
        RenderPrepareSystems prepareSystems, Shader sharedShader, Shader itemShader,
        uint width, uint height, uint viewCount, uint decalCapacity, uint listCapacity,
        MaterialDecalBinningSettings? settings = null, GraphRegionId region = default)
    {
        _graph = graph;
        _pipelines = pipelines;
        _prepareSystems = prepareSystems;
        _settings = settings ?? new();
        _width = width; _height = height; _viewCount = viewCount;
        _views = new MaterialDecalBinningView[viewCount];
        _volumeValues = new MaterialDecalVolume[decalCapacity];
        _dispatchLayout = graph.Backend.CreateIndirectCommandLayout(graph.Device, new(
            [new IndirectArgumentDesc(IndirectArgumentType.Dispatch)], 12, label: "Decal binning expansion"));
        uint cells = CellCount(_settings);
        RenderGraphEdit edit = graph.BeginEdit();
        try
        {
            GraphRegionId work = edit.AddRegion("Material decal binning", region);
            GraphBufferInput volumes = edit.CreateBufferInput(BufferUsages.ShaderRead);
            GraphBufferInput viewParameters = edit.CreateBufferInput(BufferUsages.ShaderRead);
            GraphBufferSrvId volumesView = edit.CreateBufferSrv(volumes, structureStride: MaterialDecalVolume.SizeInBytes);
            GraphBufferSrvId viewParametersView = edit.CreateBufferSrv(viewParameters,
                structureStride: MaterialDecalBinningView.SizeInBytes);
            GraphBufferSrvId[] inputs = [volumesView, viewParametersView];
            ScreenBinningResources binning = ScreenBinningPasses.Add(ref edit, pipelines, sharedShader, itemShader,
                _dispatchLayout, decalCapacity * viewCount, cells * viewCount, listCapacity,
                (name, _) => name switch
                {
                    "MaterialDecalVolumes" => GraphParameterResourceBinding.ReadOnlyBufferDescriptor(volumesView),
                    "MaterialDecalViews" => GraphParameterResourceBinding.ReadOnlyBufferDescriptor(viewParametersView),
                    _ => null,
                }, inputs, this, work);
            edit.Commit();
            Region = work;
            _volumes = volumes;
            _viewParameters = viewParameters;
            _volumesView = volumesView;
            _viewParametersView = viewParametersView;
            _inputViews = inputs;
            _binning = binning;
            _sharedShader = sharedShader;
            _itemShader = itemShader;
            _decalCapacity = decalCapacity;
            CellCountPerView = cells;
            ListCapacity = listCapacity;
        }
        finally { edit.Dispose(); }
        _prepareSystemIndex = prepareSystems.Add(new MaterialDecalBinningPrepareSystem(this));
    }

    /// <summary>Uploads standalone transforms in the caller's material application order.</summary>
    public void Record(ref RenderGraphFrame frame, scoped ReadOnlySpan<Matrix4x4> decalTransforms,
        scoped ReadOnlySpan<RenderView> views)
    {
        for (int index = 0; index < decalTransforms.Length; index++)
            _volumeValues[index] = new MaterialDecalVolume { DecalToWorld = decalTransforms[index] };
        MaterialDecalVolume empty = default;
        ReadOnlySpan<MaterialDecalVolume> volumes = decalTransforms.IsEmpty
            ? MemoryMarshal.CreateReadOnlySpan(ref empty, 1) : _volumeValues.AsSpan(0, decalTransforms.Length);
        GraphBufferSlice data = frame.Upload<MaterialDecalVolume>(volumes, BufferUsages.ShaderRead,
            "Decal binning volumes");
        Record(ref frame, data, (uint)decalTransforms.Length, views);
    }

    /// <summary>Reads the same volume slice supplied to receiving materials.</summary>
    public void Record(ref RenderGraphFrame frame, GraphBufferSlice volumes, uint decalCount,
        scoped ReadOnlySpan<RenderView> views)
    {
        for (int index = 0; index < views.Length; index++)
            _views[index] = MaterialDecalBinningView.Create(in views[index], _settings, CellCountPerView, decalCount);
        frame.SetBufferInput(_volumes, volumes);
        frame.SetBufferInput(_viewParameters, frame.Upload<MaterialDecalBinningView>(_views.AsSpan(0, views.Length),
            BufferUsages.ShaderRead, "Decal binning views"));
        ScreenBinningPasses.SetFrameParameters(ref frame, _binning, decalCount * (uint)views.Length,
            _decalCapacity * _viewCount, CellCountPerView * _viewCount);
    }

    public void SetCapacity(uint decalCapacity)
    {
        if (decalCapacity <= _decalCapacity) return;
        Rebuild(_settings, decalCapacity, ListCapacity);
    }

    public void SetSettings(MaterialDecalBinningSettings settings, uint listCapacity)
    {
        if (_settings == settings && ListCapacity == listCapacity) return;
        Rebuild(settings, _decalCapacity, listCapacity);
    }

    private void Rebuild(MaterialDecalBinningSettings settings, uint decalCapacity, uint listCapacity)
    {
        uint cells = CellCount(settings);
        MaterialDecalVolume[] volumeValues = decalCapacity > _volumeValues.Length
            ? new MaterialDecalVolume[decalCapacity] : _volumeValues;
        RenderGraphEdit edit = _graph.BeginEdit();
        try
        {
            ScreenBinningResources binning = ScreenBinningPasses.Rebuild(ref edit, in _binning, _pipelines,
                _sharedShader, _itemShader, _dispatchLayout, decalCapacity * _viewCount, cells * _viewCount,
                listCapacity, ResolveResource, _inputViews, this, Region);
            edit.Commit();
            _binning = binning;
            _settings = settings;
            _decalCapacity = decalCapacity;
            _volumeValues = volumeValues;
            CellCountPerView = cells;
            ListCapacity = listCapacity;
        }
        finally { edit.Dispose(); }
    }

    private uint CellCount(MaterialDecalBinningSettings settings) =>
        ((_width + settings.TileSize - 1) / settings.TileSize) *
        ((_height + settings.TileSize - 1) / settings.TileSize) * settings.DepthSliceCount;

    private GraphParameterResourceBinding? ResolveResource(string name, ResourceBindingType _) => name switch
    {
        "MaterialDecalVolumes" => GraphParameterResourceBinding.ReadOnlyBufferDescriptor(_volumesView),
        "MaterialDecalViews" => GraphParameterResourceBinding.ReadOnlyBufferDescriptor(_viewParametersView),
        _ => null,
    };

    public bool ReplaceShader(Shader previous, Shader next)
    {
        if (!ReferenceEquals(_sharedShader, previous) && !ReferenceEquals(_itemShader, previous)) return false;
        RenderGraphEdit edit = _graph.BeginEdit();
        try
        {
            ScreenBinningResources binning = ScreenBinningPasses.ReplaceShader(ref edit, in _binning,
                _pipelines, previous, next, ResolveResource);
            edit.Commit();
            _binning = binning;
            if (ReferenceEquals(_sharedShader, previous)) _sharedShader = next;
            if (ReferenceEquals(_itemShader, previous)) _itemShader = next;
        }
        finally { edit.Dispose(); }
        return true;
    }

    internal void GrowList()
    {
        if (RequiredListLength <= ListCapacity) return;
        RenderGraphEdit edit = _graph.BeginEdit();
        try
        {
            ScreenBinningPasses.SetListCapacity(ref edit, _binning, RequiredListLength);
            edit.Commit();
            ListCapacity = RequiredListLength;
        }
        finally { edit.Dispose(); }
    }

    void IGraphReadbackReceiver.OnReadback(ReadOnlySpan<byte> bytes, ulong tag)
    {
        ReadOnlySpan<uint> values = MemoryMarshal.Cast<byte, uint>(bytes);
        LastFrameOverflowCells = values[0];
        RequiredListLength = values[1];
        FeedbackFrameIdentity = tag;
    }

    public void Dispose()
    {
        _prepareSystems.Remove(_prepareSystemIndex);
        if (!_graph.IsDisposed && !_graph.IsLost)
        {
            RenderGraphEdit edit = _graph.BeginEdit();
            try
            {
                ScreenBinningPasses.Remove(ref edit, _binning);
                edit.Remove(Region);
                edit.Remove(_volumesView);
                edit.Remove(_viewParametersView);
                edit.Commit();
            }
            finally { edit.Dispose(); }
        }
        _graph.RetireAfterSubmittedFrames(_dispatchLayout);
    }
}
