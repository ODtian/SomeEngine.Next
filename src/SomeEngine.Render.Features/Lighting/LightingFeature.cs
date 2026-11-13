using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.Resources;
using SomeEngine.Graphics;
using SomeEngine.Render.Components;
using SomeEngine.Render.Lighting;
using SomeEngine.Render.Systems;
using SomeEngine.RenderGraph;

namespace SomeEngine.Render.Features.Lighting;

/// <summary>Clustered-light resources. AllocationStatus begins with the number of overflow cells.</summary>
public readonly record struct LightingFeatureResources(
    GraphBufferId Grid,
    GraphBufferId LightIndices,
    GraphBufferId AllocationStatus,
    GraphTextureId CookieAtlas,
    uint CellCountPerView,
    uint MaxLightsPerCell,
    uint ViewCount)
{
    public bool IsValid =>
        Grid != default && LightIndices != default && AllocationStatus != default && CookieAtlas != default &&
        CellCountPerView != 0 && MaxLightsPerCell != 0 && ViewCount != 0;
}

/// <summary>Supplies clustered lighting and cookie resources through ordinary graph bindings.</summary>
public sealed class LightingFeature : IGraphResourceProvider, IDisposable, IRenderGraphFrameExecutionObserver
{
    private readonly global::SomeEngine.RenderGraph.RenderGraph _graph;
    private readonly ClusteredLightBinning _binning;
    private readonly GraphTextureId _cookieAtlas;
    private readonly GraphPassId _cookieInitializePass;
    private readonly uint _viewCount;
    private readonly ClusteredLightGridSettings _settings;
    private readonly ClusteredLightGridParameters[] _parameters;
    private readonly GraphBufferInput _frameViews;
    private ClusteredViewGrid? _tileGrid;
    private bool _cookieInitialized;
    private bool _disposed;

