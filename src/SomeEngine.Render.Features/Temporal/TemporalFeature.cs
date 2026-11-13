using SomeEngine.ECS.Entities;
using SlangShaderSharp;
using SomeEngine.Assets.Schema;
using SomeEngine.Resources;
using SomeEngine.Graphics;
using SomeEngine.Render.Assets;
using SomeEngine.Render.Frame;
using SomeEngine.Render.Features.Motion;
using SomeEngine.RenderGraph;
using System.Numerics;

namespace SomeEngine.Render.Features.Temporal;

public readonly record struct TemporalFeatureOutput(
    GraphTextureId ResolvedColor,
    Format Format,
    int Width,
    int Height,
    GraphPassId CompletePass)
{
    public bool IsValid =>
        ResolvedColor != default && Format != default && Width > 0 && Height > 0 &&
        CompletePass != default;
}

/// <summary>
/// Owns temporal history, resolve and initialization passes. The feature consumes
/// only explicit color, depth, and motion outputs and has no Cluster shader operation role.
/// </summary>
public sealed class TemporalFeature : IDisposable
{

    private readonly RenderPipelines _renderPipelines;
    private readonly global::SomeEngine.RenderGraph.RenderGraph _graph;
    private static readonly TextureSubresourceRange ColorRange =
        new(0, 1, 0, 1, TextureAspects.Color);
    private static readonly TextureSubresourceRange HistoryRange =
        new(0, 1, 0, 2, TextureAspects.Color);

    private readonly LinkedRasterPipeline _pipeline;
    private readonly TemporalResource[] _resourceOrder;
    private readonly GraphTextureId _history;
    private uint _historyLayer;
    private Vector2 _previousJitterPixels;
    private readonly GraphPassId _initializePass;
    private readonly GraphPassId _resolvePass;
    private readonly GraphBufferInput _uniforms;
    private readonly GraphTextureInput _sceneColor;
    private readonly GraphTextureInput _sceneDepth;
    private readonly GraphTextureInput _sceneMotion;
    private bool _historyValid;
    private Entity _historyView;
    private bool _disposed;

