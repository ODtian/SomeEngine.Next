using System.Runtime.InteropServices;
using SomeEngine.Assets.Schema;
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
    int Height)
{
    public bool IsValid =>
        ResolvedColor != default && Format != default && Width > 0 && Height > 0;
}

/// <summary>
/// Owns temporal history, resolve, initialization, and history-update passes. The feature consumes
/// only explicit color, depth, and motion outputs and has no Cluster shader operation role.
/// </summary>
public sealed class TemporalFeature : IDisposable
{
    private static readonly TextureSubresourceRange ColorRange =
        new(0, 1, 0, 1, TextureAspects.Color);
    private static readonly TextureSubresourceRange DepthRange =
        new(0, 1, 0, 1, TextureAspects.Depth);

    private readonly LinkedRasterPipeline _pipeline;
    private readonly TemporalResource[] _resourceOrder;
    private readonly GraphTextureId[] _colorHistory = new GraphTextureId[2];
    private readonly GraphTextureId[] _motionHistory = new GraphTextureId[2];
    private readonly GraphTextureId[] _depthHistory = new GraphTextureId[2];
    private readonly GraphPassId _initializePass;
    private readonly GraphPassId _resolvePass;
    private readonly GraphPassId _historyPass;
    private int _historyGeneration;
    private bool _historyValid;
    private bool _pending;
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
            for (int index = 0; index < 2; index++)
            {
                _colorHistory[index] = edit.CreatePersistentTexture(TextureDescription(
                    colorHistory.Format,
                    colorHistory.Usages,
                    $"Temporal color history {index}"));
                _motionHistory[index] = edit.CreatePersistentTexture(TextureDescription(
                    motionHistory.Format,
                    motionHistory.Usages,
                    $"Temporal motion history {index}"));
                _depthHistory[index] = edit.CreatePersistentTexture(TextureDescription(
                    depthHistory.Format,
                    depthHistory.Usages,
                    $"Temporal depth history {index}"));
            }
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
            _initializePass = edit.AddCopyFramePass(
                "Initialize temporal histories",
                PassQueueSelection.AnyOfType(QueueType.Graphics),
                default(TemporalInitializePassData),
                new PassOptions(Culling: PassCullingMode.NeverCull),
                static (ref PassDefinition _, ref TemporalInitializePassData _) => { },
                TemporalInitializePassData.Record);
            _resolvePass = edit.AddRasterFramePass(
                "Temporal resolve",
                PassQueueSelection.AnyOfType(QueueType.Graphics),
                new TemporalResolvePassData(resolvedAttachment, width, height),
                new PassOptions(Culling: PassCullingMode.NeverCull),
                pipeline.Pipeline,
                pipeline.Program.Layout.GetGlobalParamsVarLayout(),
                default,
                TemporalResolvePassData.Declare,
                TemporalResolvePassData.Record);
            _historyPass = edit.AddCopyFramePass(
                "Update temporal history",
                PassQueueSelection.AnyOfType(QueueType.Graphics),
                default(TemporalHistoryPassData),
                new PassOptions(Culling: PassCullingMode.NeverCull),
                static (ref PassDefinition _, ref TemporalHistoryPassData _) => { },
                TemporalHistoryPassData.Record);
            edit.OrderAfter(_resolvePass, _initializePass);
            edit.OrderAfter(_historyPass, _resolvePass);
            edit.Commit();
            Output = new TemporalFeatureOutput(
                resolved,
                Format.R16G16B16A16Float,
                width,
                height);
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

    public void Record(
        ref RenderGraphFrame graph,
        GraphTextureId sceneColor,
        GraphTextureId sceneDepth,
        in MotionFeatureOutput motion,
        TemporalResolveSettings settings,
        bool enabled = true,
        bool cameraCut = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_pending)
            throw new InvalidOperationException("The previous temporal frame must be committed or discarded.");
        if (sceneColor == default || sceneDepth == default || !motion.IsValid)
            throw new ArgumentException("Temporal resolve requires valid color, depth, and motion inputs.");

        int previous = _historyGeneration;
        int current = previous ^ 1;
        bool useHistory = enabled && _historyValid && !cameraCut;
        TemporalResolveUniforms uniforms = useHistory
            ? settings.ToUniforms()
            : settings.ToUniforms() with { HistoryWeight = 0.0f };
        GraphBufferId uniformBuffer = FeatureUploads.UploadConstant(
            ref graph,
            in uniforms,
            "Temporal resolve uniforms");

