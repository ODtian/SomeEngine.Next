using System.Buffers;
using SomeEngine.Resources;
using SomeEngine.Graphics;
using SlangShaderSharp;
using GraphicsPipeline = SomeEngine.Graphics.Pipeline;

namespace SomeEngine.Render.Assets;

/// <summary>Owns an attachment-compatible graphics or mesh RHI pipeline created from direct Slang data.</summary>
public sealed class LinkedRasterPipeline : IDisposable
{
    private readonly Shader _shader;
    private ShaderParameterLayout? _globalResources;
    private bool _disposed;

    private LinkedRasterPipeline(
        Shader shader,
        ShaderReflection reflection,
        GraphicsPipeline pipeline,
        bool usesMesh,
        string geometryEntry)
    {
        if (reflection == ShaderReflection.Null)
            throw new ArgumentException("Slang reflection is null.", nameof(reflection));
        _shader = shader;
        Reflection = reflection;
        Pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
        UsesMesh = usesMesh;
        GeometryEntry = geometryEntry;
    }

    /// <summary>Gets the full global resource order, without entry filtering or block markers.</summary>
    public ShaderParameterLayout GlobalResources
    {
        get
        {
            ShaderParameterLayout resources = _globalResources ??= ShaderParameterLayout.FromReflection(ParameterLayout);
            GC.KeepAlive(_shader);
            return resources;
        }
    }

    public ShaderReflection Reflection { get; }
    /// <summary>A borrowed global block; keep this pipeline alive while consuming it.</summary>
    public VariableLayoutReflection ParameterLayout
    {
        get
        {
            try { return Reflection.GetGlobalParamsVarLayout() ?? VariableLayoutReflection.Null; }
            finally { GC.KeepAlive(_shader); }
        }
    }
    public GraphicsPipeline Pipeline { get; }
    public bool UsesMesh { get; }
    public string GeometryEntry { get; }

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
        DynamicStates additionalDynamicStates = DynamicStates.None) =>
        Create(backend, device, shader, SlangStage.Vertex, vertexEntryPoint, pixelEntryPoint,
            colorFormats, depthStencilFormat, rasterizer, depthStencil, blendAttachments,
            sampleCount, label, sampleMask, alphaToCoverage,
            additionalDynamicStates: additionalDynamicStates);

    public static LinkedRasterPipeline Create(
        IGraphicsBackend backend,
        Device device,
        Shader shader,
        SlangStage geometryStage,
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
        ReadOnlySpan<VertexBufferLayout> vertexBuffers = default,
        ReadOnlySpan<VertexAttribute> vertexAttributes = default,
        DynamicStates additionalDynamicStates = DynamicStates.None)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(shader);
        ArgumentException.ThrowIfNullOrWhiteSpace(vertexEntryPoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(pixelEntryPoint);
        if (geometryStage is not SlangStage.Vertex and not SlangStage.Mesh)
            throw new ArgumentOutOfRangeException(nameof(geometryStage));
        GraphicsPipeline? pipeline = null;
        try
        {
            int targetIndex = shader.RequireSlangTarget(device.Capabilities.CompileTarget);
            ShaderReflection reflection = shader.GetSlangReflection(targetIndex);
            EntryPointReflection geometry = shader.RequireSlangEntry(
                targetIndex,
                vertexEntryPoint,
                geometryStage);
            EntryPointReflection pixel = shader.RequireSlangEntry(
                targetIndex,
                pixelEntryPoint,
                SlangStage.Fragment);
            int geometryIndex = shader.GetSlangEntryIndex(targetIndex, geometry);
            ReadOnlySequence<byte> geometryCode = shader.GetSlangEntryBytes(targetIndex, geometryIndex);
            int pixelIndex = shader.GetSlangEntryIndex(targetIndex, pixel);
            ReadOnlySequence<byte> pixelCode = shader.GetSlangEntryBytes(targetIndex, pixelIndex);
            ISlangBlob? geometryBlob = shader.GetSlangEntryBlob(targetIndex, geometryIndex);
            ISlangBlob? pixelBlob = shader.GetSlangEntryBlob(targetIndex, pixelIndex);
            var multisample = new MultisampleState(sampleCount, sampleMask, alphaToCoverage);
            var blend = new BlendState(
                blendAttachments,
                independentBlend: blendAttachments.Length > 1);
            var attachments = new AttachmentFormatSignature(
                colorFormats,
                depthStencilFormat,
                sampleCount);
            bool usesMesh = geometryStage == SlangStage.Mesh;
            pipeline = usesMesh
                ? backend.CreateMeshPipeline(
                    device,
                    new MeshPipelineDesc(
                        shader.SlangCode,
                        reflection,
                        geometry,
                        geometryCode,
                        EntryPointReflection.Null,
                        ReadOnlySequence<byte>.Empty,
                        pixel,
                        pixelCode,
                        rasterizer,
                        multisample,
                        depthStencil,
                        blend,
                        attachments,
                        DynamicStates.Viewport | DynamicStates.Scissor | additionalDynamicStates,
                        label,
                        meshCodeBlob: geometryBlob,
                        pixelCodeBlob: pixelBlob))
                : backend.CreateGraphicsPipeline(
                    device,
                    new GraphicsPipelineDesc(
                        shader.SlangCode,
                        reflection,
                        geometry,
                        geometryCode,
                        pixel,
                        pixelCode,
                        vertexBuffers,
                        vertexAttributes,
                        PrimitiveTopology.TriangleList,
                        StripCut.Disabled,
                        rasterizer,
                        multisample,
                        depthStencil,
                        blend,
                        attachments,
                        DynamicStates.Viewport | DynamicStates.Scissor | additionalDynamicStates,
                        label,
                        vertexCodeBlob: geometryBlob,
                        pixelCodeBlob: pixelBlob));
            return new LinkedRasterPipeline(shader, reflection, pipeline, usesMesh, vertexEntryPoint);
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
        finally { GC.KeepAlive(shader); }
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
