using System.Runtime.InteropServices;
using SlangShaderSharp;
using SomeEngine.Assets.Schema;
using SomeEngine.Resources;
using SomeEngine.Graphics;
using SomeEngine.Render.Assets;
using SomeEngine.RenderGraph;

namespace SomeEngine.Render.Features.Translucency;

public readonly record struct TranslucencyFeatureOutput(
    GraphTextureId Color,
    GraphTextureId Accumulation,
    GraphTextureId Revealage,
    Format Format,
    int Width,
    int Height,
    GraphPassId CompletePass)
{
    public bool IsValid =>
        Color != default && Accumulation != default && Revealage != default &&
        Width > 0 && Height > 0 && CompletePass != default;
}

/// <summary>
/// Owns weighted-translucency targets, their initialization, and independent opaque/translucent
/// composition. Producers target the exposed accumulation resources without entering Cluster.
/// </summary>
public sealed class TranslucencyFeature : IDisposable
{
    private readonly LinkedRasterPipeline _pipeline;
    private readonly CompositionResource[] _resourceOrder;
    private readonly GraphPassId _clearPass;
    private readonly GraphPassId _composePass;
    private readonly GraphBufferInput _uniforms;
    private readonly GraphTextureInput _opaqueColor;
    private bool _disposed;

    public TranslucencyFeature(
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
            throw new ArgumentException("Translucency and RenderGraph must use the same Device.", nameof(graph));

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
                label: "Translucency composition");
            _resourceOrder = FeatureResourceLayout.Resolve<CompositionResource>(
                pipeline.Reflection.GetGlobalParamsVarLayout() ?? VariableLayoutReflection.Null,
                ResolveResource);

