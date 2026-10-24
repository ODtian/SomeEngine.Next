using System.Buffers;
using SlangShaderSharp;
using SomeEngine.Graphics;
using SomeEngine.Resources;
using GraphicsPipeline = SomeEngine.Graphics.Pipeline;

namespace SomeEngine.Render.Cluster.Pipeline;

internal abstract class ClusterPipeline : IDisposable
{
    private bool _disposed;

    protected ClusterPipeline(ShaderReflection reflection, GraphicsPipeline pipeline)
    {
        if (reflection == ShaderReflection.Null)
            throw new ArgumentException("Slang reflection is null.", nameof(reflection));
        Reflection = reflection;
        Pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
    }

    internal ShaderReflection Reflection { get; }
    internal VariableLayoutReflection ParameterLayout =>
        Reflection.GetGlobalParamsVarLayout() ?? VariableLayoutReflection.Null;
    internal GraphicsPipeline Pipeline { get; }

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

    protected static void CleanupConstructionFailure(
        Exception primary,
        GraphicsPipeline? pipeline,
        string message)
    {
        List<Exception>? cleanupFailures = null;
        if (pipeline is not null)
            TryDispose(pipeline, ref cleanupFailures);
        if (cleanupFailures is null)
            return;
        cleanupFailures.Insert(0, primary);
        throw new AggregateException(message, cleanupFailures);
    }

    private static void TryDispose(IDisposable value, ref List<Exception>? failures)
    {
        try
        {
            value.Dispose();
        }
        catch (Exception failure)
        {
            (failures ??= []).Add(failure);
        }
    }
}

internal sealed class ClusterComputePipeline : ClusterPipeline
{
    private ClusterComputePipeline(ShaderReflection reflection, GraphicsPipeline pipeline)
        : base(reflection, pipeline)
    {
    }

    internal static ClusterComputePipeline Create(
        IGraphicsBackend backend,
        Device device,
        Shader shader,
        string entryPoint,
        string name)
    {
        int targetIndex = shader.RequireSlangTarget(device.Capabilities.CompileTarget);
        EntryPointReflection entry = shader.RequireSlangEntry(
            targetIndex,
            entryPoint,
            SlangStage.Compute);
        return Create(
            backend,
            device,
            shader,
            entry,
            shader.GetSlangEntryBytes(targetIndex, entry),
            shader.GetSlangEntryBlob(targetIndex, entry),
            name);
    }

    internal static ClusterComputePipeline Create(
        IGraphicsBackend backend,
        Device device,
        Shader shader,
        EntryPointReflection entry,
        string name)
    {
        if (entry == EntryPointReflection.Null || entry.Stage != SlangStage.Compute)
            throw new InvalidDataException($"Cluster compute pipeline '{name}' has no compute entry.");
        int targetIndex = shader.RequireSlangTarget(device.Capabilities.CompileTarget);
        return Create(
            backend,
            device,
            shader,
            entry,
            shader.GetSlangEntryBytes(targetIndex, entry),
            shader.GetSlangEntryBlob(targetIndex, entry),
            name);
    }

    private static ClusterComputePipeline Create(
        IGraphicsBackend backend,
        Device device,
        Shader shader,
        EntryPointReflection entry,
        ReadOnlySequence<byte> code,
        ISlangBlob? codeBlob,
        string name)
    {
        GraphicsPipeline? pipeline = null;
        try
        {
            int targetIndex = shader.RequireSlangTarget(device.Capabilities.CompileTarget);
            ShaderReflection reflection = shader.GetSlangReflection(targetIndex);
            pipeline = backend.CreateComputePipeline(
                device,
                new ComputePipelineDesc(
                    shader.SlangCode,
                    reflection,
                    entry,
                    code,
                    name,
                    CodeBlob: codeBlob));
            return new ClusterComputePipeline(reflection, pipeline);
        }
        catch (Exception primary)
        {
            CleanupConstructionFailure(
                primary,
                pipeline,
                $"Cluster compute pipeline '{name}' construction failed and cleanup also reported failures.");
            throw;
        }
    }
}

