using SlangShaderSharp;
using SomeEngine.Assets.Schema;
using SomeEngine.Resources;
using SomeEngine.Graphics;
using SomeEngine.RenderGraph;
using GraphicsPipeline = SomeEngine.Graphics.Pipeline;

namespace SomeEngine.Runtime;

/// <summary>
/// Runtime-owned output composition. It consumes an explicit geometry color capability and owns
/// the swapchain-format conversion and presentation target; Cluster has no dependency on it.
/// </summary>
internal sealed class RuntimePresentationPipeline : IDisposable
{
    private readonly GraphicsPipeline _pipeline;
    private readonly GraphTextureId _target;
    private bool _disposed;

    internal RuntimePresentationPipeline(
        IGraphicsBackend backend,
        Device device,
        global::SomeEngine.RenderGraph.RenderGraph graph,
        GraphTextureId inputColor,
        Format inputFormat,
        int inputWidth,
        int inputHeight,
        GraphPassId inputCompletePass,
        Shader shader,
        string vertexEntryPoint,
        string pixelEntryPoint,
        Format outputFormat)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(shader);
        if (inputColor == default)
            throw new ArgumentException("Presentation requires a valid color input.", nameof(inputColor));
        if (inputCompletePass == default)
        {
            throw new ArgumentException(
                "Presentation requires a valid color producer.",
                nameof(inputCompletePass));
        }
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(inputWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(inputHeight);
        if (!ReferenceEquals(graph.Device, device))
            throw new ArgumentException("Presentation and RenderGraph must use the same Device.", nameof(graph));

        CookedShaderEntry vertex = shader.RequireEntry(
            device.Capabilities.CompileTarget,
            vertexEntryPoint,
            ShaderStage.Vertex);
        CookedShaderEntry pixel = shader.RequireEntry(
            device.Capabilities.CompileTarget,
            pixelEntryPoint,
            ShaderStage.Pixel);
        if (!ReferenceEquals(vertex.Program, pixel.Program))
            throw new InvalidDataException("Presentation entries must share one cooked shader program.");

        GraphicsPipeline? pipeline = null;
        try
        {
            pipeline = backend.CreateGraphicsPipeline(
                device,
                new GraphicsPipelineDesc(
                    vertex.Program,
                    vertex.EntryPoint,
                    pixel.EntryPoint,
                    vertexBuffers: default,
                    vertexAttributes: default,
                    PrimitiveTopology.TriangleList,
                    StripCut.Disabled,
                    new RasterizerState(Cull: CullType.None),
                    new MultisampleState(),
                    new DepthStencilState(),
                    new BlendState(),
                    new AttachmentFormatSignature([outputFormat], null),
                    DynamicStates.Viewport | DynamicStates.Scissor,
                    "Runtime presentation"));

            RenderGraphEdit edit = graph.BeginEdit();
            try
            {
                GraphTextureId target = edit.DeclareExternalTexture(new TextureDesc(
                    TextureDimension.Texture2D,
                    checked((uint)inputWidth),
                    checked((uint)inputHeight),
                    1,
                    1,
                    1,
                    1,
                    outputFormat,
                    TextureUsages.ColorAttachment | TextureUsages.CopySource,
                    label: "Runtime presentation target"));
                GraphTextureSrvId source = edit.CreateTextureSrv(
                    inputColor,
                    new TextureSubresourceRange(
                        0,
                        1,
                        0,
                        1,
                        TextureAspects.Color),
                    format: inputFormat,
                    dimension: SomeEngine.Graphics.TextureViewDimension.Texture2D,
                    label: "Runtime presentation source");
                GraphColorAttachmentViewId output = edit.CreateColorAttachmentView(
                    target,
                    format: outputFormat,
                    dimension: SomeEngine.Graphics.TextureViewDimension.Texture2D,
                    label: "Runtime presentation output");
                RuntimePresentationPassData data = new(
                    output,
                    inputWidth,
                    inputHeight);
                GraphParameterResourceBinding[] bindings =
                [
                    GraphParameterResourceBinding.SampledTexture(
                        source,
                        PipelineSync.PixelShading),
                ];
                GraphPassId pass = edit.AddRasterFramePass(
                    "Runtime tone map and present",
                    PassQueueSelection.AnyOfType(QueueType.Graphics),
                    data,
                    new PassOptions(Culling: PassCullingMode.NeverCull),
                    pipeline,
                    vertex.Program.Layout.GetGlobalParamsVarLayout(),
                    bindings,
                    RuntimePresentationPassData.Declare,
                    RuntimePresentationPassData.Record);
                edit.OrderAfter(pass, inputCompletePass);
                edit.Commit();
                _target = target;
            }
            finally
            {
                edit.Dispose();
            }
            _pipeline = pipeline;
            pipeline = null;
        }
        catch
        {
            pipeline?.Dispose();
            throw;
        }
    }

    internal GraphTextureId Target => _target;

    internal void Bind(
        ref RenderGraphFrame frame,
        in SwapchainImage image,
        Queue presentQueue)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        frame.BindExternalTexture(_target, image, presentQueue);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _pipeline.Dispose();
        _disposed = true;
    }

    private readonly record struct RuntimePresentationPassData(
        GraphColorAttachmentViewId Output,
        int Width,
        int Height)
    {
        internal static void Declare(
            ref PassDefinition access,
            ref RuntimePresentationPassData data) =>
            access.ColorAttachment(
                0,
                data.Output,
                LoadType.Clear,
                StoreType.Store,
                WriteCoverage.Complete,
                new System.Numerics.Vector4(0, 0, 0, 1));

        internal static void Record(
            ref RasterPassCommandScope commands,
            in RuntimePresentationPassData data)
        {
            commands.SetViewports([new Viewport(0, 0, data.Width, data.Height)]);
            commands.SetScissors([new ScissorRect(0, 0, data.Width, data.Height)]);
            commands.Draw(new DrawArguments(3, 1, 0, 0));
        }
    }
}
