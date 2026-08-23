using SomeEngine.Assets.Schema;
using SomeEngine.Graphics;
using SomeEngine.Render.Assets;
using SomeEngine.RenderGraph;
using GraphicsPipeline = SomeEngine.Graphics.Pipeline;

namespace SomeEngine.Render.Cluster.Pipeline;

/// <summary>Owns one linked shader program and the RHI pipeline created from it.</summary>
internal abstract class ClusterPipeline : IDisposable
{
    private bool _disposed;

    protected ClusterPipeline(
        LiveShaderProgram program,
        GraphicsPipeline pipeline)
    {
        Program = program ?? throw new ArgumentNullException(nameof(program));
        Pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
    }

    internal LiveShaderProgram Program { get; }

    internal GraphicsPipeline Pipeline { get; }

    public void Dispose()
    {
        if (_disposed)
            return;
        List<Exception>? failures = null;
        TryDispose(Pipeline, ref failures);
        TryDispose(Program, ref failures);
        _disposed = true;
        if (failures is not null)
            throw failures.Count == 1 ? failures[0] : new AggregateException(failures);
    }

    protected static void CleanupConstructionFailure(
        Exception primary,
        GraphicsPipeline? pipeline,
        LiveShaderProgram? program,
        string message)
    {
        List<Exception>? cleanupFailures = null;
        if (pipeline is not null)
            TryDispose(pipeline, ref cleanupFailures);
        if (program is not null)
            TryDispose(program, ref cleanupFailures);
        if (cleanupFailures is null)
            return;
        cleanupFailures.Insert(0, primary);
        throw new AggregateException(message, cleanupFailures);
    }

    private static void TryDispose(IDisposable value, ref List<Exception>? failures)
    {
        try { value.Dispose(); }
        catch (Exception failure) { (failures ??= []).Add(failure); }
    }
}

/// <summary>A linked compute program and its compute pipeline.</summary>
internal sealed class ClusterComputePipeline : ClusterPipeline
{
    private ClusterComputePipeline(
        LiveShaderProgram program,
        GraphicsPipeline pipeline)
        : base(program, pipeline)
    {
    }

    internal static ClusterComputePipeline Create(
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
            return new ClusterComputePipeline(program, pipeline);
        }
        catch (Exception primary)
        {
            CleanupConstructionFailure(
                primary,
                pipeline,
                program,
                $"Cluster compute pipeline '{name}' construction failed and cleanup also reported failures.");
            throw;
        }
    }
}

/// <summary>Owns one linked vertex/pixel program and the RHI pipeline created from it.</summary>
internal sealed class ClusterRasterPipeline : ClusterPipeline
{
    private ClusterRasterPipeline(
        LiveShaderProgram program,
        GraphicsPipeline pipeline)
        : base(program, pipeline)
    {
    }

    internal static ClusterRasterPipeline Create(
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
            return new ClusterRasterPipeline(program, pipeline);
        }
        catch (Exception primary)
        {
            CleanupConstructionFailure(
                primary,
                pipeline,
                program,
                $"Cluster raster pipeline '{name}' construction failed and cleanup also reported failures.");
            throw;
        }
    }
}
