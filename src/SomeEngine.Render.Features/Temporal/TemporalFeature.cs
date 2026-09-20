using SomeEngine.ECS.Entities;
using System.Runtime.InteropServices;
using SomeEngine.Assets.Schema;
using SomeEngine.Resources;
using SomeEngine.Graphics;
using SomeEngine.Render.Assets;
using SomeEngine.Render.Frame;
using SomeEngine.Render.Features.Motion;
using SomeEngine.RenderGraph;

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
/// Owns temporal history, resolve, initialization, and history-update passes. The feature consumes
/// only explicit color, depth, and motion outputs and has no Cluster shader operation role.
/// </summary>
public sealed class TemporalFeature : IDisposable, IRenderGraphFrameExecutionObserver
{
    private static readonly TextureSubresourceRange ColorRange =
        new(0, 1, 0, 1, TextureAspects.Color);
    private static readonly TextureSubresourceRange DepthRange =
        new(0, 1, 0, 1, TextureAspects.Depth);

    private readonly LinkedRasterPipeline _pipeline;
    private readonly TemporalResource[] _resourceOrder;
    private readonly GraphTextureId _colorHistory;
    private readonly GraphTextureId _motionHistory;
    private readonly GraphTextureId _depthHistory;
    private readonly GraphPassId _initializePass;
    private readonly GraphPassId _resolvePass;
    private readonly GraphPassId _historyPass;
    private readonly GraphBufferInput _uniforms;
    private readonly GraphTextureInput _sceneColor;
    private readonly GraphTextureInput _sceneDepth;
    private readonly GraphTextureInput _sceneMotion;
    private bool _historyValid;
    private Entity _historyView;
    private Entity _frameView;
    private bool _frameActive;
    private bool _disposed;

    public TemporalFeature(
        IGraphicsBackend backend,
        Device device,
        global::SomeEngine.RenderGraph.RenderGraph graph,
        int width,
        int height,
        Shader shader,
        string vertexEntryPoint,
        string pixelEntryPoint)
    {
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
                backend,
                device,
                shader,
                vertexEntryPoint,
                pixelEntryPoint,
                [Format.R16G16B16A16Float],
                rasterizer: new RasterizerState(Cull: CullType.None),
                label: "Temporal resolve");
            _resourceOrder = FeatureResourceLayout.Resolve<TemporalResource>(
                pipeline.Program.Layout.GetGlobalParamsVarLayout(),
                ResolveResource);