    public LightingFeature(
        IGraphicsBackend backend,
        Device device,
        RenderPipelines pipelines,
        global::SomeEngine.RenderGraph.RenderGraph graph,
        RenderPrepareSystems prepareSystems,
        int width,
        int height,
        uint viewCount,
        Shader shader,
        ClusteredLightGridSettings? settings = null,
        GraphRegionId region = default)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(shader);
        if (!ReferenceEquals(graph.Device, device))
            throw new ArgumentException("Lighting and RenderGraph must use the same Device.", nameof(graph));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfZero(viewCount);
        _graph = graph;
        _viewCount = viewCount;
        _settings = settings ?? new();
        _parameters = new ClusteredLightGridParameters[viewCount];
        _binning = new ClusteredLightBinning(graph, pipelines, prepareSystems, width, height, viewCount,
            shader, shader, _settings, region);
        RenderGraphEdit edit = graph.BeginEdit();
        try
        {
            GraphBufferInput frameViews = edit.CreateBufferInput(BufferUsages.ShaderRead);
            GraphTextureId cookieAtlas = edit.CreatePersistentTexture(new TextureDesc(
                TextureDimension.Texture2D, 1, 1, 1, 1, 1, 1, Format.R8G8B8A8UNorm,
                TextureUsages.Sampled | TextureUsages.CopyDestination | TextureUsages.ColorAttachment,
                label: "Lighting cookie atlas"));
            GraphPassId initialize = edit.AddCopyFramePass(
                "Initialize lighting cookie atlas", PassQueueSelection.AnyOfType(QueueType.Graphics), cookieAtlas,
                new PassOptions(Culling: PassCullingMode.NeverCull),
                static (ref PassDefinition pass, ref GraphTextureId texture) =>
                    pass.Write(texture, new TextureSubresourceRange(0, 1, 0, 1, TextureAspects.Color),
                        PipelineSync.RenderTarget, ResourceAccess.RenderTarget, TextureLayout.RenderTarget,
                        WriteCoverage.Complete),
                static (ref CopyPassCommandScope commands, in GraphTextureId texture) =>
                    commands.ClearTexture(commands.GetTexture(texture),
                        new TextureSubresourceRange(0, 1, 0, 1, TextureAspects.Color), Vector4.One), region: region);
            edit.Commit();
            _frameViews = frameViews;
            _cookieAtlas = cookieAtlas;
            _cookieInitializePass = initialize;
        }
        finally { edit.Dispose(); }
        Resources = new(_binning.Cells, _binning.LightIndices, _binning.Diagnostics, _cookieAtlas,
            _binning.CellCountPerView, _settings.MaxLightsPerCell, viewCount);
    }

    public LightingFeatureResources Resources { get; }
    public ClusteredViewGrid ViewGrid => new(_frameViews, Resources.CellCountPerView, _viewCount);
    public uint LastFrameOverflowCells => _binning.LastFrameOverflowCells;
    public ulong FeedbackFrameIdentity => _binning.FeedbackFrameIdentity;

    /// <summary>Creates view parameters for screen tile candidates with one depth slice.</summary>
    public ClusteredViewGrid CreateTileGrid()
    {
        if (_tileGrid is { } grid) return grid;
        using RenderGraphEdit edit = _graph.BeginEdit();
        grid = new(edit.CreateBufferInput(BufferUsages.ShaderRead),
            Resources.CellCountPerView / _settings.DepthSliceCount, _viewCount);
        edit.Commit();
        _tileGrid = grid;
        return grid;
    }

    public void Record(ref RenderGraphFrame graph, RenderLightSet lights, ReadOnlySpan<RenderView> views,
        bool enabled = true)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(lights);
        if (views.IsEmpty || views.Length > checked((int)_viewCount))
            throw new ArgumentOutOfRangeException(nameof(views));
        int sourceLightCount = checked(lights.Directional.Count + lights.Points.Count + lights.Spots.Count);
        if (sourceLightCount > _settings.MaxLights)
            throw new InvalidOperationException($"Lighting received {sourceLightCount} lights, exceeding its fixed capacity {_settings.MaxLights}.");
        _binning.Record(ref graph, lights, views, enabled);
        uint directionalCount = enabled ? checked((uint)lights.Directional.Count) : 0u;
        uint localCount = enabled ? checked((uint)lights.UploadOrder.Count) : 0u;
        Span<ClusteredLightGridParameters> parameters = _parameters;
        parameters.Clear();
        for (int index = 0; index < views.Length; index++)
            parameters[index] = ClusteredLightGridParameters.Create(in views[index], _settings,
                Resources.CellCountPerView, directionalCount, localCount);
        graph.SetBufferInput(_frameViews, graph.Upload(parameters, BufferUsages.ShaderRead,
            "Lighting frame views"));
        graph.SetPassEnabled(_cookieInitializePass, !_cookieInitialized);
        if (!_cookieInitialized) graph.ObserveExecution(this);
        if (_tileGrid is { } tileGrid)
        {
            for (int index = 0; index < views.Length; index++)
            {
                parameters[index].DepthSliceCount = 1;
                parameters[index].CellCountPerView = tileGrid.CellCountPerView;
            }
            graph.SetBufferInput(tileGrid.Parameters, graph.Upload(parameters, BufferUsages.ShaderRead,
                "Screen tile frame views"));
        }
    }

    void IRenderGraphFrameExecutionObserver.OnFrameSubmitted(in RenderGraphSubmission submission) =>
        _cookieInitialized = true;

    public bool TryResolve(string name, ResourceBindingType type, out GraphResourceBinder binder)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        LightingResourceBinding? binding = LightingResources.Resolve(name, type);
        binder = binding is { Mode: GraphAccessMode.Read } resource ? resource.Resource switch
        {
            LightingResource.Views => BindLightingViews,
            LightingResource.Lights => BindLights,
            LightingResource.Grid => BindGrid,
            LightingResource.Indices => BindIndices,
            LightingResource.CookieAtlas => BindCookieAtlas,
            _ => null!,
        } : null!;
        return binder is not null;
    }

    private void BindLightingViews(ref RenderGraphEdit graph, ref PassDefinition pass, PipelineSync sync) =>
        pass.Bind(graph.CreateBufferSrv(_frameViews, structureStride: ClusteredLightGridParameters.SizeInBytes,
            label: "Standard shade lighting views"), sync);

    private void BindLights(ref RenderGraphEdit graph, ref PassDefinition pass, PipelineSync sync) =>
        pass.Bind(graph.CreateBufferSrv(_binning.LightBuffer, structureStride: GpuLight.SizeInBytes,
            label: "Standard shade lights"), sync);

    private void BindGrid(ref RenderGraphEdit graph, ref PassDefinition pass, PipelineSync sync) =>
        pass.Bind(graph.CreateBufferSrv(_binning.Cells, structureStride: 2 * sizeof(uint),
            label: "Standard shade clustered grid"), sync);

    private void BindIndices(ref RenderGraphEdit graph, ref PassDefinition pass, PipelineSync sync) =>
        pass.Bind(graph.CreateBufferSrv(_binning.LightIndices, structureStride: sizeof(uint),
            label: "Standard shade light indices"), sync, BufferContentScope.CountBoundedPrefix);

    private void BindCookieAtlas(ref RenderGraphEdit graph, ref PassDefinition pass, PipelineSync sync) =>
        pass.Bind(graph.CreateTextureSrv(_cookieAtlas, format: Format.R8G8B8A8UNorm,
            dimension: TextureViewDimension.Texture2DArray, label: "Standard shade lighting cookie atlas"), sync);

    internal void BindVirtualShadowPageMarking(ref RenderGraphEdit graph, ref PassDefinition pass, PipelineSync sync)
    {
        BindLightingViews(ref graph, ref pass, sync);
        BindLights(ref graph, ref pass, sync);
        BindGrid(ref graph, ref pass, sync);
        BindIndices(ref graph, ref pass, sync);
    }

    public void ReplaceShader(Shader previous, Shader next) => _binning.ReplaceShader(previous, next);

    public void Dispose()
    {
        if (_disposed) return;
        _binning.Dispose();
        _disposed = true;
    }
}

