using SomeEngine.Assets.Schema;
using SomeEngine.Resources;
using SomeEngine.Graphics;
using SlangShaderSharp;
using GraphicsPipeline = SomeEngine.Graphics.Pipeline;

namespace SomeEngine.Render.Assets;

/// <summary>Owns an attachment-compatible RHI pipeline created from a cooked shader program.</summary>
public sealed class LinkedRasterPipeline : IDisposable
{
    private bool _disposed;

    private LinkedRasterPipeline(ShaderProgram program, GraphicsPipeline pipeline)
    {
        Program = program;
        Pipeline = pipeline;
    }

    public ShaderProgram Program { get; }
    public GraphicsPipeline Pipeline { get; }

    public static LinkedRasterPipeline Create(
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
        string? label = null,
        uint sampleMask = uint.MaxValue,
        bool alphaToCoverage = false)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(shader);
        ArgumentException.ThrowIfNullOrWhiteSpace(vertexEntryPoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(pixelEntryPoint);
        GraphicsPipeline? pipeline = null;
        try
        {
            CookedShaderEntry vertex = shader.RequireEntry(
                device.Capabilities.CompileTarget,
                vertexEntryPoint,
                ShaderStage.Vertex);
            CookedShaderEntry pixel = shader.RequireEntry(
                device.Capabilities.CompileTarget,
                pixelEntryPoint,
                ShaderStage.Pixel);
            if (!ReferenceEquals(vertex.Program, pixel.Program))
                throw new InvalidDataException("Raster entries must belong to one cooked shader program.");
            var multisample = new MultisampleState(sampleCount, sampleMask, alphaToCoverage);
            var blend = new BlendState(
                blendAttachments,
                independentBlend: blendAttachments.Length > 1);
            var attachments = new AttachmentFormatSignature(
                colorFormats,
                depthStencilFormat,
                sampleCount);
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
                    rasterizer,
                    multisample,
                    depthStencil,
                    blend,
                    attachments,
                    DynamicStates.Viewport | DynamicStates.Scissor,
                    label));
            return new LinkedRasterPipeline(vertex.Program, pipeline);
        }
        catch (Exception primary)
        {
            List<Exception>? cleanupFailures = null;
            if (pipeline is not null) TryDispose(pipeline, ref cleanupFailures);
            if (cleanupFailures is not null)
            {
                cleanupFailures.Insert(0, primary);
                throw new AggregateException(
                    $"Raster pipeline '{label}' construction failed and cleanup also reported failures.",
                    cleanupFailures);
            }
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        List<Exception>? failures = null;
        TryDispose(Pipeline, ref failures);
        _disposed = true;
        if (failures is not null)
            throw failures.Count == 1 ? failures[0] : new AggregateException(failures);
    }

    private static void TryDispose(IDisposable value, ref List<Exception>? failures)
    {
        try { value.Dispose(); }
        catch (Exception failure) { (failures ??= []).Add(failure); }
    }
}
