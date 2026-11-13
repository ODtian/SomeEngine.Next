using SomeEngine.Assets.Schema;
using SomeEngine.Resources;
using SomeEngine.Graphics;
using SlangShaderSharp;
using GraphicsPipeline = SomeEngine.Graphics.Pipeline;

namespace SomeEngine.Render.Assets;

/// <summary>Pairs raster reflection with a pipeline retained by RenderPipelines.</summary>
public sealed class LinkedRasterPipeline : IDisposable
{

    private LinkedRasterPipeline(Shader shader, ShaderReflection reflection, GraphicsPipeline pipeline)
    {
        Shader = shader;
        Reflection = reflection;
        Pipeline = pipeline;
    }

    public Shader Shader { get; private set; }
    public ShaderReflection Reflection { get; private set; }
    public GraphicsPipeline Pipeline { get; private set; }

    public static LinkedRasterPipeline Create(
        RenderPipelines pipelines,
        Shader shader,
        string vertexEntryPoint,
        string pixelEntryPoint,
        ReadOnlySpan<Format> colorFormats,
        Format? depthStencilFormat = null,
        RasterizerState rasterizer = default,
        DepthStencilState depthStencil = default,
        ReadOnlySpan<BlendAttachmentState> blendAttachments = default,
        uint sampleCount = 1,
        string? label = null,
        uint sampleMask = uint.MaxValue,
        bool alphaToCoverage = false,
        DynamicStates additionalDynamicStates = DynamicStates.None)
    {
        ArgumentNullException.ThrowIfNull(shader);
        ArgumentException.ThrowIfNullOrWhiteSpace(vertexEntryPoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(pixelEntryPoint);
        GraphicsPipeline? pipeline = null;
            int targetIndex = shader.RequireSlangTarget(pipelines.Device.Capabilities.CompileTarget);
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
            var multisample = new MultisampleState(sampleCount, sampleMask, alphaToCoverage);
            var blend = new BlendState(
                blendAttachments,
                independentBlend: blendAttachments.Length > 1);
            var attachments = new AttachmentFormatSignature(
                colorFormats,
                depthStencilFormat,
                sampleCount);
            pipeline = pipelines.CreateGraphics(
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
                    rasterizer,
                    multisample,
                    depthStencil,
                    blend,
                    attachments,
                    DynamicStates.Viewport | DynamicStates.Scissor | additionalDynamicStates,
                    label,
                    vertexCodeBlob: shader.GetSlangEntryBlob(targetIndex, vertex),
                    pixelCodeBlob: shader.GetSlangEntryBlob(targetIndex, pixel))).GetAwaiter().GetResult();
            return new LinkedRasterPipeline(shader, reflection, pipeline);
    }

    public bool ReplaceShader(RenderPipelines pipelines, Shader previous, Shader next)
    {
        if (!ReferenceEquals(Shader, previous)) return false;
        Pipeline = pipelines.CreateWithShader(Pipeline, next).GetAwaiter().GetResult();
        Reflection = next.GetSlangReflection(next.RequireSlangTarget(pipelines.Device.Capabilities.CompileTarget));
        Shader = next;
        return true;
    }

    public void Dispose()
    {
    }
}