    public TemporalFeature(
        IGraphicsBackend backend,
        Device device,
        RenderPipelines pipelines,
        global::SomeEngine.RenderGraph.RenderGraph graph,
        int width,
        int height,
        Shader shader,
        string vertexEntryPoint,
        string pixelEntryPoint,
        GraphRegionId region = default, Format depthFormat = Format.D32Float)
    {

        _renderPipelines = pipelines;
        _graph = graph;
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(shader);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (!ReferenceEquals(graph.Device, device))
            throw new ArgumentException("Temporal and RenderGraph must use the same Device.", nameof(graph));

        LinkedRasterPipeline? pipeline = null;
        try
        {
            pipeline = LinkedRasterPipeline.Create(
                _renderPipelines,
                shader,
                vertexEntryPoint,
                pixelEntryPoint,
                [Format.R16G16B16A16Float],
                rasterizer: new RasterizerState(Cull: CullType.None),
                label: "Temporal resolve");
            _resourceOrder = ShaderResourceLayout.Resolve<TemporalResource>(
                pipeline.Reflection.GetGlobalParamsVarLayout() ?? VariableLayoutReflection.Null,
                ResolveResource);

            using RenderGraphEdit edit = graph.BeginEdit();
            _history = edit.CreatePersistentTexture(new TextureDesc(
                TextureDimension.Texture2D,
                checked((uint)width), checked((uint)height), 1, 1, 2, 1,
                Format.R32G32B32A32UInt,
                TextureUsages.Storage | TextureUsages.ColorAttachment | TextureUsages.CopyDestination,
                label: "Temporal histories"));
            GraphTextureId resolved = edit.CreatePersistentTexture(TextureDescription(
                Format.R16G16B16A16Float,
                TextureUsages.Sampled | TextureUsages.ColorAttachment | TextureUsages.CopySource,
                "Temporal resolved color"));
            GraphColorAttachmentViewId resolvedAttachment = edit.CreateColorAttachmentView(
                resolved,
                ColorRange,
                Format.R16G16B16A16Float,
                SomeEngine.Graphics.TextureViewDimension.Texture2D,
                "Temporal output");
            _uniforms = edit.CreateBufferInput(BufferUsages.Constant);
            _sceneColor = edit.CreateTextureInput();
            _sceneDepth = edit.CreateTextureInput();
            _sceneMotion = edit.CreateTextureInput();
            _initializePass = edit.AddCopyFramePass(
                "Initialize temporal histories",
                PassQueueSelection.AnyOfType(QueueType.Graphics),
                new TemporalInitializePassData(_history),
                new PassOptions(Culling: PassCullingMode.NeverCull, RepeatCommands: true),
                static (ref PassDefinition pass, ref TemporalInitializePassData data) => data.Declare(ref pass),
                TemporalInitializePassData.Record, region: region);
            _resolvePass = edit.AddRasterFramePass(
                "Temporal resolve",
                PassQueueSelection.AnyOfType(QueueType.Graphics),
                new TemporalResolvePassData(resolvedAttachment, width, height),
                new PassOptions(Culling: PassCullingMode.NeverCull),
                pipeline.Pipeline,
                pipeline.Reflection.GetGlobalParamsVarLayout() ?? VariableLayoutReflection.Null,
                default,
                TemporalResolvePassData.Declare,
                TemporalResolvePassData.Record, region: region);
            edit.OrderAfter(_resolvePass, _initializePass);
            PassDefinition resolve = edit.GetPass(_resolvePass);
            foreach (TemporalResource resource in _resourceOrder)
            {
                switch (resource)
                {
                    case TemporalResource.Uniforms:
                        _ = resolve.Bind(
                            _uniforms,
                            PipelineSync.PixelShading);
                        break;
                    case TemporalResource.SceneColor:
                        _ = resolve.Bind(edit.CreateTextureSrv(_sceneColor, new TextureSubresourceRange(0, 1, 0, 1, TextureAspects.Color), Format.R16G16B16A16Float, SomeEngine.Graphics.TextureViewDimension.Texture2DArray, "Temporal scene color"), PipelineSync.PixelShading);
                        break;
                    case TemporalResource.MotionVectors:
                        _ = resolve.Bind(edit.CreateTextureSrv(_sceneMotion, new TextureSubresourceRange(0, 1, 0, 1, TextureAspects.Color), Format.R16G16Float, SomeEngine.Graphics.TextureViewDimension.Texture2DArray, "Temporal motion"), PipelineSync.PixelShading);
                        break;
                    case TemporalResource.SceneDepth:
                        TextureAspects depthAspect = depthFormat is Format.D16UNorm or Format.D32Float
                            or Format.D24UNormS8UInt or Format.D32FloatS8UInt ? TextureAspects.Depth : TextureAspects.Color;
                        _ = resolve.Bind(edit.CreateTextureSrv(_sceneDepth, new TextureSubresourceRange(0, 1, 0, 1, depthAspect),
                            depthFormat, SomeEngine.Graphics.TextureViewDimension.Texture2DArray, "Temporal scene depth"), PipelineSync.PixelShading);
                        break;
                    case TemporalResource.History:
                        _ = resolve.Bind(edit.CreateTextureUav(_history, HistoryRange,
                            Format.R32G32B32A32UInt, SomeEngine.Graphics.TextureViewDimension.Texture2DArray,
                            "Temporal histories"), PipelineSync.PixelShading);
                        break;
                    default:
                        throw new InvalidOperationException("Unknown temporal resource key.");
                }
            }
            edit.Commit();
            Output = new TemporalFeatureOutput(
                resolved,
                Format.R16G16B16A16Float,
                width,
                height,
                _resolvePass);
            _pipeline = pipeline;
            pipeline = null;

            TextureDesc TextureDescription(Format format, TextureUsages usages, string label) => new(
                TextureDimension.Texture2D,
                checked((uint)width),
                checked((uint)height),
                1, 1, 1, 1,
                format,
                usages,
                label: label);
        }
        catch
        {
            pipeline?.Dispose();
            throw;
        }
    }

    public TemporalFeatureOutput Output { get; }

    public void OrderAfter(ref RenderGraphEdit graph, GraphPassId producer)
    {
        graph.OrderAfter(_initializePass, producer);
        graph.OrderAfter(_resolvePass, producer);
    }

