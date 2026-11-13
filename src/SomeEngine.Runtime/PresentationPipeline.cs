using SomeEngine.Render;
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
internal sealed class PresentationPipeline : IDisposable
{

    private readonly RenderPipelines _renderPipelines;
    private GraphicsPipeline _pipeline;
    private Shader _shader;
    private readonly global::SomeEngine.RenderGraph.RenderGraph _graph;
    private readonly GraphTextureId _target;
    private readonly GraphPassId _drawPass;
    private readonly uint _width, _height;
    private bool _disposed;

    internal PresentationPipeline(
        IGraphicsBackend backend,
        Device device,
        RenderPipelines pipelines,
        global::SomeEngine.RenderGraph.RenderGraph graph,
        GraphTextureId inputColor,
        Format inputFormat,
        int inputWidth,
        int inputHeight,
        GraphReadbackId pageFaultReadback,
        Shader shader,
        string vertexEntryPoint,
        string pixelEntryPoint,
        Format outputFormat,
        GraphRegionId region)
    {

        _renderPipelines = pipelines;
        _shader = shader;
        _graph = graph;
        _width = (uint)inputWidth;
        _height = (uint)inputHeight;
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(shader);
        if (inputColor == default)
            throw new ArgumentException("Presentation requires a valid color input.", nameof(inputColor));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(inputWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(inputHeight);
        if (!ReferenceEquals(graph.Device, device))
            throw new ArgumentException("Presentation and RenderGraph must use the same Device.", nameof(graph));

        int targetIndex = shader.RequireSlangTarget(device.Capabilities.CompileTarget);
        ShaderReflection reflection = shader.GetSlangReflection(targetIndex);
        EntryPointReflection vertex = shader.RequireSlangEntry(
            targetIndex,
            vertexEntryPoint,
            SlangStage.Vertex);
        EntryPointReflection pixel = shader.RequireSlangEntry(
            targetIndex,
            pixelEntryPoint,
            SlangStage.Fragment);
        System.Buffers.ReadOnlySequence<byte> vertexCode =
            shader.GetSlangEntryBytes(targetIndex, vertex);
        System.Buffers.ReadOnlySequence<byte> pixelCode =
            shader.GetSlangEntryBytes(targetIndex, pixel);

        GraphicsPipeline? pipeline = null;
        try
        {
            pipeline = _renderPipelines.CreateGraphics(
                new GraphicsPipelineDesc(
                    shader.SlangCode,
                    reflection,
                    vertex,
                    vertexCode,
                    pixel,
                    pixelCode,
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
                    "Runtime presentation",
                    vertexCodeBlob: shader.GetSlangEntryBlob(targetIndex, vertex),
                    pixelCodeBlob: shader.GetSlangEntryBlob(targetIndex, pixel))).GetAwaiter().GetResult();

            RenderGraphEdit edit = graph.BeginEdit();
            try
            {
                GraphTextureId target = edit.CreatePersistentTexture(new TextureDesc(
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
                PresentationPassData data = new(
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
                    reflection.GetGlobalParamsVarLayout() ?? VariableLayoutReflection.Null,
                    bindings,
                    PresentationPassData.Declare,
                    PresentationPassData.Record,
                    region: region);
                edit.OrderAfter(pass, pageFaultReadback);
                edit.Commit();
                _target = target;
                _drawPass = pass;
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

            throw;
        }
    }

    internal GraphTextureId Target => _target;
    internal GraphPassId DrawPass => _drawPass;

    internal void OrderAfter(RenderGraphEdit edit, GraphReadbackId predecessor) => edit.OrderAfter(_drawPass, predecessor);

    internal void Bind(
        ref RenderGraphFrame frame,
        in SwapchainImage image,
        Queue presentQueue)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        GraphTextureId target = frame.Import(image, presentQueue);
        frame.AddCopyPass("Copy presentation image", PassQueueSelection.AnyOfType(QueueType.Graphics),
            (Source: _target, Target: target, Width: _width, Height: _height), default,
            static (ref PassDefinition pass, ref (GraphTextureId Source, GraphTextureId Target, uint Width, uint Height) data) =>
            {
                pass.Read(data.Source, new(0, 1, 0, 1, TextureAspects.Color), PipelineSync.Copy,
                    ResourceAccess.CopySource, TextureLayout.CopySource);
                pass.Write(data.Target, new(0, 1, 0, 1, TextureAspects.Color), PipelineSync.Copy,
                    ResourceAccess.CopyDestination, TextureLayout.CopyDestination, WriteCoverage.Complete);
            },
            static (ref CopyPassCommandScope commands, in (GraphTextureId Source, GraphTextureId Target, uint Width, uint Height) data) =>
                commands.CopyTexture(new(commands.GetTexture(data.Source), 0, 0, TextureAspects.Color, 0, 0, 0,
                    commands.GetTexture(data.Target), 0, 0, TextureAspects.Color, 0, 0, 0, data.Width, data.Height, 1)));
    }

    internal void ReplaceShader(Shader previous, Shader next)
    {
        if (!ReferenceEquals(_shader, previous)) return;
        _shader = next;
        _pipeline = _renderPipelines.CreateWithShader(_pipeline, next).GetAwaiter().GetResult();
        using RenderGraphEdit edit = _graph.BeginEdit();
        PassDefinition pass = edit.GetPass(_drawPass);
        pass.SetPipeline(_pipeline);
        pass.SetParameterBlock(next.GetSlangReflection(next.RequireSlangTarget(_renderPipelines.Device.Capabilities.CompileTarget))
            .GetGlobalParamsVarLayout() ?? VariableLayoutReflection.Null);
        edit.Commit();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
    }

    private readonly record struct PresentationPassData(
        GraphColorAttachmentViewId Output,
        int Width,
        int Height)
    {
        internal static void Declare(
            ref PassDefinition access,
            ref PresentationPassData data) =>
            access.ColorAttachment(
                0,
                data.Output,
                LoadType.Clear,
                StoreType.Store,
                WriteCoverage.Complete,
                new System.Numerics.Vector4(0, 0, 0, 1));

        internal static void Record(
            ref RasterPassCommandScope commands,
            in PresentationPassData data)
        {
            commands.SetViewports([new Viewport(0, 0, data.Width, data.Height)]);
            commands.SetScissors([new ScissorRect(0, 0, data.Width, data.Height)]);
            commands.Draw(new DrawArguments(3, 1, 0, 0));
        }
    }
}
