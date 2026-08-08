using SomeEngine.Assets.Schema;
using SomeEngine.Graphics;
using SomeEngine.Render.Assets;
using SomeEngine.RenderGraph;
using GraphicsPipeline = SomeEngine.Graphics.Pipeline;

namespace SomeEngine.Render.Cluster.Pipeline;

/// <summary>Slang-linked compute state shared by one Cluster compute pass.</summary>
internal sealed class ClusterComputeShader : IDisposable
{
    private ClusterComputeShader(
        LiveShaderProgram program,
        GraphicsPipeline pipeline)
    {
        Program = program;
        Pipeline = pipeline;
    }

    internal LiveShaderProgram Program { get; }

    internal GraphicsPipeline Pipeline { get; }

    internal static ClusterComputeShader Create(
        IGraphicsBackend backend,
        Device device,
        Shader shader,
        string entryPoint,
        string name)
    {
        LiveShaderProgram? program = null;
        GraphicsPipeline? pipeline = null;
        try
        {
            program = LiveShaderProgram.Link(
                shader,
                [new LiveShaderEntry(entryPoint, LiveShaderStage.Compute)]);
            pipeline = backend.CreateComputePipeline(
                device,
                new ComputePipelineDesc(
                    program.Program,
                    program.GetEntryPoint(0),
                    name));
            return new ClusterComputeShader(program, pipeline);
        }
        catch
        {
            pipeline?.Dispose();
            program?.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        List<Exception>? failures = null;
        TryDispose(Pipeline, ref failures);
        TryDispose(Program, ref failures);
        if (failures is not null)
            throw failures.Count == 1 ? failures[0] : new AggregateException(failures);
    }

    private static void TryDispose(IDisposable value, ref List<Exception>? failures)
    {
        try { value.Dispose(); }
        catch (Exception failure) { (failures ??= []).Add(failure); }
    }
}

/// <summary>Slang-linked vertex/pixel state with one complete parameter layout.</summary>
internal sealed class ClusterRasterShader : IDisposable
{
    private ClusterRasterShader(
        LiveShaderProgram program,
        GraphicsPipeline pipeline)
    {
        Program = program;
        Pipeline = pipeline;
    }

    internal LiveShaderProgram Program { get; }

    internal GraphicsPipeline Pipeline { get; }

    internal static ClusterRasterShader Create(
        IGraphicsBackend backend,
        Device device,
        Shader shader,
        string vertexEntryPoint,
        string pixelEntryPoint,
        ReadOnlySpan<Format> colorFormats,
        Format? depthStencilFormat = null,
        RasterizerState rasterizer = default,
        DepthStencilState depthStencil = default,
        ReadOnlySpan<BlendAttachmentState> blendAttachments = default,
        uint sampleCount = 1,
        string? name = null,
        uint sampleMask = uint.MaxValue,
        bool alphaToCoverage = false)
    {
        LiveShaderProgram? program = null;
        GraphicsPipeline? pipeline = null;
        try
        {
            program = LiveShaderProgram.Link(
                shader,
                [
                    new LiveShaderEntry(vertexEntryPoint, LiveShaderStage.Vertex),
                    new LiveShaderEntry(pixelEntryPoint, LiveShaderStage.Pixel),
                ]);
            var multisample = new MultisampleState(sampleCount, sampleMask, alphaToCoverage);
            var blend = new BlendState(
                blendAttachments,
                independentBlend: blendAttachments.Length > 1);
            var attachments = new AttachmentFormatSignature(
                colorFormats,
                depthStencilFormat,
                sampleCount);
            var description = new GraphicsPipelineDesc(
                program.Program,
                program.GetEntryPoint(0),
                program.GetEntryPoint(1),
                vertexBuffers: default,
                vertexAttributes: default,
                PrimitiveTopology.TriangleList,
                StripCut.Disabled,
                rasterizer,
                multisample,
                depthStencil,
                blend,
                attachments,
                DynamicStates.Viewport | DynamicStates.Scissor,
                name);
            pipeline = backend.CreateGraphicsPipeline(device, description);
            return new ClusterRasterShader(program, pipeline);
        }
        catch
        {
            pipeline?.Dispose();
            program?.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        List<Exception>? failures = null;
        TryDispose(Pipeline, ref failures);
        TryDispose(Program, ref failures);
        if (failures is not null)
            throw failures.Count == 1 ? failures[0] : new AggregateException(failures);
    }

    private static void TryDispose(IDisposable value, ref List<Exception>? failures)
    {
        try { value.Dispose(); }
        catch (Exception failure) { (failures ??= []).Add(failure); }
    }
}