    public void Record(
        ref RenderGraphFrame graph,
        GraphTextureId sceneColor,
        GraphTextureId sceneDepth,
        in MotionFeatureOutput motion,
        TemporalResolveSettings settings,
        bool enabled = true,
        bool cameraCut = false,
        Entity view = default,
        Vector2 jitterPixels = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (sceneColor == default || sceneDepth == default || !motion.IsValid)
            throw new ArgumentException("Temporal resolve requires valid color, depth, and motion inputs.");

        bool sameView = view != Entity.Null && view == _historyView;
        bool useHistory = enabled && _historyValid && !cameraCut && sameView;
        TemporalResolveUniforms uniforms = useHistory
            ? settings.ToUniforms()
            : settings.ToUniforms() with { HistoryWeight = 0.0f };
        uniforms = uniforms with
        {
            HistoryLayer = _historyLayer,
            JitterDifferenceUv = useHistory
                ? (jitterPixels - _previousJitterPixels) /
                  new Vector2(motion.Width, motion.Height)
                : Vector2.Zero,
        };
        GraphBufferSlice uniformBuffer = FeatureUploads.UploadConstant(
            ref graph,
            in uniforms,
            "Temporal resolve uniforms");

        graph.SetBufferInput(_uniforms, uniformBuffer);
        graph.SetTextureInput(_sceneColor, sceneColor);
        graph.SetTextureInput(_sceneDepth, sceneDepth);
        graph.SetTextureInput(_sceneMotion, motion.MotionVectors);
        graph.SetPassEnabled(_initializePass, !_historyValid || cameraCut || !sameView);
        graph.SetPassEnabled(_resolvePass, true);
        graph.ObserveExecution(new HistorySubmission(this, view, jitterPixels, _historyLayer ^ 1u));
    }

    private sealed class HistorySubmission(TemporalFeature feature, Entity view, Vector2 jitter, uint layer)
        : IRenderGraphFrameExecutionObserver
    {
        public void OnFrameSubmitted(in RenderGraphSubmission submission)
        {
            feature._historyLayer = layer;
            feature._historyView = view;
            feature._historyValid = view != Entity.Null;
            feature._previousJitterPixels = jitter;
        }
    }

    public void InvalidateHistory()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _historyValid = false;
    }

    public void ReplaceShader(Shader previous, Shader next)
    {
        if (!_pipeline.ReplaceShader(_renderPipelines, previous, next)) return;
        using RenderGraphEdit edit = _graph.BeginEdit();
        PassDefinition pass = edit.GetPass(_resolvePass);
        pass.SetPipeline(_pipeline.Pipeline);
        pass.SetParameterBlock(_pipeline.Reflection.GetGlobalParamsVarLayout() ?? VariableLayoutReflection.Null);
        edit.Commit();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _pipeline.Dispose();
        _historyValid = false;
        _disposed = true;
    }

    private static TemporalResource? ResolveResource(string name, ResourceBindingType type) =>
        (name, type) switch
        {
            ("TemporalResolveUniforms", ResourceBindingType.ConstantBuffer) => TemporalResource.Uniforms,
            ("SceneColor", ResourceBindingType.TextureSrv) => TemporalResource.SceneColor,
            ("MotionVectors", ResourceBindingType.TextureSrv) => TemporalResource.MotionVectors,
            ("SceneDepth", ResourceBindingType.TextureSrv) => TemporalResource.SceneDepth,
            ("TemporalHistory", ResourceBindingType.TextureUav) => TemporalResource.History,
            _ => null,
        };

    private enum TemporalResource : byte
    {
        Uniforms,
        SceneColor,
        MotionVectors,
        SceneDepth,
        History,
    }

    private readonly record struct TemporalResolvePassData(
        GraphColorAttachmentViewId Output,
        int Width,
        int Height)
    {
        internal static void Declare(ref PassDefinition pass, ref TemporalResolvePassData data) =>
            pass.ColorAttachment(
                0,
                data.Output,
                LoadType.Clear,
                StoreType.Store,
                WriteCoverage.Complete,
                new System.Numerics.Vector4(0, 0, 0, 1));

        internal static void Record(
            ref RasterPassCommandScope commands,
            in TemporalResolvePassData data)
        {
            commands.SetViewports([new Viewport(0, 0, data.Width, data.Height)]);
            commands.SetScissors([new ScissorRect(0, 0, data.Width, data.Height)]);
            commands.Draw(new DrawArguments(3, 1, 0, 0));
        }
    }

    private readonly record struct TemporalInitializePassData(GraphTextureId History)
    {
        internal void Declare(ref PassDefinition pass) =>
            _ = pass.Write(History, HistoryRange, PipelineSync.RenderTarget,
                ResourceAccess.RenderTarget, TextureLayout.RenderTarget, WriteCoverage.Complete);

        internal static void Record(
            ref CopyPassCommandScope commands,
            in TemporalInitializePassData data) =>
            commands.ClearTexture(commands.GetTexture(data.History), HistoryRange, default);
    }
}
