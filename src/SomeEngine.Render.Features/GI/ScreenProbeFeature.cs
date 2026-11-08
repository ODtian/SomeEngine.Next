using SomeEngine.Graphics;
using SomeEngine.Render.Assets;
using SomeEngine.Render.Cluster.Pipeline;
using SomeEngine.RenderGraph;
using SomeEngine.Resources;
using TextureViewDimension = SomeEngine.Graphics.TextureViewDimension;

namespace SomeEngine.Render.Features.GI;

/// <summary>Maintains directional radiance sampled during material shading and updated from material outputs.</summary>
public sealed class ScreenProbeFeature : IGraphResourceProvider, IRenderGraphFrameExecutionObserver, IDisposable
{
    private readonly int _width;
    private readonly int _height;
    private readonly uint _views;
    private readonly GraphRegionId _traceRegion;
    private readonly GraphRegionId _sceneRegion;
    private readonly GraphBufferInput _captureSettings;
    private readonly GraphBufferInput _screenSettings;
    private readonly FeatureUniformBuffer _uniformData;
    private readonly GraphTextureId _normal;
    private readonly GraphTextureId _diffuse;
    private readonly GraphTextureId _reflectedRadiance;
    private readonly GraphBufferId _probeSurfaces;
    private readonly GraphBufferId _radiance;
    private readonly LinkedComputePipeline[] _pipelines;
    private GraphPassId _initializePass;
    private GraphPassId _tracePass;
    private uint _frame;

    public ScreenProbeFeature(IGraphicsBackend backend, Device device,
        global::SomeEngine.RenderGraph.RenderGraph graph, int width, int height, uint viewCount,
        Shader? shader, bool enabled, GraphRegionId sceneRegion = default, GraphRegionId traceRegion = default)
    {
        (_width, _height, _views, Enabled) = (width, height, viewCount, enabled);
        _traceRegion = traceRegion;
        _sceneRegion = sceneRegion;
        RenderGraphEdit edit = graph.BeginEdit();
        try
        {
            _captureSettings = edit.CreateBufferInput(BufferUsages.Constant);
            _screenSettings = edit.CreateBufferInput(BufferUsages.Constant);
            _uniformData = new FeatureUniformBuffer(ref edit,
                enabled ? [_captureSettings, _screenSettings] : [_captureSettings],
                device.Capabilities.Limits.ConstantBufferAlignment, "GI constant data");
            if (enabled)
            {
                _normal = edit.CreatePersistentTexture(SurfaceTexture(Format.R16G16B16A16Float, "GI material normals"));
                _diffuse = edit.CreatePersistentTexture(SurfaceTexture(Format.R8G8B8A8UNorm, "GI material diffuse"));
                _reflectedRadiance = edit.CreatePersistentTexture(SurfaceTexture(Format.R11G11B10Float, "GI material reflected radiance"));
                uint count = ProbeWidth * ProbeHeight * viewCount;
                _probeSurfaces = edit.CreatePersistentBuffer(new BufferDesc((ulong)count * 2u * 32u,
                    BufferUsages.ShaderRead | BufferUsages.ShaderWrite, "GI probe surfaces"));
                _radiance = edit.CreatePersistentBuffer(new BufferDesc((ulong)count * 2u * 9u * 16u,
                    BufferUsages.ShaderRead | BufferUsages.ShaderWrite, "GI probe directional radiance"));
                var clearData = new SurfaceClearData(_normal, _diffuse, _reflectedRadiance, viewCount);
                ClearSurfacesPass = edit.AddCopyFramePass("Clear GI material surfaces",
                    PassQueueSelection.AnyOfType(QueueType.Graphics), clearData,
                    new PassOptions(Culling: PassCullingMode.NeverCull, RepeatCommands: true),
                    SurfaceClearData.Declare, SurfaceClearData.Record, region: sceneRegion);
                _pipelines = [
                    LinkedComputePipeline.Create(backend, device, shader!, "InitializeGiScreenProbes", "Initialize GI probe history"),
                    LinkedComputePipeline.Create(backend, device, shader!, "TraceGiScreenProbes", "Trace GI screen probes"),
                ];
            }
            else _pipelines = [];
            edit.Commit();
        }
        finally { edit.Dispose(); }

        TextureDesc SurfaceTexture(Format format, string label) => new(TextureDimension.Texture2D,
            (uint)width, (uint)height, 1, 1, viewCount, 1,
            format, TextureUsages.Sampled | TextureUsages.Storage | TextureUsages.CopyDestination |
                TextureUsages.ColorAttachment, label: label);
    }