        bool initializeHistory = !_historyValid || cameraCut;
        graph.SetPassEnabled(_initializePass, initializeHistory);
        if (initializeHistory)
        {
            var initialization = new TemporalInitializePassData(
                _colorHistory[0], _colorHistory[1],
                _motionHistory[0], _motionHistory[1],
                sceneDepth,
                _depthHistory[0], _depthHistory[1],
                checked((uint)Output.Width),
                checked((uint)Output.Height));
            PassDefinition initialize = graph.GetPass(_initializePass);
            initialization.Declare(ref initialize);
            graph.SetPassData(_initializePass, initialization);
        }
        PassDefinition resolve = graph.GetPass(_resolvePass);
        foreach (TemporalResource resource in _resourceOrder)
        {
            switch (resource)
            {
                case TemporalResource.Uniforms:
                    _ = resolve.Bind(
                        graph.CreateBufferCbv(uniformBuffer, label: "Temporal uniforms"),
                        PipelineSync.PixelShading);
                    break;
                case TemporalResource.SceneColor:
                    BindTexture(ref graph, ref resolve, sceneColor, Format.R16G16B16A16Float, TextureAspects.Color, 0, SomeEngine.Graphics.TextureViewDimension.Texture2DArray, "Temporal scene color");
                    break;
                case TemporalResource.PreviousSceneColor:
                    BindTexture(ref graph, ref resolve, _colorHistory[previous], Format.R16G16B16A16Float, TextureAspects.Color, 0, SomeEngine.Graphics.TextureViewDimension.Texture2D, "Temporal previous color");
                    break;
                case TemporalResource.MotionVectors:
                    BindTexture(ref graph, ref resolve, motion.MotionVectors, Format.R16G16Float, TextureAspects.Color, 0, SomeEngine.Graphics.TextureViewDimension.Texture2DArray, "Temporal motion");
                    break;
                case TemporalResource.PreviousMotionVectors:
                    BindTexture(ref graph, ref resolve, _motionHistory[previous], Format.R16G16Float, TextureAspects.Color, 0, SomeEngine.Graphics.TextureViewDimension.Texture2D, "Temporal previous motion");
                    break;
                case TemporalResource.SceneDepth:
                    BindTexture(ref graph, ref resolve, sceneDepth, Format.D32Float, TextureAspects.Depth, 0, SomeEngine.Graphics.TextureViewDimension.Texture2DArray, "Temporal scene depth");
                    break;
                case TemporalResource.PreviousSceneDepth:
                    BindTexture(ref graph, ref resolve, _depthHistory[previous], Format.D32Float, TextureAspects.Depth, 0, SomeEngine.Graphics.TextureViewDimension.Texture2D, "Temporal previous depth");
                    break;
                default:
                    throw new InvalidOperationException("Unknown temporal resource key.");
            }
        }
        graph.SetPassEnabled(_resolvePass, true);

        PassDefinition history = graph.GetPass(_historyPass);
        DeclareCopy(ref history, Output.ResolvedColor, _colorHistory[current], ColorRange, ColorRange);
        DeclareCopy(ref history, motion.MotionVectors, _motionHistory[current], ColorRange, ColorRange);
        DeclareCopy(ref history, sceneDepth, _depthHistory[current], DepthRange, DepthRange);
        graph.SetPassData(
            _historyPass,
            new TemporalHistoryPassData(
                Output.ResolvedColor,
                _colorHistory[current],
                motion.MotionVectors,
                _motionHistory[current],
                sceneDepth,
                _depthHistory[current],
                checked((uint)Output.Width),
                checked((uint)Output.Height)));
        graph.SetPassEnabled(_historyPass, true);
        _pending = true;
    }

    public void Commit()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_pending)
            throw new InvalidOperationException("No temporal frame is waiting for commit.");
        _historyGeneration ^= 1;
        _historyValid = true;
        _pending = false;
    }

    public void Discard()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _pending = false;
    }

    public void InvalidateHistory()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_pending)
            throw new InvalidOperationException("Temporal history cannot be invalidated during an open frame.");
        _historyValid = false;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _pipeline.Dispose();
        Array.Clear(_colorHistory);
        Array.Clear(_motionHistory);
        Array.Clear(_depthHistory);
        _pending = false;
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
        ref RenderGraphFrame graph,
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
        GraphTextureId Color0,
        GraphTextureId Color1,
        GraphTextureId Motion0,
        GraphTextureId Motion1,
        GraphTextureId SourceDepth,
        GraphTextureId Depth0,
        GraphTextureId Depth1,
        uint Width,
        uint Height)
    {
        internal void Declare(ref PassDefinition pass)
        {
            DeclareColor(ref pass, Color0);
            DeclareColor(ref pass, Color1);
            DeclareColor(ref pass, Motion0);
            DeclareColor(ref pass, Motion1);
            _ = pass.Read(
                SourceDepth,
                DepthRange,
                PipelineSync.Copy,
                ResourceAccess.CopySource,
                TextureLayout.CopySource);
            DeclareDepth(ref pass, Depth0);
            DeclareDepth(ref pass, Depth1);
        }

        internal static void Record(
            ref CopyPassCommandScope commands,
            in TemporalInitializePassData data)
        {
            Clear(ref commands, data.Color0);
            Clear(ref commands, data.Color1);
            Clear(ref commands, data.Motion0);
            Clear(ref commands, data.Motion1);
            CopyDepth(ref commands, data.SourceDepth, data.Depth0, data.Width, data.Height);
            CopyDepth(ref commands, data.SourceDepth, data.Depth1, data.Width, data.Height);
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
            GraphTextureId source,
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
        GraphTextureId SourceMotion,
        GraphTextureId DestinationMotion,
        GraphTextureId SourceDepth,
        GraphTextureId DestinationDepth,
        uint Width,
        uint Height)
    {
        internal static void Record(
            ref CopyPassCommandScope commands,
            in TemporalHistoryPassData data)
        {
            Copy(ref commands, data.SourceColor, data.DestinationColor, data.Width, data.Height);
            Copy(ref commands, data.SourceMotion, data.DestinationMotion, data.Width, data.Height);
            Copy(ref commands, data.SourceDepth, data.DestinationDepth, data.Width, data.Height, TextureAspects.Depth);
        }

        private static void Copy(
            ref CopyPassCommandScope commands,
            GraphTextureId source,
            GraphTextureId destination,
            uint width,
            uint height,
            TextureAspects aspects = TextureAspects.Color) =>
            commands.CopyTexture(new TextureCopy(
                commands.GetTexture(source),
                0, 0, aspects, 0, 0, 0,
                commands.GetTexture(destination),
                0, 0, aspects, 0, 0, 0,
                width,
                height,
                1));
    }
}
