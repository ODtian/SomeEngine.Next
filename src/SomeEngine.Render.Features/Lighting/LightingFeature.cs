using System.Numerics;
using SlangShaderSharp;
using System.Runtime.InteropServices;
using SomeEngine.Assets.Schema;
using SomeEngine.Resources;
using SomeEngine.Graphics;
using SomeEngine.Render.Assets;
using SomeEngine.Render;
using SomeEngine.Render.Components;
using SomeEngine.Render.Lighting;
using SomeEngine.RenderGraph;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.Render.Features.Lighting;

/// <summary>Explicit clustered-lighting resources. AllocationStatus contains one uint:
/// zero means complete assignment; nonzero means the shared budget omitted required lights.</summary>
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
        Grid != default &&
        LightIndices != default &&
        AllocationStatus != default &&
        CookieAtlas != default &&
        CellCountPerView != 0 &&
        MaxLightsPerCell != 0 &&
        ViewCount != 0;
}

/// <summary>
/// Owns light conversion, shared-budget spatial assignment resources, and retained graph passes.
/// Standard shade discovers these resources from its final linked program through the generic
/// graph resource boundary.
/// </summary>
public sealed class LightingFeature : IGraphResourceProvider, IDisposable, IRenderGraphFrameExecutionObserver
{
    private const uint ThreadCount = 64;
    private LinkedComputePipeline? _singlePointPreparePipeline;
    private LinkedComputePipeline? _singlePointFillPipeline;
    private GraphPassId _singlePointPreparePass;
    private GraphPassId _singlePointFillPass;
    private ulong _singlePointVersion;
    private readonly IGraphicsBackend _backend;
    private readonly Device _device;
    private readonly global::SomeEngine.RenderGraph.RenderGraph _graph;
    private readonly Shader _singlePointShader;
    private readonly GraphRegionId _singlePointRegion;
    private readonly uint _entryCapacity;
    private readonly bool _supportsSinglePoint;

    private readonly LinkedComputePipeline _pipeline;
    private readonly LinkedComputePipeline _clearPipeline;
    private readonly LinkedComputePipeline _compactPipeline;
    private readonly GraphPassId _clearPass;
    private readonly GraphPassId _buildPass;
    private readonly GraphPassId _compactPass;
    private readonly GraphPassId _cookieInitializePass;
    private readonly GraphBufferId _grid;
    private readonly GraphBufferId _lightIndices;
    private readonly GraphBufferId _lightLinks;
    private readonly GraphBufferId _allocationCounters;
    private readonly GraphBufferId _allocationStatus;
    private readonly GraphTextureId _cookieAtlas;
    private readonly int _width;
    private readonly int _height;
    private readonly uint _viewCount;
    private readonly ClusteredLightGridSettings _settings;
    private readonly List<GpuLight> _gpuLights;
    private readonly ClusteredLightGridParameters[] _viewParameters;
    private readonly GraphBufferInput _frameLights;
    private readonly GraphBufferInput _frameViews;
    private bool _cookieInitialized;
    private bool _disposed;

    public LightingFeature(
        IGraphicsBackend backend,
        Device device,
        global::SomeEngine.RenderGraph.RenderGraph graph,
        int width,
        int height,
        uint viewCount,
        Shader shader,
        string entryPoint,
        ClusteredLightGridSettings? settings = null,
        GraphRegionId region = default)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(shader);
        if (!ReferenceEquals(graph.Device, device))
            throw new ArgumentException("Lighting and RenderGraph must use the same Device.", nameof(graph));
        ArgumentException.ThrowIfNullOrWhiteSpace(entryPoint);

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfZero(viewCount);
        _width = width;
        _height = height;
        _viewCount = viewCount;
        _settings = settings ?? new ClusteredLightGridSettings();
        _settings.Validate();
        _gpuLights = new List<GpuLight>(_settings.MaxLights);
        _viewParameters = new ClusteredLightGridParameters[checked((int)_viewCount)];