    public bool Enabled { get; }
    public GraphPassId ClearSurfacesPass { get; }
    public GraphPassId CompletePass => _tracePass;
    private uint ProbeWidth => ((uint)_width + 7u) / 8u;
    private uint ProbeHeight => ((uint)_height + 7u) / 8u;

    /// <summary>Connects existing depth, visibility, view data and motion; material evaluation remains in Cluster shade.</summary>
    public void Connect(ref RenderGraphEdit graph, in ClusterPipelineOutputs surfaces)
    {
        if (!Enabled) return;
        if (_tracePass == default)
        {
            uint count = ProbeWidth * ProbeHeight * _views;
            _initializePass = AddPass(ref graph, _pipelines[0], "Initialize GI screen probes",
                new DispatchArguments((count * 2u + 63u) / 64u, 1, 1), surfaces, ProbePass.Initialize);
            _tracePass = AddPass(ref graph, _pipelines[1], "Trace GI screen probes",
                new DispatchArguments((count + 63u) / 64u, 1, 1), surfaces, ProbePass.Trace);
            graph.OrderAfter(ClearSurfacesPass, _initializePass);
            graph.OrderAfter(surfaces.Geometry.CompletePass, ClearSurfacesPass);
            graph.OrderAfter(_tracePass, _initializePass);
        }
    }

    public void Record(ref RenderGraphFrame graph, bool cameraCut)
    {
        GiCaptureSettings capture = new(Enabled ? 1u : 0u, 0, 0, 0);
        _uniformData.Write(ref graph, _captureSettings, in capture);
        if (Enabled)
        {
            GiScreenSettings settings = new((uint)_width, (uint)_height, ProbeWidth, ProbeHeight,
                _frame, _frame & 1u, _frame != 0u && !cameraCut ? 1u : 0u, _views);
            _uniformData.Write(ref graph, _screenSettings, in settings);
            graph.SetPassEnabled(_initializePass, _frame == 0u || cameraCut);
            graph.ObserveExecution(this);
        }
        _uniformData.Upload(ref graph);
    }

    void IRenderGraphFrameExecutionObserver.OnRenderGraphFrameFinished(RenderGraphFrameResult result)
    {
        if (result.TryGetPassCompletion(_tracePass, out _))
            _frame++;
    }

    public bool TryResolve(string name, ResourceBindingType type, out GraphResourceBinder binder)
    {
        binder = (name, type) switch
        {
            ("GiCapture", ResourceBindingType.ConstantBuffer) => BindCaptureSettings,
            ("GiSettings", ResourceBindingType.ConstantBuffer) => BindProbeSettings,
            ("GiPreviousProbeSurfaces", ResourceBindingType.BufferSrv) => BindProbeSurfaces,
            ("GiPreviousProbeRadiance", ResourceBindingType.BufferSrv) => BindProbeRadiance,
            ("GiSurfaceNormal", ResourceBindingType.TextureUav) => BindMaterialNormal,
            ("GiSurfaceDiffuse", ResourceBindingType.TextureUav) => BindMaterialDiffuse,
            ("GiSurfaceRadiance", ResourceBindingType.TextureUav) => BindMaterialRadiance,
            _ => null!,
        };
        return binder is not null;
    }

    private void BindCaptureSettings(ref RenderGraphEdit graph, ref PassDefinition pass, PipelineSync sync) =>
        _ = pass.Bind(_captureSettings, sync);

    private void BindProbeSettings(ref RenderGraphEdit graph, ref PassDefinition pass, PipelineSync sync)
    {
        if (Enabled) _ = pass.Bind(_screenSettings, sync);
        else pass.Bind(GraphParameterResourceBinding.Null(ResourceBindingType.ConstantBuffer));
    }

    private void BindProbeSurfaces(ref RenderGraphEdit graph, ref PassDefinition pass, PipelineSync sync) =>
        BindProbeBuffer(ref graph, ref pass, _probeSurfaces, 32, sync);

    private void BindProbeRadiance(ref RenderGraphEdit graph, ref PassDefinition pass, PipelineSync sync) =>
        BindProbeBuffer(ref graph, ref pass, _radiance, 16, sync);

