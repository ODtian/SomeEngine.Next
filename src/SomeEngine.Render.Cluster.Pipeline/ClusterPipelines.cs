using SomeEngine.Assets.Schema;
using SomeEngine.Resources;
using SomeEngine.Graphics;
using SomeEngine.RenderGraph;
using SlangShaderSharp;
using GraphicsPipeline = SomeEngine.Graphics.Pipeline;

namespace SomeEngine.Render.Cluster.Pipeline;

/// <summary>Owns one RHI pipeline created from a cooked shader program.</summary>
internal abstract class ClusterPipeline : IDisposable
{
    private bool _disposed;

    protected ClusterPipeline(
        ShaderProgram program,
        GraphicsPipeline pipeline)
    {
        Program = program ?? throw new ArgumentNullException(nameof(program));
        Pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
    }

    internal ShaderProgram Program { get; }

    internal ShaderVariableLayout ParameterLayout => Program.Layout.GetGlobalParamsVarLayout();

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
        try { value.Dispose(); }
        catch (Exception failure) { (failures ??= []).Add(failure); }
    }
}

/// <summary>A cooked compute program and its compute pipeline.</summary>
internal sealed class ClusterComputePipeline : ClusterPipeline
{
    private ClusterComputePipeline(
        ShaderProgram program,
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
        GraphicsPipeline? pipeline = null;
        try
        {
            CookedShaderEntry cooked = shader.RequireEntry(
                device.Capabilities.ShaderTarget,
                entryPoint,
                ShaderStage.Compute);
            pipeline = backend.CreateComputePipeline(
                device,
                new ComputePipelineDesc(
                    cooked.Program,
                    cooked.EntryPoint,
                    name));
            return new ClusterComputePipeline(cooked.Program, pipeline);
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

    internal static ClusterComputePipeline Create(
        IGraphicsBackend backend,
        Device device,
        in CookedShaderEntry cooked,
        string name)
    {
        GraphicsPipeline? pipeline = null;
        try
        {
            pipeline = backend.CreateComputePipeline(
                device,
                new ComputePipelineDesc(
                    cooked.Program,
                    cooked.EntryPoint,
                    name));
            return new ClusterComputePipeline(cooked.Program, pipeline);
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

/// <summary>Owns one cooked vertex/pixel program and its RHI pipeline.</summary>
internal sealed class ClusterRasterPipeline : ClusterPipeline
{
    private ClusterRasterPipeline(
        ShaderProgram program,
        GraphicsPipeline pipeline, bool usesMesh, string geometryEntry)
        : base(program, pipeline)
    {
        UsesMesh = usesMesh;
        GeometryEntry = geometryEntry;
    }

    internal bool UsesMesh { get; }

    internal string GeometryEntry { get; }

    internal bool UsesMaterialRanges => GeometryEntry is "MSVisBufferMeshlet" or ClusterMaterialEntries.HardwareMeshlet;

    internal static bool SupportsMeshlets(IGraphicsBackend backend, Device device) =>
        SupportsMesh(backend, device) && backend.TryGetCapability(device, out MeshShaders? mesh) &&
        mesh!.MaximumOutputVertices >= 64 && mesh.MaximumOutputPrimitives >= 128;

    internal static bool SupportsMesh(IGraphicsBackend backend, Device device) =>
        backend.TryGetCapability(device, out MeshShaders? mesh) && mesh is not null &&
        mesh.IndirectDispatch && mesh.MaximumThreadsPerGroup >= 32 &&
        mesh.MaximumOutputVertices >= 96 && mesh.MaximumOutputPrimitives >= 64;

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
        bool alphaToCoverage = false,
        ReadOnlySpan<VertexBufferLayout> vertexBuffers = default,
        ReadOnlySpan<VertexAttribute> vertexAttributes = default,
        ShaderStage geometryStage = ShaderStage.Vertex)
    {
        GraphicsPipeline? pipeline = null;
        try
        {
            CookedShaderEntry vertex = shader.RequireEntry(
                device.Capabilities.ShaderTarget,
                vertexEntryPoint,
                geometryStage);
            CookedShaderEntry pixel = shader.RequireEntry(
                device.Capabilities.ShaderTarget,
                pixelEntryPoint,
                ShaderStage.Pixel);
            if (!ReferenceEquals(vertex.Program, pixel.Program))
                throw new InvalidDataException("Cluster raster entries must belong to one cooked shader program.");
            var multisample = new MultisampleState(sampleCount, sampleMask, alphaToCoverage);
            var blend = new BlendState(
                blendAttachments,
                independentBlend: blendAttachments.Length > 1);
            var attachments = new AttachmentFormatSignature(
                colorFormats,
                depthStencilFormat,
                sampleCount);
            var description = new GraphicsPipelineDesc(
                vertex.Program,
                vertex.EntryPoint,
                pixel.EntryPoint,
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
                name);
            bool usesMesh = geometryStage == ShaderStage.Mesh;
            pipeline = usesMesh
                ? backend.CreateMeshPipeline(device, new MeshPipelineDesc(
                    vertex.Program, vertex.EntryPoint, ShaderEntryPoint.Null, pixel.EntryPoint,
                    rasterizer, multisample, depthStencil, blend, attachments,
                    DynamicStates.Viewport | DynamicStates.Scissor, name))
                : backend.CreateGraphicsPipeline(device, description);
            return new ClusterRasterPipeline(vertex.Program, pipeline, usesMesh, vertexEntryPoint);
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
