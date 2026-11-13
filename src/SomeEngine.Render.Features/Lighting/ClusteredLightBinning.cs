using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.Graphics;
using SomeEngine.Render.Components;
using SomeEngine.Render.Lighting;
using SomeEngine.Render.Systems;
using SomeEngine.RenderGraph;
using SomeEngine.Resources;

namespace SomeEngine.Render.Features.Lighting;

/// <summary>Bins prioritized local lights into view cells using shared screen binning.</summary>
public sealed class ClusteredLightBinning : IDisposable, IGraphReadbackReceiver
{
    private ScreenBinningResources _binning;
    private readonly global::SomeEngine.RenderGraph.RenderGraph _graph;
    private readonly RenderPipelines _pipelines;
    private Shader _sharedShader;
    private Shader _itemShader;
    private readonly IndirectCommandLayout _dispatchLayout;
    private readonly GraphBufferSrvId[] _inputViews;
    private readonly RenderPrepareSystems _prepareSystems;
    private readonly int _prepareSystemIndex;
    private readonly ClusteredLightGridSettings _settings;
    private readonly ClusteredLightBinningView[] _parameters;
    private readonly GpuLight[] _lights;
    private readonly uint[] _kinds;
    private readonly GraphBufferInput _frameLights;
    private readonly GraphBufferInput _frameKinds;
    private readonly GraphBufferInput _frameViews;

    public ClusteredLightBinning(global::SomeEngine.RenderGraph.RenderGraph graph, RenderPipelines pipelines,
        RenderPrepareSystems prepareSystems, int width, int height, uint viewCount,
        Shader sharedShader, Shader itemShader, ClusteredLightGridSettings? settings = null, GraphRegionId region = default)
    {
        _graph = graph;
        _pipelines = pipelines;
        _prepareSystems = prepareSystems;
        _settings = settings ?? new();
        _settings.Validate();
        _parameters = new ClusteredLightBinningView[viewCount];
        _lights = new GpuLight[_settings.MaxLights];
        _kinds = new uint[_settings.MaxLights];
        uint tileCountX = ((uint)width + _settings.TileSize - 1u) / _settings.TileSize;
        uint tileCountY = ((uint)height + _settings.TileSize - 1u) / _settings.TileSize;
        CellCountPerView = tileCountX * tileCountY * _settings.DepthSliceCount;
        _dispatchLayout = graph.Backend.CreateIndirectCommandLayout(graph.Device, new(
            [new IndirectArgumentDesc(IndirectArgumentType.Dispatch)], 12, label: "Light binning expansion"));
        RenderGraphEdit edit = graph.BeginEdit();
        try
        {
            GraphBufferInput frameLights = edit.CreateBufferInput(BufferUsages.ShaderRead);
            GraphBufferInput frameKinds = edit.CreateBufferInput(BufferUsages.ShaderRead);
            GraphBufferInput frameViews = edit.CreateBufferInput(BufferUsages.ShaderRead);
            GraphBufferSrvId lightsView = edit.CreateBufferSrv(frameLights, structureStride: GpuLight.SizeInBytes);
            GraphBufferSrvId kindsView = edit.CreateBufferSrv(frameKinds, structureStride: sizeof(uint));
            GraphBufferSrvId viewsView = edit.CreateBufferSrv(frameViews, structureStride: ClusteredLightBinningView.SizeInBytes);
            GraphBufferSrvId[] inputViews = [lightsView, kindsView, viewsView];
            ScreenBinningResources binning = ScreenBinningPasses.Add(ref edit, pipelines, sharedShader, itemShader, _dispatchLayout,
                (uint)_settings.MaxLights * viewCount, CellCountPerView * viewCount, _settings.LightListCapacity,
                (name, type) => ResolveInput(name, type, inputViews), inputViews, this, region);
            edit.Commit();
            _binning = binning;
            _frameLights = frameLights;
            _frameKinds = frameKinds;
            _frameViews = frameViews;
            _inputViews = inputViews;
            _sharedShader = sharedShader;
            _itemShader = itemShader;
            ListCapacity = _settings.LightListCapacity;
        }
        finally { edit.Dispose(); }
        _prepareSystemIndex = prepareSystems.Add(new ClusteredLightBinningPrepareSystem(this));
    }

    public GraphBufferId Cells => _binning.Cells;
    public GraphBufferId LightIndices => _binning.Indices;
    public GraphBufferId Diagnostics => _binning.Diagnostics;
    public GraphPassId CompletePass => _binning.CompletePass;
    public GraphBufferInput LightBuffer => _frameLights;
    public GraphBufferInput ViewParameters => _frameViews;
    public uint CellCountPerView { get; }
    public uint LastFrameOverflowCells { get; private set; }
    public uint RequiredListLength { get; private set; }
    public uint ListCapacity { get; private set; }
    public ulong FeedbackFrameIdentity { get; private set; }

    internal void Grow()
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