[StructLayout(LayoutKind.Sequential, Pack = 4, Size = SizeInBytes)]
internal struct ClusteredLightGridParameters
{
    internal const int SizeInBytes = 192;

    internal Matrix4x4 View;
    internal Matrix4x4 Projection;
    internal uint TileSizeX;
    internal uint TileSizeY;
    internal uint TileCountX;
    internal uint TileCountY;
    internal Vector4 ZParams;
    internal uint DepthSliceCount;
    internal uint MaxLightsPerCell;
    internal uint DirectionalCount;
    internal uint NonDirectionalCount;
    internal uint ScreenWidth;
    internal uint ScreenHeight;
    internal uint CellCountPerView;
    internal uint Padding;

    internal static ClusteredLightGridParameters Create(
        in RenderView view,
        ClusteredLightGridSettings settings,
        uint cellCountPerView,
        uint directionalCount,
        uint nonDirectionalCount)
    {
        const float nearDepth = 0.1f;
        const float farDepth = 10_000.0f;
        float logRange = MathF.Log2(farDepth / nearDepth);
        float sliceScale = settings.DepthSliceCount / logRange;
        return new ClusteredLightGridParameters
        {
            View = view.View,
            TileSizeX = settings.TileSize,
            TileSizeY = settings.TileSize,
            TileCountX = DivideRoundUp(view.ViewportWidth, settings.TileSize),
            TileCountY = DivideRoundUp(view.ViewportHeight, settings.TileSize),
            ZParams = new Vector4(
                nearDepth,
                farDepth,
                sliceScale,
                -MathF.Log2(nearDepth) * sliceScale),
            DepthSliceCount = settings.DepthSliceCount,
            MaxLightsPerCell = settings.MaxLightsPerCell,
            DirectionalCount = directionalCount,
            NonDirectionalCount = nonDirectionalCount,
            Projection = view.Projection,
            ScreenWidth = view.ViewportWidth,
            ScreenHeight = view.ViewportHeight,
            CellCountPerView = cellCountPerView,
        };
    }

    private static uint DivideRoundUp(uint value, uint divisor) =>
        checked((value + divisor - 1u) / divisor);
}