            using RenderGraphEdit edit = graph.BeginEdit();
            TextureDesc colorHistory = new(
                TextureDimension.Texture2D,
                checked((uint)width),
                checked((uint)height),
                1, 1, 1, 1,
                Format.R16G16B16A16Float,
                TextureUsages.Sampled | TextureUsages.ColorAttachment |
                    TextureUsages.CopySource | TextureUsages.CopyDestination,
                label: "Temporal color history");
            TextureDesc motionHistory = new(
                TextureDimension.Texture2D,
                checked((uint)width),
                checked((uint)height),
                1, 1, 1, 1,
                Format.R16G16Float,
                TextureUsages.Sampled | TextureUsages.ColorAttachment |
                    TextureUsages.CopySource | TextureUsages.CopyDestination,
                label: "Temporal motion history");
            TextureDesc depthHistory = new(
                TextureDimension.Texture2D,
                checked((uint)width),
                checked((uint)height),
                1, 1, 1, 1,
                Format.D32Float,
                TextureUsages.Sampled | TextureUsages.DepthStencilAttachment |
                    TextureUsages.CopySource | TextureUsages.CopyDestination,
                label: "Temporal depth history");
            _colorHistory = edit.CreatePersistentTexture(TextureDescription(
                colorHistory.Format,
                colorHistory.Usages,
                "Temporal color history"));
            _motionHistory = edit.CreatePersistentTexture(TextureDescription(
                motionHistory.Format,
                motionHistory.Usages,
                "Temporal motion history"));
            _depthHistory = edit.CreatePersistentTexture(TextureDescription(
                depthHistory.Format,
                depthHistory.Usages,
                "Temporal depth history"));
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
                new TemporalInitializePassData(_colorHistory, _motionHistory, _sceneDepth, _depthHistory,
                    checked((uint)width), checked((uint)height)),
                new PassOptions(Culling: PassCullingMode.NeverCull, RepeatCommands: true),
                static (ref PassDefinition pass, ref TemporalInitializePassData data) => data.Declare(ref pass),
                TemporalInitializePassData.Record);
            _resolvePass = edit.AddRasterFramePass(
                "Temporal resolve",
                PassQueueSelection.AnyOfType(QueueType.Graphics),
                new TemporalResolvePassData(resolvedAttachment, width, height),
                new PassOptions(Culling: PassCullingMode.NeverCull, RepeatCommands: true),
                pipeline.Pipeline,
                pipeline.Program.Layout.GetGlobalParamsVarLayout(),
                default,
                TemporalResolvePassData.Declare,
                TemporalResolvePassData.Record);
            _historyPass = edit.AddCopyFramePass(
                "Update temporal history",
                PassQueueSelection.AnyOfType(QueueType.Graphics),
                new TemporalHistoryPassData(resolved, _colorHistory, _sceneMotion, _motionHistory,
                    _sceneDepth, _depthHistory, checked((uint)width), checked((uint)height)),
                new PassOptions(Culling: PassCullingMode.NeverCull, RepeatCommands: true),
                static (ref PassDefinition pass, ref TemporalHistoryPassData data) => data.Declare(ref pass),
                TemporalHistoryPassData.Record);
            edit.OrderAfter(_resolvePass, _initializePass);
            edit.OrderAfter(_historyPass, _resolvePass);
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
                    case TemporalResource.PreviousSceneColor:
                        BindTexture(edit, ref resolve, _colorHistory, Format.R16G16B16A16Float, TextureAspects.Color, 0, SomeEngine.Graphics.TextureViewDimension.Texture2D, "Temporal previous color");
                        break;
                    case TemporalResource.MotionVectors:
                        _ = resolve.Bind(edit.CreateTextureSrv(_sceneMotion, new TextureSubresourceRange(0, 1, 0, 1, TextureAspects.Color), Format.R16G16Float, SomeEngine.Graphics.TextureViewDimension.Texture2DArray, "Temporal motion"), PipelineSync.PixelShading);
                        break;
                    case TemporalResource.PreviousMotionVectors:
                        BindTexture(edit, ref resolve, _motionHistory, Format.R16G16Float, TextureAspects.Color, 0, SomeEngine.Graphics.TextureViewDimension.Texture2D, "Temporal previous motion");
                        break;
                    case TemporalResource.SceneDepth:
                        _ = resolve.Bind(edit.CreateTextureSrv(_sceneDepth, new TextureSubresourceRange(0, 1, 0, 1, TextureAspects.Depth), Format.D32Float, SomeEngine.Graphics.TextureViewDimension.Texture2DArray, "Temporal scene depth"), PipelineSync.PixelShading);
                        break;
                    case TemporalResource.PreviousSceneDepth:
                        BindTexture(edit, ref resolve, _depthHistory, Format.D32Float, TextureAspects.Depth, 0, SomeEngine.Graphics.TextureViewDimension.Texture2D, "Temporal previous depth");
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
        Entity view = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_frameActive)
            throw new InvalidOperationException("The previous temporal graph frame is still active.");
        if (sceneColor == default || sceneDepth == default || !motion.IsValid)
            throw new ArgumentException("Temporal resolve requires valid color, depth, and motion inputs.");

        bool sameView = view != Entity.Null && view == _historyView;
        bool useHistory = enabled && _historyValid && !cameraCut && sameView;
        TemporalResolveUniforms uniforms = useHistory
            ? settings.ToUniforms()
            : settings.ToUniforms() with { HistoryWeight = 0.0f };
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
        graph.SetPassEnabled(_historyPass, true);
        graph.ObserveExecution(this);
        _frameView = view;
        _frameActive = true;
    }

    void IRenderGraphFrameExecutionObserver.OnRenderGraphFrameFinished(RenderGraphFrameResult result)
    {
        if (_disposed || !_frameActive)
            return;
        // A submitted copy pass contains all three history writes. An interrupted frame
        // may have initialized the textures without producing the next history.
        if (result.TryGetPassCompletion(_historyPass, out _))
        {
            _historyView = _frameView;
            _historyValid = _frameView != Entity.Null;
        }
        else if (result.TryGetPassCompletion(_initializePass, out _))
            _historyValid = false;
        _frameActive = false;
    }

    public void InvalidateHistory()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_frameActive)
            throw new InvalidOperationException("Temporal history cannot be invalidated during an open frame.");
        _historyValid = false;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        if (_frameActive)
            throw new InvalidOperationException("Temporal resources cannot be disposed during an open graph frame.");
        _pipeline.Dispose();
        _historyValid = false;
        _disposed = true;
    }

    private static TemporalResource? ResolveResource(string name, ResourceBindingType type) =>
        (name, type) switch
        {
            ("TemporalResolveUniforms", ResourceBindingType.ConstantBuffer) => TemporalResource.Uniforms,
            ("SceneColor", ResourceBindingType.TextureSrv) => TemporalResource.SceneColor,
            ("PreviousSceneColor", ResourceBindingType.TextureSrv) => TemporalResource.PreviousSceneColor,
            ("MotionVectors", ResourceBindingType.TextureSrv) => TemporalResource.MotionVectors,
            ("PreviousMotionVectors", ResourceBindingType.TextureSrv) => TemporalResource.PreviousMotionVectors,
            ("SceneDepth", ResourceBindingType.TextureSrv) => TemporalResource.SceneDepth,
            ("PreviousSceneDepth", ResourceBindingType.TextureSrv) => TemporalResource.PreviousSceneDepth,
            _ => null,
        };

    private static void BindTexture(
        RenderGraphEdit graph,
        ref PassDefinition pass,
        GraphTextureId texture,
        Format format,
        TextureAspects aspects,
        uint layer,
        SomeEngine.Graphics.TextureViewDimension dimension,
        string label)
    {
        _ = pass.Bind(
            graph.CreateTextureSrv(
                texture,
                new TextureSubresourceRange(0, 1, layer, 1, aspects),
                format,
                dimension,
                label),
            PipelineSync.PixelShading);
    }

    private static void DeclareCopy(
        ref PassDefinition pass,
        GraphTextureId source,
        GraphTextureId destination,
        in TextureSubresourceRange sourceRange,
        in TextureSubresourceRange destinationRange)
    {
        _ = pass.Read(
            source,
            sourceRange,
            PipelineSync.Copy,
            ResourceAccess.CopySource,
            TextureLayout.CopySource);
        _ = pass.Write(
            destination,
            destinationRange,
            PipelineSync.Copy,
            ResourceAccess.CopyDestination,
            TextureLayout.CopyDestination,
            WriteCoverage.Complete);
    }

    private enum TemporalResource : byte
    {
        Uniforms,
        SceneColor,
        PreviousSceneColor,
        MotionVectors,
        PreviousMotionVectors,
        SceneDepth,
        PreviousSceneDepth,
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

    private readonly record struct TemporalInitializePassData(
        GraphTextureId Color,
        GraphTextureId Motion,
        GraphTextureInput SourceDepth,
        GraphTextureId Depth,
        uint Width,
        uint Height)
    {
        internal void Declare(ref PassDefinition pass)
        {
            DeclareColor(ref pass, Color);
            DeclareColor(ref pass, Motion);
            _ = pass.Read(
                SourceDepth,
                DepthRange,
                PipelineSync.Copy,
                ResourceAccess.CopySource,
                TextureLayout.CopySource);
            DeclareDepth(ref pass, Depth);
        }

        internal static void Record(
            ref CopyPassCommandScope commands,
            in TemporalInitializePassData data)
        {
            Clear(ref commands, data.Color);
            Clear(ref commands, data.Motion);
            CopyDepth(ref commands, data.SourceDepth, data.Depth, data.Width, data.Height);
        }

        private static void DeclareColor(ref PassDefinition pass, GraphTextureId texture) =>
            _ = pass.Write(
                texture,
                ColorRange,
                PipelineSync.Copy,
                ResourceAccess.CopyDestination,
                TextureLayout.CopyDestination,
                WriteCoverage.Complete);

        private static void DeclareDepth(ref PassDefinition pass, GraphTextureId texture) =>
            _ = pass.Write(
                texture,
                DepthRange,
                PipelineSync.Copy,
                ResourceAccess.CopyDestination,
                TextureLayout.CopyDestination,
                WriteCoverage.Complete);

        private static void Clear(ref CopyPassCommandScope commands, GraphTextureId texture) =>
            commands.ClearTexture(commands.GetTexture(texture), ColorRange, default);

        private static void CopyDepth(
            ref CopyPassCommandScope commands,
            GraphTextureInput source,
            GraphTextureId destination,
            uint width,
            uint height) =>
            commands.CopyTexture(new TextureCopy(
                commands.GetTexture(source),
                0, 0, TextureAspects.Depth, 0, 0, 0,
                commands.GetTexture(destination),
                0, 0, TextureAspects.Depth, 0, 0, 0,
                width,
                height,
                1));
    }

    private readonly record struct TemporalHistoryPassData(
        GraphTextureId SourceColor,
        GraphTextureId DestinationColor,
        GraphTextureInput SourceMotion,
        GraphTextureId DestinationMotion,
        GraphTextureInput SourceDepth,
        GraphTextureId DestinationDepth,
        uint Width,
        uint Height)
    {
        internal void Declare(ref PassDefinition pass)
        {
            DeclareCopy(ref pass, SourceColor, DestinationColor, ColorRange, ColorRange);
            _ = pass.Read(SourceMotion, ColorRange, PipelineSync.Copy, ResourceAccess.CopySource, TextureLayout.CopySource);
            _ = pass.Write(DestinationMotion, ColorRange, PipelineSync.Copy, ResourceAccess.CopyDestination,
                TextureLayout.CopyDestination, WriteCoverage.Complete);
            _ = pass.Read(SourceDepth, DepthRange, PipelineSync.Copy, ResourceAccess.CopySource, TextureLayout.CopySource);
            _ = pass.Write(DestinationDepth, DepthRange, PipelineSync.Copy, ResourceAccess.CopyDestination,
                TextureLayout.CopyDestination, WriteCoverage.Complete);
        }

        internal static void Record(
            ref CopyPassCommandScope commands,
            in TemporalHistoryPassData data)
        {
            Copy(ref commands, commands.GetTexture(data.SourceColor), commands.GetTexture(data.DestinationColor), data.Width, data.Height);
            Copy(ref commands, commands.GetTexture(data.SourceMotion), commands.GetTexture(data.DestinationMotion), data.Width, data.Height);
            Copy(ref commands, commands.GetTexture(data.SourceDepth), commands.GetTexture(data.DestinationDepth), data.Width, data.Height, TextureAspects.Depth);
        }

        private static void Copy(
            ref CopyPassCommandScope commands,
            SomeEngine.Graphics.Texture source,
            SomeEngine.Graphics.Texture destination,
            uint width,
            uint height,
            TextureAspects aspects = TextureAspects.Color) =>
            commands.CopyTexture(new TextureCopy(
                source,
                0, 0, aspects, 0, 0, 0,
                destination,
                0, 0, aspects, 0, 0, 0,
                width,
                height,
                1));
    }
}
