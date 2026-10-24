using SomeEngine.Assets.Schema;
using SomeEngine.Resources;
using SomeEngine.Graphics;
using SlangShaderSharp;
using GraphicsPipeline = SomeEngine.Graphics.Pipeline;

namespace SomeEngine.Render.Assets;

/// <summary>Owns an attachment-compatible RHI pipeline created from direct Slang data.</summary>
public sealed class LinkedRasterPipeline : IDisposable
{
    private bool _disposed;

    private LinkedRasterPipeline(ShaderReflection reflection, GraphicsPipeline pipeline)
    {
        Reflection = reflection;
        Pipeline = pipeline;
    }

    public ShaderReflection Reflection { get; }
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
        bool alphaToCoverage = false,
        DynamicStates additionalDynamicStates = DynamicStates.None)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(shader);
        ArgumentException.ThrowIfNullOrWhiteSpace(vertexEntryPoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(pixelEntryPoint);
        GraphicsPipeline? pipeline = null;
        try
        {
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
                    pixelCodeBlob: shader.GetSlangEntryBlob(targetIndex, pixel)));
            return new LinkedRasterPipeline(reflection, pipeline);
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