        uint tileCountX = DivideRoundUp(checked((uint)_width), _settings.TileSize);
        uint tileCountY = DivideRoundUp(checked((uint)_height), _settings.TileSize);
        uint cellsPerView = checked(tileCountX * tileCountY * _settings.DepthSliceCount);
        ulong gridBytes = checked((ulong)cellsPerView * _viewCount * 2u * sizeof(uint));
        uint lightEntryCapacity = checked(cellsPerView * _viewCount * _settings.MaxLightsPerCell);
        ulong indexBytes = (ulong)lightEntryCapacity * sizeof(uint);

        _entryCapacity = lightEntryCapacity;
        _backend = backend;
        _device = device;
        _graph = graph;
        _singlePointShader = shader;
        LinkedComputePipeline? pipeline = null;
        LinkedComputePipeline? clearPipeline = null;
        LinkedComputePipeline? compactPipeline = null;
        try
        {
            pipeline = LinkedComputePipeline.Create(
                backend,
                device,
                shader,
                entryPoint,
                "Build clustered lighting grid");
            clearPipeline = LinkedComputePipeline.Create(
                backend, device, shader, "ClearClusteredLightGrid", "Clear clustered lighting counts");
            compactPipeline = LinkedComputePipeline.Create(
                backend, device, shader, "CompactClusteredLightGrid", "Compact clustered lighting lists");
            _supportsSinglePoint = HasSinglePointRole(pipeline.EntryPoint.Function, "build-v2") &&
                HasSinglePointEntry(pipeline.Reflection, "PrepareSinglePointLightGrid", "prepare-v2") &&
                HasSinglePointEntry(pipeline.Reflection, "FillSinglePointLightGrid", "fill-v2");
            RenderGraphEdit edit = graph.BeginEdit();
            try
            {
                _frameLights = edit.CreateBufferInput(BufferUsages.ShaderRead);
                _frameViews = edit.CreateBufferInput(BufferUsages.ShaderRead);
                _grid = edit.CreateBuffer(new BufferDesc(
                    gridBytes,
                    BufferUsages.ShaderRead | BufferUsages.ShaderWrite,
                    "Lighting clustered grid"));
                _lightIndices = edit.CreateBuffer(new BufferDesc(
                    indexBytes,
                    BufferUsages.ShaderRead | BufferUsages.ShaderWrite,
                    "Lighting clustered indices"));
                _lightLinks = edit.CreateBuffer(new BufferDesc(
                    indexBytes * 2u, BufferUsages.ShaderRead | BufferUsages.ShaderWrite,
                    "Lighting pooled links"));
                _allocationCounters = edit.CreateBuffer(new BufferDesc(
                    2u * sizeof(uint), BufferUsages.ShaderRead | BufferUsages.ShaderWrite,
                    "Lighting list allocation counters"));
                _allocationStatus = edit.CreateBuffer(new BufferDesc(
                    sizeof(uint), BufferUsages.ShaderRead | BufferUsages.ShaderWrite | BufferUsages.CopySource,
                    "Lighting list allocation status"));
                _cookieAtlas = edit.CreatePersistentTexture(new TextureDesc(
                    TextureDimension.Texture2D,
                    1,
                    1,
                    1,
                    1,
                    1,
                    1,
                    Format.R8G8B8A8UNorm,
                    TextureUsages.Sampled | TextureUsages.CopyDestination |
                        TextureUsages.ColorAttachment,
                    label: "Lighting cookie atlas"));
                _cookieInitializePass = edit.AddCopyFramePass(
                    "Initialize lighting cookie atlas",
                    PassQueueSelection.AnyOfType(QueueType.Graphics),
                    _cookieAtlas,
                    new PassOptions(Culling: PassCullingMode.NeverCull),
                    static (ref PassDefinition pass, ref GraphTextureId texture) =>
                        _ = pass.Write(
                            texture,
                            new TextureSubresourceRange(0, 1, 0, 1, TextureAspects.Color),
                            PipelineSync.RenderTarget,
                            ResourceAccess.RenderTarget,
                            TextureLayout.RenderTarget,
                            WriteCoverage.Complete),
                    static (ref CopyPassCommandScope commands, in GraphTextureId texture) =>
                        commands.ClearTexture(
                            commands.GetTexture(texture),
                            new TextureSubresourceRange(0, 1, 0, 1, TextureAspects.Color),
                            System.Numerics.Vector4.One), region: region);
                var clearData = new LightingBuildPassData(default);
                _clearPass = edit.AddComputeFramePass(
                    "Clear lighting assignment",
                    PassQueueSelection.AnyCompatible(),
                    clearData,
                    new PassOptions(Culling: PassCullingMode.NeverCull, RepeatCommands: true),
                    clearPipeline.Pipeline,
                    clearPipeline.ParameterLayout,
                    default,
                    static (ref PassDefinition _, ref LightingBuildPassData _) => { },
                    LightingBuildPassData.Record, region: region);
                var build = new LightingBuildPassData(default);
                _buildPass = edit.AddComputeFramePass(
                    "Build lighting assignment",
                    PassQueueSelection.AnyCompatible(),
                    build,
                    new PassOptions(Culling: PassCullingMode.NeverCull, RepeatCommands: true),
                    pipeline.Pipeline,
                    pipeline.ParameterLayout,
                    default,
                    static (ref PassDefinition _, ref LightingBuildPassData _) => { },
                    LightingBuildPassData.Record, region: region);
                _compactPass = edit.AddComputeFramePass(
                    "Compact lighting assignment",
                    PassQueueSelection.AnyCompatible(),
                    new LightingBuildPassData(default),
                    new PassOptions(Culling: PassCullingMode.NeverCull, RepeatCommands: true),
                    compactPipeline.Pipeline,
                    compactPipeline.ParameterLayout,
                    default,
                    static (ref PassDefinition _, ref LightingBuildPassData _) => { },
                    LightingBuildPassData.Record, region: region);
                edit.OrderAfter(_buildPass, _clearPass);
                edit.OrderAfter(_compactPass, _buildPass);
                for (int assignment = 0; assignment < 3; assignment++)
                {
                    bool clear = assignment == 0;
                    bool compact = assignment == 2;
                    PassDefinition pass = edit.GetPass(clear ? _clearPass : compact ? _compactPass : _buildPass);
                    int[] resources = ShaderResourceLayout.Resolve<int>(
                        clear ? clearPipeline.ParameterLayout : compact ? compactPipeline.ParameterLayout : pipeline.ParameterLayout,
                        static (name, type) => (name, type) switch
                        {
                            ("LightingViews", ResourceBindingType.BufferSrv) => 0,
                            ("LightBuffer", ResourceBindingType.BufferSrv) => 1,
                            ("ClusteredLightGrid", ResourceBindingType.BufferUav) => 2,
                            ("LightIndexList", ResourceBindingType.BufferUav) => 3,
                            ("LightLinkList", ResourceBindingType.BufferUav) => 4,
                            ("LightAllocationCounters", ResourceBindingType.BufferUav) => 5,
                            ("LightAllocationStatus", ResourceBindingType.BufferUav) => 6,
                            _ => null,
                        });
                    foreach (int resource in resources)
                        switch (resource)
                        {
                            case 0:
                                _ = pass.Bind(edit.CreateBufferSrv(_frameViews,
                                    structureStride: ClusteredLightGridParameters.SizeInBytes,
                                    label: "Lighting view parameters"), PipelineSync.ComputeShading);
                                break;
                            case 1:
                                _ = pass.Bind(edit.CreateBufferSrv(_frameLights,
                                    structureStride: GpuLight.SizeInBytes, label: "Lighting lights"),
                                    PipelineSync.ComputeShading);
                                break;
                            case 2:
                                _ = pass.Bind(edit.CreateBufferUav(_grid,
                                    structureStride: 2 * sizeof(uint), label: "Lighting grid output"),
                                    PipelineSync.ComputeShading,
                                    clear ? GraphAccessMode.Write : GraphAccessMode.ReadWrite,
                                    clear ? WriteCoverage.Complete : WriteCoverage.Partial);
                                break;
                            case 3:
                                _ = pass.Bind(edit.CreateBufferUav(_lightIndices,
                                    structureStride: sizeof(uint), label: "Lighting index output"),
                                    PipelineSync.ComputeShading, GraphAccessMode.Write,
                                    WriteCoverage.Partial, resultContents: ResourceContentState.Defined,
                                    contentScope: BufferContentScope.CountBoundedPrefix);
                                break;
                            case 4:
                                _ = pass.Bind(edit.CreateBufferUav(_lightLinks,
                                    structureStride: 2 * sizeof(uint), label: "Lighting link pool"),
                                    PipelineSync.ComputeShading, compact ? GraphAccessMode.Read : GraphAccessMode.Write,
                                    WriteCoverage.Partial, resultContents: ResourceContentState.Defined,
                                    contentScope: BufferContentScope.CountBoundedPrefix);
                                break;
                            case 5:
                            case 6:
                                _ = pass.Bind(edit.CreateBufferUav(resource == 5 ? _allocationCounters : _allocationStatus,
                                    structureStride: sizeof(uint), label: "Lighting allocation state"),
                                    PipelineSync.ComputeShading, clear ? GraphAccessMode.Write : GraphAccessMode.ReadWrite,
                                    clear ? WriteCoverage.Complete : WriteCoverage.Partial);
                                break;
                        }
                    // Clearing every cell count establishes an empty valid index prefix even
                    // when the compiled clear entry has no index-list descriptor.
                    if (clear && !resources.Contains(3))
                        pass.Write(_lightIndices, BufferRange.Whole, PipelineSync.ComputeShading,
                            ResourceAccess.UnorderedAccess, WriteCoverage.Partial,
                            resultContents: ResourceContentState.Defined,
                            contentScope: BufferContentScope.CountBoundedPrefix);
                }
                if (_supportsSinglePoint)
                    _singlePointRegion = edit.AddRegion("Single point grid", region);
                edit.Commit();


            }
            finally
            {
                edit.Dispose();
            }
            _pipeline = pipeline;
            _clearPipeline = clearPipeline;
            _compactPipeline = compactPipeline;
            pipeline = null;
            clearPipeline = null;
            compactPipeline = null;
            Resources = new LightingFeatureResources(
                _grid,
                _lightIndices,
                _allocationStatus,
                _cookieAtlas,
                cellsPerView,
                _settings.MaxLightsPerCell,
                _viewCount);
        }
        catch
        {
            pipeline?.Dispose();
            clearPipeline?.Dispose();
            compactPipeline?.Dispose();
            throw;
        }
    }

    public LightingFeatureResources Resources { get; }
    public ClusteredViewGrid ViewGrid => new(_frameViews, Resources.CellCountPerView, _viewCount);

    public void Record(
        ref RenderGraphFrame graph,
        RenderLightSet lights,
        ReadOnlySpan<RenderView> views,
        bool enabled = true)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(lights);
        if (views.IsEmpty || views.Length > _viewParameters.Length)
        {
            throw new ArgumentOutOfRangeException(
                nameof(views),
                $"Lighting requires 1..{_viewParameters.Length} render views.");
        }

        int sourceLightCount = checked(
            lights.Directional.Count + lights.Points.Count + lights.Spots.Count);
        if (sourceLightCount > _settings.MaxLights)
        {
            throw new InvalidOperationException(
                $"Lighting received {sourceLightCount} lights, exceeding its fixed capacity {_settings.MaxLights}.");
        }
        EncodeLights(lights);
        uint directionalCount = enabled ? checked((uint)lights.Directional.Count) : 0u;
        uint nonDirectionalCount = enabled
            ? checked((uint)(lights.Points.Count + lights.Spots.Count))
            : 0u;
        if (_gpuLights.Count == 0)
            _gpuLights.Add(default);

        Span<ClusteredLightGridParameters> parameters = _viewParameters;
        parameters.Clear();
        for (int index = 0; index < views.Length; index++)
        {
            parameters[index] = ClusteredLightGridParameters.Create(
                in views[index],
                _settings,
                Resources.CellCountPerView,
                directionalCount,
                nonDirectionalCount);
        }

        graph.SetBufferInput(_frameLights, graph.Upload(
            CollectionsMarshal.AsSpan(_gpuLights),
            BufferUsages.ShaderRead,
            "Lighting frame lights"));
        graph.SetBufferInput(_frameViews, graph.Upload(
            parameters,
            BufferUsages.ShaderRead,
            "Lighting frame views"));

        graph.SetPassData(
            _clearPass,
            new LightingBuildPassData(new DispatchArguments(
                DivideRoundUp(Resources.CellCountPerView, ThreadCount),
                checked((uint)views.Length),
                1)));
        graph.SetPassData(
            _buildPass,
            new LightingBuildPassData(new DispatchArguments(
                nonDirectionalCount,
                checked((uint)views.Length),
                parameters[0].GroupsPerLight)));
        graph.SetPassData(_compactPass, new LightingBuildPassData(new DispatchArguments(
            DivideRoundUp(Resources.CellCountPerView, ThreadCount), checked((uint)views.Length), 1)));
        bool useSinglePoint = _supportsSinglePoint && enabled && _viewCount == 1 && views.Length == 1 &&
            lights.Points.Count == 1 && lights.Spots.Count == 0 &&
            parameters[0].TileCountX != 0 && parameters[0].TileCountY != 0 && parameters[0].DepthSliceCount != 0 &&
            Resources.CellCountPerView % parameters[0].DepthSliceCount == 0 &&
            (ulong)parameters[0].TileCountX * parameters[0].TileCountY == Resources.CellCountPerView / parameters[0].DepthSliceCount &&
            _entryCapacity >= Resources.CellCountPerView;
        if (useSinglePoint && _singlePointVersion == 0)
            InstallSinglePointPasses();
        bool singlePointVisible = _singlePointVersion != 0 && graph.StructureVersion >= _singlePointVersion;
        useSinglePoint &= singlePointVisible;
        if (singlePointVisible)
        {
            uint groups = Resources.CellCountPerView / ThreadCount + (Resources.CellCountPerView % ThreadCount != 0 ? 1u : 0u);
            graph.SetPassData(_singlePointPreparePass, new LightingBuildPassData(new DispatchArguments(1, 1, 1)));
            graph.SetPassData(_singlePointFillPass, new LightingBuildPassData(new DispatchArguments(Math.Min(groups, 65535u), (groups + 65534u) / 65535u, 1)));
            graph.SetPassEnabled(_singlePointPreparePass, useSinglePoint);
            graph.SetPassEnabled(_singlePointFillPass, useSinglePoint);
        }
        graph.SetPassEnabled(_clearPass, !useSinglePoint);
        graph.SetPassEnabled(_cookieInitializePass, !_cookieInitialized);
        if (!_cookieInitialized) graph.ObserveExecution(this);
        graph.SetPassEnabled(_buildPass, !useSinglePoint && enabled && nonDirectionalCount != 0);
        graph.SetPassEnabled(_compactPass, !useSinglePoint && enabled && nonDirectionalCount != 0);
    }

    void IRenderGraphFrameExecutionObserver.OnRenderGraphFrameFinished(RenderGraphFrameResult result)
    {
        if (result.TryGetPassCompletion(_cookieInitializePass, out _)) _cookieInitialized = true;
    }

    public bool TryResolve(
        string name,
        ResourceBindingType type,
        out GraphResourceBinder binder)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        binder = name switch
        {
            "LightingViews" when type == ResourceBindingType.BufferSrv => BindLightingViews,
            "LightBuffer" when type == ResourceBindingType.BufferSrv => BindLights,
            "ClusteredLightGrid" when type == ResourceBindingType.BufferSrv => BindGrid,
            "LightIndexList" when type == ResourceBindingType.BufferSrv => BindIndices,
            "LightingCookieAtlas" when type == ResourceBindingType.TextureSrv => BindCookieAtlas,
            _ => null!,
        };
        return binder is not null;
    }

    private void BindLightingViews(ref RenderGraphEdit graph, ref PassDefinition pass, PipelineSync sync)
    {
        _ = pass.Bind(
            graph.CreateBufferSrv(
                _frameViews,
                structureStride: ClusteredLightGridParameters.SizeInBytes,
                label: "Standard shade lighting views"),
            sync);
    }

    private void BindLights(ref RenderGraphEdit graph, ref PassDefinition pass, PipelineSync sync)
    {
        _ = pass.Bind(
            graph.CreateBufferSrv(
                _frameLights,
                structureStride: GpuLight.SizeInBytes,
                label: "Standard shade lights"),
            sync);
    }

    private void BindGrid(ref RenderGraphEdit graph, ref PassDefinition pass, PipelineSync sync)
    {
        _ = pass.Bind(
            graph.CreateBufferSrv(
                _grid,
                structureStride: 2 * sizeof(uint),
                label: "Standard shade clustered grid"),
            sync);
    }

    private void BindIndices(ref RenderGraphEdit graph, ref PassDefinition pass, PipelineSync sync)
    {
        _ = pass.Bind(
            graph.CreateBufferSrv(
                _lightIndices,
                structureStride: sizeof(uint),
                label: "Standard shade light indices"),
            sync,
            BufferContentScope.CountBoundedPrefix);
    }

    private void BindCookieAtlas(ref RenderGraphEdit graph, ref PassDefinition pass, PipelineSync sync)
    {
        _ = pass.Bind(
            graph.CreateTextureSrv(
                _cookieAtlas,
                format: Format.R8G8B8A8UNorm,
                dimension: SomeEngine.Graphics.TextureViewDimension.Texture2DArray,
                label: "Standard shade lighting cookie atlas"),
            sync);
    }

    internal void BindVirtualShadowPageMarking(
        ref RenderGraphEdit graph,
        ref PassDefinition pass, PipelineSync sync)
    {
        _ = pass.Bind(
            graph.CreateBufferSrv(
                _frameViews,
                structureStride: ClusteredLightGridParameters.SizeInBytes,
                label: "Virtual shadow lighting views"),
            sync);
        BindLights(ref graph, ref pass, sync);
        _ = pass.Bind(
            graph.CreateBufferSrv(
                _grid,
                structureStride: 2 * sizeof(uint),
                label: "Virtual shadow clustered light grid"),
            sync);
        _ = pass.Bind(
            graph.CreateBufferSrv(
                _lightIndices,
                structureStride: sizeof(uint),
                label: "Virtual shadow clustered light indices"),
            sync,
            BufferContentScope.CountBoundedPrefix);
    }

    private void EncodeLights(RenderLightSet source)
    {
        _gpuLights.Clear();
        for (int index = 0; index < source.Directional.Count; index++)
        {
            RenderDirectionalLight light = source.Directional[index];
            _gpuLights.Add(new GpuLight
            {
                Direction = NormalizeOrZero(light.Direction),
                Color = light.Color,
                Intensity = light.Intensity,
                LayerMask = light.LayerMask,
                CookieIndex = -1,
                CookieStrength = 1.0f,
                WorldToLightCookie = Matrix4x4.Identity,
                CookieScaleOffset = Vector4.One,
            });
        }
        for (int index = 0; index < source.Points.Count; index++)
        {
            RenderPointLight light = source.Points[index];
            _gpuLights.Add(new GpuLight
            {
                Position = light.Position,
                Range = light.Range,
                Color = light.Color,
                Intensity = light.Intensity,
                LayerMask = light.LayerMask,
                CookieIndex = -1,
                CookieStrength = 1.0f,
                WorldToLightCookie = Matrix4x4.Identity,
                CookieScaleOffset = Vector4.One,
            });
        }
        for (int index = 0; index < source.Spots.Count; index++)
        {
            RenderSpotLight light = source.Spots[index];
            _gpuLights.Add(new GpuLight
            {
                Position = light.Position,
                Range = light.Range,
                Direction = NormalizeOrZero(light.Direction),
                InnerConeCos = light.InnerConeCos,
                OuterConeCos = light.OuterConeCos,
                Color = light.Color,
                Intensity = light.Intensity,
                LayerMask = light.LayerMask,
                CookieIndex = -1,
                CookieStrength = 1.0f,
                WorldToLightCookie = Matrix4x4.Identity,
                CookieScaleOffset = Vector4.One,
            });
        }
    }

    private static Vector3 NormalizeOrZero(Vector3 value) =>
        value.LengthSquared() > 1e-12f ? Vector3.Normalize(value) : Vector3.Zero;

    private static uint DivideRoundUp(uint value, uint divisor) =>
        value == 0 ? 0 : checked((value + divisor - 1u) / divisor);

    public void Dispose()
    {
        if (_disposed)
            return;
        _singlePointPreparePipeline?.Dispose();
        _singlePointFillPipeline?.Dispose();
        _pipeline.Dispose();
        _clearPipeline.Dispose();
        _compactPipeline.Dispose();
        _gpuLights.Clear();
        Array.Clear(_viewParameters);
        _disposed = true;
    }

    private void InstallSinglePointPasses()
    {
        LinkedComputePipeline? prepare = null, fill = null;
        try
        {
            prepare = LinkedComputePipeline.Create(_backend, _device, _singlePointShader,
                "PrepareSinglePointLightGrid", "Prepare singleton cell bounds");
            fill = LinkedComputePipeline.Create(_backend, _device, _singlePointShader,
                "FillSinglePointLightGrid", "Fill singleton final grid");
            RenderGraphEdit edit = _graph.BeginEdit();
            try
            {
                GraphBufferId bounds = edit.CreateBuffer(new BufferDesc(32,
                    BufferUsages.ShaderRead | BufferUsages.ShaderWrite, "Single point bounds"));
                GraphPassId preparePass = AddSinglePointPass(ref edit, prepare, true, bounds);
                GraphPassId fillPass = AddSinglePointPass(ref edit, fill, false, bounds);
                edit.OrderAfter(fillPass, preparePass);
                edit.Commit();
                _singlePointPreparePass = preparePass;
                _singlePointFillPass = fillPass;
                _singlePointPreparePipeline = prepare;
                _singlePointFillPipeline = fill;
                _singlePointVersion = _graph.StructureVersion;
                prepare = null;
                fill = null;
            }
            finally { edit.Dispose(); }
        }
        catch
        {
            prepare?.Dispose();
            fill?.Dispose();
            throw;
        }
    }

    private GraphPassId AddSinglePointPass(ref RenderGraphEdit edit, LinkedComputePipeline linked, bool prepare, GraphBufferId bounds)
    {
        GraphPassId id = edit.AddComputeFramePass(
            prepare ? "Prepare single point grid" : "Fill single point grid",
            PassQueueSelection.AnyCompatible(), new LightingBuildPassData(default),
            new PassOptions(Culling: PassCullingMode.NeverCull, RepeatCommands: true),
            linked.Pipeline, linked.ParameterLayout, default,
            static (ref PassDefinition _, ref LightingBuildPassData _) => { },
            initiallyEnabled: false, callback: (ref ComputePassCommandScope commands, in LightingBuildPassData data) =>
            {
                commands.Dispatch(data.Dispatch);
            }, region: _singlePointRegion);
        PassDefinition pass = edit.GetPass(id);
        var resources = ShaderResourceLayout.Resolve<(string Name, ResourceBindingType Type)>(
            linked.ParameterLayout, static (name, type) => (name, type));
        foreach (var input in resources)
        {
            switch (input.Name)
            {
                case "LightingViews":
                    pass.Bind(edit.CreateBufferSrv(_frameViews, structureStride: ClusteredLightGridParameters.SizeInBytes), PipelineSync.ComputeShading); break;
                case "LightBuffer":
                    var lightView = edit.CreateBufferSrv(_frameLights, structureStride: GpuLight.SizeInBytes);
                    if (prepare) pass.Bind(lightView, PipelineSync.ComputeShading);
                    else pass.Bind(GraphParameterResourceBinding.ReadOnlyBufferDescriptor(lightView));
                    break;
                case "ClusteredLightGrid":
                    BindOutput(ref edit, ref pass, _grid, 8, !prepare, WriteCoverage.Complete, BufferContentScope.FullRange); break;
                case "LightIndexList":
                    BindOutput(ref edit, ref pass, _lightIndices, 4, !prepare, WriteCoverage.Partial, BufferContentScope.CountBoundedPrefix); break;
                case "LightLinkList":
                    BindOutput(ref edit, ref pass, _lightLinks, 8, !prepare, WriteCoverage.Partial, BufferContentScope.CountBoundedPrefix); break;
                case "LightAllocationCounters":
                    BindOutput(ref edit, ref pass, _allocationCounters, 4, prepare, WriteCoverage.Complete, BufferContentScope.FullRange); break;
                case "LightAllocationStatus":
                    BindOutput(ref edit, ref pass, _allocationStatus, 4, prepare, WriteCoverage.Complete, BufferContentScope.FullRange); break;
                default: throw new InvalidDataException("Unknown direct grid binding " + input.Name);
            }
        }
        pass.BindParameterBlock(linked.EntryPoint.VarLayout);
        pass.Bind(edit.CreateBufferUav(bounds, structureStride: 16), PipelineSync.ComputeShading,
            prepare ? GraphAccessMode.Write : GraphAccessMode.Read,
            prepare ? WriteCoverage.Complete : WriteCoverage.Partial);
        return id;
        void BindOutput(ref RenderGraphEdit bindingEdit, ref PassDefinition bindingPass, GraphBufferId buffer, uint stride,
            bool writes, WriteCoverage coverage, BufferContentScope scope)
        {
            var view = bindingEdit.CreateBufferUav(buffer, structureStride: stride);
            if (writes) bindingPass.Bind(view, PipelineSync.ComputeShading, GraphAccessMode.Write, coverage,
                resultContents: ResourceContentState.Defined, contentScope: scope);
            else bindingPass.Bind(GraphParameterResourceBinding.WritableBufferDescriptor(view));
        }
    }

    private static bool HasSinglePointRole(FunctionReflection function, string role)
    {
        int count = 0;
        bool valid = false;
        for (uint i = 0; i < function.AttributeCount; i++)
        {
            var attribute = function.GetAttribute(i);
            if (attribute.Name != "ClusteredSinglePointDirect") continue;
            count++;
            valid = attribute.ArgumentCount == 1 && attribute.GetArgumentValueString(0) == role;
        }
        return count == 1 && valid;
    }
    private static bool HasSinglePointEntry(ShaderReflection reflection, string name, string role)
    {
        int matches = 0;
        bool valid = false;
        for (uint i = 0; i < reflection.EntryPointCount; i++)
        {
            var entry = reflection.GetEntryPointByIndex(i);
            if (entry.Name != name) continue;
            matches++;
            valid = entry.Stage == SlangStage.Compute && HasSinglePointRole(entry.Function, role);
        }
        return matches == 1 && valid;
    }

    private readonly record struct LightingBuildPassData(DispatchArguments Dispatch)
    {
        internal static void Record(
            ref ComputePassCommandScope commands,
            in LightingBuildPassData data) =>
            commands.Dispatch(data.Dispatch);
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
    internal uint LightEntriesPerCell;
    internal uint DirectionalCount;
    internal uint NonDirectionalCount;
    internal uint ScreenWidth;
    internal uint ScreenHeight;
    internal uint CellCountPerView;
    internal uint GroupsPerLight;

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
            LightEntriesPerCell = settings.MaxLightsPerCell,
            DirectionalCount = directionalCount,
            NonDirectionalCount = nonDirectionalCount,
            // Share the grid's slice parallelism between lights without expanding every light by every slice.
            GroupsPerLight = DivideRoundUp(settings.DepthSliceCount, Math.Max(nonDirectionalCount, 1u)),
            Projection = view.Projection,
            ScreenWidth = view.ViewportWidth,
            ScreenHeight = view.ViewportHeight,
            CellCountPerView = cellCountPerView,
        };
    }

    private static uint DivideRoundUp(uint value, uint divisor) =>
        checked((value + divisor - 1u) / divisor);
}