    private void BindProbeBuffer(ref RenderGraphEdit graph, ref PassDefinition pass,
        GraphBufferId buffer, uint stride, PipelineSync sync)
    {
        if (Enabled) _ = pass.Bind(graph.CreateBufferSrv(buffer, structureStride: stride), sync);
        else pass.Bind(GraphParameterResourceBinding.Null(ResourceBindingType.BufferSrv));
    }

    private void BindMaterialNormal(ref RenderGraphEdit graph, ref PassDefinition pass, PipelineSync sync) =>
        BindMaterialOutput(ref graph, ref pass, _normal, Format.R16G16B16A16Float, sync);

    private void BindMaterialDiffuse(ref RenderGraphEdit graph, ref PassDefinition pass, PipelineSync sync) =>
        BindMaterialOutput(ref graph, ref pass, _diffuse, Format.R8G8B8A8UNorm, sync);

    private void BindMaterialRadiance(ref RenderGraphEdit graph, ref PassDefinition pass, PipelineSync sync) =>
        BindMaterialOutput(ref graph, ref pass, _reflectedRadiance, Format.R11G11B10Float, sync);

    private void BindMaterialOutput(ref RenderGraphEdit graph, ref PassDefinition pass,
        GraphTextureId texture, Format format, PipelineSync sync)
    {
        if (Enabled)
            pass.Bind(graph.CreateTextureUav(texture, format: format, dimension: TextureViewDimension.Texture2DArray),
                sync, GraphAccessMode.Write, WriteCoverage.Partial);
        else
            pass.Bind(GraphParameterResourceBinding.Null(ResourceBindingType.TextureUav));
    }

    private GraphPassId AddPass(ref RenderGraphEdit graph, LinkedComputePipeline pipeline, string name,
        DispatchArguments dispatch, in ClusterPipelineOutputs surfaces, ProbePass operation)
    {
        GraphPassId id = graph.AddComputeFramePass(name, PassQueueSelection.AnyOfType(QueueType.Graphics),
            dispatch, new PassOptions(Culling: PassCullingMode.NeverCull, RepeatCommands: true),
            pipeline.Pipeline, pipeline.ParameterLayout, default,
            static (ref PassDefinition _, ref DispatchArguments _) => { },
            static (ref ComputePassCommandScope commands, in DispatchArguments data) => commands.Dispatch(data),
            region: operation == ProbePass.Initialize ? _sceneRegion : _traceRegion);
        PassDefinition pass = graph.GetPass(id);
        var resources = ShaderResourceLayout.Resolve<(Resource Id, ResourceBindingType Type)>(pipeline.ParameterLayout,
            static (name, type) => (name switch
            {
                "GiSettings" => Resource.Settings,
                "GiViews" => Resource.Views,
                "GiDepth" => Resource.Depth,
                "GiVisibility" => Resource.Visibility,
                "GiMotion" => Resource.Motion,
                "GiNormal" => Resource.Normal,
                "GiDiffuse" => Resource.Diffuse,
                "GiReflectedRadiance" => Resource.ReflectedRadiance,
                "GiProbeSurfaces" => Resource.ProbeSurfaces,
                "GiProbeRadiance" => Resource.Radiance,
                _ => throw new InvalidDataException($"Unknown GI resource '{name}'."),
            }, type));
        foreach ((Resource resource, ResourceBindingType type) in resources)
        {
            if (!UsesResource(operation, resource))
            {
                pass.Bind(GraphParameterResourceBinding.Null(type));
                continue;
            }
            switch (resource)
            {
                case Resource.Settings: _ = pass.Bind(_screenSettings, PipelineSync.ComputeShading); break;
                case Resource.Views:
                    _ = pass.Bind(graph.CreateBufferSrv(surfaces.Geometry.PackedViews,
                        structureStride: surfaces.Geometry.PackedViewStride), PipelineSync.ComputeShading);
                    break;
                case Resource.Depth:
                    _ = pass.Bind(graph.CreateTextureSrv(surfaces.Depth,
                        new TextureSubresourceRange(0, 1, 0, _views, TextureAspects.Depth), Format.D32Float,
                        TextureViewDimension.Texture2DArray), PipelineSync.ComputeShading);
                    break;
                case Resource.Visibility: BindTexture(ref graph, ref pass, surfaces.Visibility, Format.R32UInt); break;
                case Resource.Motion: BindTexture(ref graph, ref pass, surfaces.MotionVectors, Format.R16G16Float); break;
                case Resource.Normal: BindTexture(ref graph, ref pass, _normal, Format.R16G16B16A16Float); break;
                case Resource.Diffuse: BindTexture(ref graph, ref pass, _diffuse, Format.R8G8B8A8UNorm); break;
                case Resource.ReflectedRadiance: BindTexture(ref graph, ref pass, _reflectedRadiance, Format.R11G11B10Float); break;
                case Resource.ProbeSurfaces:
                    _ = pass.Bind(graph.CreateBufferUav(_probeSurfaces, structureStride: 32), PipelineSync.ComputeShading,
                        ProbeAccess(operation), operation == ProbePass.Initialize ? WriteCoverage.Complete : WriteCoverage.Partial);
                    break;
                case Resource.Radiance:
                    _ = pass.Bind(graph.CreateBufferUav(_radiance, structureStride: 16), PipelineSync.ComputeShading,
                        ProbeAccess(operation), operation == ProbePass.Initialize ? WriteCoverage.Complete : WriteCoverage.Partial);
                    break;
            }
        }
        return id;
    }