            using RenderGraphEdit edit = graph.BeginEdit();
            GraphTextureId accumulation = edit.CreatePersistentTexture(new TextureDesc(
                TextureDimension.Texture2D,
                checked((uint)width), checked((uint)height), 1, 1, 1, 1,
                Format.R16G16B16A16Float,
                TextureUsages.ColorAttachment | TextureUsages.Sampled,
                label: "Translucency accumulation"));
            GraphTextureId revealage = edit.CreatePersistentTexture(new TextureDesc(
                TextureDimension.Texture2D,
                checked((uint)width), checked((uint)height), 1, 1, 1, 1,
                Format.R16Float,
                TextureUsages.ColorAttachment | TextureUsages.Sampled,
                label: "Translucency revealage"));
            GraphTextureId output = edit.CreatePersistentTexture(new TextureDesc(
                TextureDimension.Texture2D,
                checked((uint)width), checked((uint)height), 1, 1, 1, 1,
                Format.R16G16B16A16Float,
                TextureUsages.ColorAttachment | TextureUsages.Sampled,
                label: "Composited translucent color"));
            GraphColorAttachmentViewId accumulationAttachment = edit.CreateColorAttachmentView(
                accumulation,
                format: Format.R16G16B16A16Float,
                dimension: SomeEngine.Graphics.TextureViewDimension.Texture2D,
                label: "Translucency accumulation attachment");
            GraphColorAttachmentViewId revealageAttachment = edit.CreateColorAttachmentView(
                revealage,
                format: Format.R16Float,
                dimension: SomeEngine.Graphics.TextureViewDimension.Texture2D,
                label: "Translucency revealage attachment");
            GraphColorAttachmentViewId outputAttachment = edit.CreateColorAttachmentView(
                output,
                format: Format.R16G16B16A16Float,
                dimension: SomeEngine.Graphics.TextureViewDimension.Texture2D,
                label: "Translucency composition output");
            _clearPass = edit.AddRasterFramePass(
                "Clear translucency targets",
                PassQueueSelection.AnyOfType(QueueType.Graphics),
                new ClearPassData(accumulationAttachment, revealageAttachment),
                new PassOptions(Culling: PassCullingMode.NeverCull),
                ClearPassData.Declare,
                static (ref RasterPassCommandScope _, in ClearPassData _) => { });
            _composePass = edit.AddRasterFramePass(
                "Composite translucency",
                PassQueueSelection.AnyOfType(QueueType.Graphics),
                new CompositionPassData(outputAttachment, width, height),
                new PassOptions(Culling: PassCullingMode.NeverCull),
                pipeline.Pipeline,
                pipeline.Reflection.GetGlobalParamsVarLayout() ?? VariableLayoutReflection.Null,
                default,
                CompositionPassData.Declare,
                CompositionPassData.Record);
            edit.OrderAfter(_composePass, _clearPass);
            _uniforms = edit.CreateBufferInput(BufferUsages.Constant);
            _opaqueColor = edit.CreateTextureInput();
            PassDefinition compose = edit.GetPass(_composePass);
            foreach (CompositionResource resource in _resourceOrder)
            {
                switch (resource)
                {
                    case CompositionResource.Uniforms:
                        _ = compose.Bind(
                            _uniforms,
                            PipelineSync.PixelShading);
                        break;
                    case CompositionResource.OpaqueColor:
                        _ = compose.Bind(edit.CreateTextureSrv(_opaqueColor, Format.R16G16B16A16Float,
                            SomeEngine.Graphics.TextureViewDimension.Texture2D, "Opaque color"), PipelineSync.PixelShading);
                        break;
                    case CompositionResource.Accumulation:
                        BindTexture(edit, ref compose, accumulation, Format.R16G16B16A16Float, "Translucency accumulation");
                        break;
                    case CompositionResource.Revealage:
                        BindTexture(edit, ref compose, revealage, Format.R16Float, "Translucency revealage");
                        break;
                    default:
                        throw new InvalidOperationException("Unknown translucency composition resource key.");
                }
            }
            edit.Commit();
            Output = new TranslucencyFeatureOutput(
                output,
                accumulation,
                revealage,
                Format.R16G16B16A16Float,
                width,
                height,
                _composePass);
            _pipeline = pipeline;
            pipeline = null;
        }
        catch
        {
            pipeline?.Dispose();
            throw;
        }
    }

    public TranslucencyFeatureOutput Output { get; }

    public void Record(ref RenderGraphFrame graph, GraphTextureId opaqueColor, bool enabled = true)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (opaqueColor == default)
            throw new ArgumentException("Translucency composition requires opaque color.", nameof(opaqueColor));
        CompositionUniforms uniforms = new() { Enabled = enabled ? 1u : 0u };
        GraphBufferSlice uniformBuffer = FeatureUploads.UploadConstant(
            ref graph,
            in uniforms,
            "Translucency composition uniforms");
        graph.SetBufferInput(_uniforms, uniformBuffer);
        graph.SetTextureInput(_opaqueColor, opaqueColor);
        graph.SetPassEnabled(_clearPass, true);
        graph.SetPassEnabled(_composePass, true);
    }

    public void OrderCompositionAfter(ref RenderGraphEdit graph, GraphPassId translucentProducer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        graph.OrderAfter(_composePass, translucentProducer);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _pipeline.Dispose();
        _disposed = true;
    }

    private static CompositionResource? ResolveResource(string name, ResourceBindingType type) =>
        (name, type) switch
        {
            ("CompositionUniforms", ResourceBindingType.ConstantBuffer) => CompositionResource.Uniforms,
            ("OpaqueColor", ResourceBindingType.TextureSrv) => CompositionResource.OpaqueColor,
            ("TranslucencyAccumulation", ResourceBindingType.TextureSrv) => CompositionResource.Accumulation,
            ("TranslucencyRevealage", ResourceBindingType.TextureSrv) => CompositionResource.Revealage,
            _ => null,
        };

    private static void BindTexture(
        RenderGraphEdit graph,
        ref PassDefinition pass,
        GraphTextureId texture,
        Format format,
        string label) =>
        _ = pass.Bind(
            graph.CreateTextureSrv(
                texture,
                format: format,
                dimension: SomeEngine.Graphics.TextureViewDimension.Texture2D,
                label: label),
            PipelineSync.PixelShading);

    private enum CompositionResource : byte
    {
        Uniforms,
        OpaqueColor,
        Accumulation,
        Revealage,
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4, Size = 16)]
    private struct CompositionUniforms
    {
        internal uint Enabled;
    }

    private readonly record struct ClearPassData(
        GraphColorAttachmentViewId Accumulation,
        GraphColorAttachmentViewId Revealage)
    {
        internal static void Declare(ref PassDefinition pass, ref ClearPassData data)
        {
            pass.ColorAttachment(
                0,
                data.Accumulation,
                LoadType.Clear,
                StoreType.Store,
                WriteCoverage.Complete,
                default);
            pass.ColorAttachment(
                1,
                data.Revealage,
                LoadType.Clear,
                StoreType.Store,
                WriteCoverage.Complete,
                System.Numerics.Vector4.One);
        }
    }

    private readonly record struct CompositionPassData(
        GraphColorAttachmentViewId Output,
        int Width,
        int Height)
    {
        internal static void Declare(ref PassDefinition pass, ref CompositionPassData data) =>
            pass.ColorAttachment(
                0,
                data.Output,
                LoadType.Clear,
                StoreType.Store,
                WriteCoverage.Complete,
                new System.Numerics.Vector4(0, 0, 0, 1));

        internal static void Record(
            ref RasterPassCommandScope commands,
            in CompositionPassData data)
        {
            commands.SetViewports([new Viewport(0, 0, data.Width, data.Height)]);
            commands.SetScissors([new ScissorRect(0, 0, data.Width, data.Height)]);
            commands.Draw(new DrawArguments(3, 1, 0, 0));
        }
    }
}
