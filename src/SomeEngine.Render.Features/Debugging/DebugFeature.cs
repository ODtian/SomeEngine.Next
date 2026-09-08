using System.Runtime.InteropServices;
using SomeEngine.Assets.Schema;
using SomeEngine.Graphics;
using SomeEngine.Render.Assets;
using SomeEngine.RenderGraph;

namespace SomeEngine.Render.Features.Debugging;

public enum DebugVisualizationMode : uint
{
    None = 0,
    Depth = 1,
}

public readonly record struct DebugFeatureOutput(
    GraphTextureId Color,
    Format Format,
    int Width,
    int Height,
    GraphPassId CompletePass)
{
    public bool IsValid =>
        Color != default && Width > 0 && Height > 0 && CompletePass != default;
}

/// <summary>
/// Owns debug visualization output and pass policy. It consumes explicit color/depth capabilities
/// and remains a pass-through when disabled, without changing Cluster ABI or shader roles.
/// </summary>
public sealed class DebugFeature : IDisposable
{
    private readonly LinkedRasterPipeline _pipeline;
    private readonly DebugResource[] _resourceOrder;
    private readonly GraphPassId _pass;
    private readonly GraphBufferInput _uniforms;
    private readonly GraphTextureInput _color;
    private readonly GraphTextureInput _depth;
    private bool _disposed;

    public DebugFeature(
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
                label: "Debug overlay");
            _resourceOrder = FeatureResourceLayout.Resolve<DebugResource>(
                pipeline.Program.Layout.GetGlobalParamsVarLayout(),
                ResolveResource);
            using RenderGraphEdit edit = graph.BeginEdit();
            GraphTextureId output = edit.CreatePersistentTexture(new TextureDesc(
                TextureDimension.Texture2D,
                checked((uint)width), checked((uint)height), 1, 1, 1, 1,
                Format.R16G16B16A16Float,
                TextureUsages.ColorAttachment | TextureUsages.Sampled,
                label: "Debug feature output"));
            GraphColorAttachmentViewId outputAttachment = edit.CreateColorAttachmentView(
                output,
                format: Format.R16G16B16A16Float,
                dimension: SomeEngine.Graphics.TextureViewDimension.Texture2D,
                label: "Debug output");
            _pass = edit.AddRasterFramePass(
                "Debug visualization",
                PassQueueSelection.AnyOfType(QueueType.Graphics),
                new DebugPassData(outputAttachment, width, height),
                new PassOptions(Culling: PassCullingMode.NeverCull),
                pipeline.Pipeline,
                pipeline.Program.Layout.GetGlobalParamsVarLayout(),
                default,
                DebugPassData.Declare,
                DebugPassData.Record);
            _uniforms = edit.CreateBufferInput(BufferUsages.Constant);
            _color = edit.CreateTextureInput();
            _depth = edit.CreateTextureInput();
            PassDefinition pass = edit.GetPass(_pass);
            foreach (DebugResource resource in _resourceOrder)
            {
                switch (resource)
                {
                    case DebugResource.Uniforms:
                        _ = pass.Bind(
                            _uniforms,
                            PipelineSync.PixelShading);
                        break;
                    case DebugResource.Color:
                        _ = pass.Bind(
                            edit.CreateTextureSrv(
                                _color,
                                format: Format.R16G16B16A16Float,
                                dimension: SomeEngine.Graphics.TextureViewDimension.Texture2D,
                                label: "Debug input color"),
                            PipelineSync.PixelShading);
                        break;
                    case DebugResource.Depth:
                        _ = pass.Bind(
                            edit.CreateTextureSrv(
                                _depth,
                                new TextureSubresourceRange(0, 1, 0, 1, TextureAspects.Depth),
                                Format.D32Float,
                                SomeEngine.Graphics.TextureViewDimension.Texture2DArray,
                                "Debug input depth"),
                            PipelineSync.PixelShading);
                        break;
                    default:
                        throw new InvalidOperationException("Unknown debug resource key.");
                }
            }
            edit.Commit();
            Output = new DebugFeatureOutput(
                output,
                Format.R16G16B16A16Float,
                width,
                height,
                _pass);
            _pipeline = pipeline;
            pipeline = null;
        }
        catch
        {
            pipeline?.Dispose();
            throw;
        }
    }

    public DebugFeatureOutput Output { get; }

    public void Record(
        ref RenderGraphFrame graph,
        GraphTextureId inputColor,
        GraphTextureId inputDepth,
        DebugVisualizationMode mode = DebugVisualizationMode.None,
        float depthScale = 1.0f)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (inputColor == default || inputDepth == default)
            throw new ArgumentException("Debug visualization requires valid color and depth inputs.");
        if (!Enum.IsDefined(mode))
            throw new ArgumentOutOfRangeException(nameof(mode));
        var uniforms = new DebugUniforms { Mode = (uint)mode, DepthScale = depthScale };
        GraphBufferSlice uniformBuffer = FeatureUploads.UploadConstant(
            ref graph,
            in uniforms,
            "Debug overlay uniforms");
        graph.SetBufferInput(_uniforms, uniformBuffer);
        graph.SetTextureInput(_color, inputColor);
        graph.SetTextureInput(_depth, inputDepth);
        graph.SetPassEnabled(_pass, true);
    }

    public void OrderAfter(ref RenderGraphEdit graph, GraphPassId colorProducer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (colorProducer == default)
        {
            throw new ArgumentException(
                "Debug visualization requires a valid color producer.",
                nameof(colorProducer));
        }
        graph.OrderAfter(_pass, colorProducer);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _pipeline.Dispose();
        _disposed = true;
    }

    private static DebugResource? ResolveResource(string name, ResourceBindingType type) =>
        (name, type) switch
        {
            ("DebugUniforms", ResourceBindingType.ConstantBuffer) => DebugResource.Uniforms,
            ("InputColor", ResourceBindingType.TextureSrv) => DebugResource.Color,
            ("InputDepth", ResourceBindingType.TextureSrv) => DebugResource.Depth,
            _ => null,
        };

    private enum DebugResource : byte
    {
        Uniforms,
        Color,
        Depth,
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4, Size = 16)]
    private struct DebugUniforms
    {
        internal uint Mode;
        internal float DepthScale;
    }

    private readonly record struct DebugPassData(
        GraphColorAttachmentViewId Output,
        int Width,
        int Height)
    {
        internal static void Declare(ref PassDefinition pass, ref DebugPassData data) =>
            pass.ColorAttachment(
                0,
                data.Output,
                LoadType.Clear,
                StoreType.Store,
                WriteCoverage.Complete,
                new System.Numerics.Vector4(0, 0, 0, 1));

        internal static void Record(ref RasterPassCommandScope commands, in DebugPassData data)
        {
            commands.SetViewports([new Viewport(0, 0, data.Width, data.Height)]);
            commands.SetScissors([new ScissorRect(0, 0, data.Width, data.Height)]);
            commands.Draw(new DrawArguments(3, 1, 0, 0));
        }
    }
}