    private static bool UsesResource(ProbePass operation, Resource resource) => resource switch
    {
        Resource.Settings or Resource.ProbeSurfaces or Resource.Radiance => true,
        Resource.Views or Resource.Depth or Resource.Normal or Resource.Diffuse => operation != ProbePass.Initialize,
        Resource.Visibility or Resource.Motion or Resource.ReflectedRadiance => operation == ProbePass.Trace,
        _ => false,
    };

    private static GraphAccessMode ProbeAccess(ProbePass operation) => operation switch
    {
        ProbePass.Initialize => GraphAccessMode.Write,
        ProbePass.Trace => GraphAccessMode.ReadWrite,
        _ => GraphAccessMode.Read,
    };

    private static void BindTexture(ref RenderGraphEdit graph, ref PassDefinition pass, GraphTextureId texture, Format format) =>
        _ = pass.Bind(graph.CreateTextureSrv(texture, format: format, dimension: TextureViewDimension.Texture2DArray),
            PipelineSync.ComputeShading);

    public void Dispose()
    {
        foreach (LinkedComputePipeline pipeline in _pipelines) pipeline.Dispose();
    }

    private enum Resource { Settings, Views, Depth, Visibility, Motion, Normal, Diffuse, ReflectedRadiance, ProbeSurfaces, Radiance }
    private enum ProbePass { Initialize, Trace }
    private readonly record struct GiCaptureSettings(uint Enabled, uint Padding0, uint Padding1, uint Padding2);
    private readonly record struct GiScreenSettings(uint Width, uint Height, uint ProbeWidth, uint ProbeHeight,
        uint Frame, uint HistoryLayer, uint UseHistory, uint ViewCount);
    private readonly record struct SurfaceClearData(GraphTextureId Normal, GraphTextureId Diffuse, GraphTextureId Radiance, uint Views)
    {
        internal static void Declare(ref PassDefinition pass, ref SurfaceClearData data)
        {
            var range = new TextureSubresourceRange(0, 1, 0, data.Views, TextureAspects.Color);
            _ = pass.Write(data.Normal, range, PipelineSync.Copy, ResourceAccess.CopyDestination,
                TextureLayout.CopyDestination, WriteCoverage.Complete);
            _ = pass.Write(data.Diffuse, range, PipelineSync.Copy, ResourceAccess.CopyDestination,
                TextureLayout.CopyDestination, WriteCoverage.Complete);
            _ = pass.Write(data.Radiance, range, PipelineSync.Copy, ResourceAccess.CopyDestination,
                TextureLayout.CopyDestination, WriteCoverage.Complete);
        }

        internal static void Record(ref CopyPassCommandScope commands, in SurfaceClearData data)
        {
            var range = new TextureSubresourceRange(0, 1, 0, data.Views, TextureAspects.Color);
            commands.ClearTexture(commands.GetTexture(data.Normal), range, default);
            commands.ClearTexture(commands.GetTexture(data.Diffuse), range, default);
            commands.ClearTexture(commands.GetTexture(data.Radiance), range, default);
        }
    }
}