    /// <summary>Orders source lights for standalone use and records the resulting cell lists.</summary>
    public void Record(ref RenderGraphFrame frame, RenderLightSet lights,
        scoped ReadOnlySpan<RenderView> views, Vector3 cameraPosition)
    {
        lights.OrderForUpload(cameraPosition);
        Record(ref frame, lights, views);
    }

    /// <summary>Consumes the light upload order established during frame preparation.</summary>
    public void Record(ref RenderGraphFrame frame, RenderLightSet lights,
        scoped ReadOnlySpan<RenderView> views, bool enabled = true)
    {
        IReadOnlyList<RenderLightIndex> order = lights.UploadOrder;
        uint directionalCount = enabled ? (uint)lights.Directional.Count : 0u;
        uint localCount = enabled ? (uint)order.Count : 0u;
        for (int index = 0; index < lights.Directional.Count; index++)
        {
            RenderDirectionalLight light = lights.Directional[index];
            _lights[index] = Encode(light.Color, light.Intensity, light.LayerMask);
            _lights[index].Direction = Vector3.Normalize(light.Direction);
        }
        for (int index = 0; index < order.Count; index++)
        {
            RenderLightIndex reference = order[index];
            int destination = lights.Directional.Count + index;
            _kinds[destination] = (uint)reference.Kind;
            if (reference.Kind == RenderLightKind.Point)
            {
                RenderPointLight light = lights.Points[reference.Index];
                _lights[destination] = Encode(light.Color, light.Intensity, light.LayerMask);
                _lights[destination].Position = light.Position;
                _lights[destination].Range = light.Range;
            }
            else
            {
                RenderSpotLight light = lights.Spots[reference.Index];
                _lights[destination] = Encode(light.Color, light.Intensity, light.LayerMask);
                _lights[destination].Position = light.Position;
                _lights[destination].Range = light.Range;
                _lights[destination].Direction = Vector3.Normalize(light.Direction);
                _lights[destination].InnerConeCos = light.InnerConeCos;
                _lights[destination].OuterConeCos = light.OuterConeCos;
            }
        }
        for (int index = 0; index < views.Length; index++)
            _parameters[index] = ClusteredLightBinningView.Create(views[index], _settings, directionalCount, localCount);
        int lightCount = Math.Max(1, lights.Directional.Count + order.Count);
        frame.SetBufferInput(_frameLights, frame.Upload<GpuLight>(_lights.AsSpan(0, lightCount),
            BufferUsages.ShaderRead, "Light binning lights"));
        frame.SetBufferInput(_frameKinds, frame.Upload<uint>(_kinds.AsSpan(0, lightCount),
            BufferUsages.ShaderRead, "Light binning types"));
        frame.SetBufferInput(_frameViews, frame.Upload<ClusteredLightBinningView>(_parameters.AsSpan(0, views.Length),
            BufferUsages.ShaderRead, "Light binning views"));
        ScreenBinningPasses.SetFrameParameters(ref frame, _binning, localCount * (uint)views.Length,
            (uint)_settings.MaxLights * (uint)_parameters.Length, CellCountPerView * (uint)_parameters.Length,
            _settings.MaxLightsPerCell);
    }

    private static GpuLight Encode(Vector3 color, float intensity, uint layerMask) => new()
    {
        Color = color,
        Intensity = intensity,
        LayerMask = layerMask,
        CookieIndex = -1,
        CookieStrength = 1f,
        WorldToLightCookie = Matrix4x4.Identity,
        CookieScaleOffset = Vector4.One,
    };

    public void ReplaceShader(Shader previous, Shader next)
    {
        if (!ReferenceEquals(_sharedShader, previous) && !ReferenceEquals(_itemShader, previous)) return;
        RenderGraphEdit edit = _graph.BeginEdit();
        try
        {
            ScreenBinningResources binning = ScreenBinningPasses.ReplaceShader(ref edit, in _binning,
                _pipelines, previous, next, ResolveInput);
            edit.Commit();
            _binning = binning;
            if (ReferenceEquals(_sharedShader, previous)) _sharedShader = next;
            if (ReferenceEquals(_itemShader, previous)) _itemShader = next;
        }
        finally { edit.Dispose(); }
    }

    private GraphParameterResourceBinding? ResolveInput(string name, ResourceBindingType type) =>
        ResolveInput(name, type, _inputViews);

    private static GraphParameterResourceBinding? ResolveInput(string name, ResourceBindingType type,
        ReadOnlySpan<GraphBufferSrvId> views) => type == ResourceBindingType.BufferSrv ? name switch
        {
            LightingResources.LightBufferName => GraphParameterResourceBinding.ReadOnlyBufferDescriptor(views[0]),
            LightingResources.LightBinningKindsName => GraphParameterResourceBinding.ReadOnlyBufferDescriptor(views[1]),
            LightingResources.LightBinningViewsName => GraphParameterResourceBinding.ReadOnlyBufferDescriptor(views[2]),
            _ => null,
        } : null;

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
                foreach (GraphBufferSrvId view in _inputViews) edit.Remove(view);
                edit.Commit();
            }
            finally { edit.Dispose(); }
        }
        _graph.RetireAfterSubmittedFrames(_dispatchLayout);
    }
}