internal sealed class ClusterRasterPipeline : ClusterPipeline
{
    private ClusterRasterPipeline(
        ShaderReflection reflection,
        GraphicsPipeline pipeline,
        bool usesMesh,
        string geometryEntry)
        : base(reflection, pipeline)
    {
        UsesMesh = usesMesh;
        GeometryEntry = geometryEntry;
    }

    internal bool UsesMesh { get; }
    internal string GeometryEntry { get; }
    internal bool UsesMaterialRanges =>
        GeometryEntry is "MSVisBufferMeshlet" or ClusterMaterialEntries.HardwareMeshlet;

    internal static bool SupportsMeshlets(IGraphicsBackend backend, Device device) =>
        SupportsMesh(backend, device) &&
        backend.TryGetCapability(device, out MeshShaders? mesh) &&
        mesh!.MaximumOutputVertices >= 64 &&
        mesh.MaximumOutputPrimitives >= 128;

    internal static bool SupportsMesh(IGraphicsBackend backend, Device device) =>
        backend.TryGetCapability(device, out MeshShaders? mesh) &&
        mesh is not null &&
        mesh.IndirectDispatch &&
        mesh.MaximumThreadsPerGroup >= 32 &&
        mesh.MaximumOutputVertices >= 96 &&
        mesh.MaximumOutputPrimitives >= 64;

    internal static ClusterRasterPipeline Create(
        IGraphicsBackend backend,
        Device device,
        Shader shader,
        string geometryEntryPoint,
        string pixelEntryPoint,
        ReadOnlySpan<Format> colorFormats,
        Format? depthStencilFormat = null,
        RasterizerState rasterizer = default,
        DepthStencilState depthStencil = default,
        ReadOnlySpan<BlendAttachmentState> blendAttachments = default,
        uint sampleCount = 1,
        string? name = null,
        uint sampleMask = uint.MaxValue,
        bool alphaToCoverage = false,
        ReadOnlySpan<VertexBufferLayout> vertexBuffers = default,
        ReadOnlySpan<VertexAttribute> vertexAttributes = default,
        SlangStage geometryStage = SlangStage.Vertex)
    {
        GraphicsPipeline? pipeline = null;
        try
        {
            int targetIndex = shader.RequireSlangTarget(device.Capabilities.CompileTarget);
            ShaderReflection reflection = shader.GetSlangReflection(targetIndex);
            EntryPointReflection geometry = shader.RequireSlangEntry(
                targetIndex,
                geometryEntryPoint,
                geometryStage);
            EntryPointReflection pixel = shader.RequireSlangEntry(
                targetIndex,
                pixelEntryPoint,
                SlangStage.Fragment);
            ReadOnlySequence<byte> geometryCode = shader.GetSlangEntryBytes(targetIndex, geometry);
            ReadOnlySequence<byte> pixelCode = shader.GetSlangEntryBytes(targetIndex, pixel);
            ISlangBlob? geometryBlob = shader.GetSlangEntryBlob(targetIndex, geometry);
            ISlangBlob? pixelBlob = shader.GetSlangEntryBlob(targetIndex, pixel);
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
                        DynamicStates.Viewport | DynamicStates.Scissor,
                        name,
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
                        DynamicStates.Viewport | DynamicStates.Scissor,
                        name,
                        vertexCodeBlob: geometryBlob,
                        pixelCodeBlob: pixelBlob));
            return new ClusterRasterPipeline(
                reflection,
                pipeline,
                usesMesh,
                geometryEntryPoint);
        }
        catch (Exception primary)
        {
            CleanupConstructionFailure(
                primary,
                pipeline,
                $"Cluster raster pipeline '{name}' construction failed and cleanup also reported failures.");
            throw;
        }
    }
}
