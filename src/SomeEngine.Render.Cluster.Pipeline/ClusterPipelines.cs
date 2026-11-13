using System.Buffers;
using SlangShaderSharp;
using SomeEngine.Graphics;
using SomeEngine.Resources;
using GraphicsPipeline = SomeEngine.Graphics.Pipeline;

namespace SomeEngine.Render.Cluster.Pipeline;

internal abstract class ClusterPipeline : IDisposable
{
    protected ClusterPipeline(ShaderReflection reflection, GraphicsPipeline pipeline, ShaderParameterResources parameterResources)
    {
        Reflection = reflection;
        Pipeline = pipeline;
        ParameterResources = parameterResources;
    }

    internal ShaderReflection Reflection { get; }
    internal VariableLayoutReflection ParameterLayout =>
        Reflection.GetGlobalParamsVarLayout() ?? VariableLayoutReflection.Null;
    internal GraphicsPipeline Pipeline { get; }
    internal ShaderParameterResources ParameterResources { get; }

    public void Dispose() { }
}

internal sealed class ClusterComputePipeline : ClusterPipeline
{
    private ClusterComputePipeline(ShaderReflection reflection, GraphicsPipeline pipeline, ShaderParameterResources parameterResources)
        : base(reflection, pipeline, parameterResources) { }

    internal static ClusterComputePipeline Create(
        RenderPipelines pipelines, Shader shader, string entryPoint, string name)
    {
        int target = shader.RequireSlangTarget(pipelines.Device.Capabilities.CompileTarget);
        return Create(pipelines, shader, shader.RequireSlangEntry(target, entryPoint, SlangStage.Compute), name);
    }

    internal static ClusterComputePipeline Create(
        RenderPipelines pipelines, Shader shader, EntryPointReflection entry, string name)
    {
        int target = shader.RequireSlangTarget(pipelines.Device.Capabilities.CompileTarget);
        ShaderReflection reflection = shader.GetSlangReflection(target);
        GraphicsPipeline pipeline = pipelines.CreateCompute(new ComputePipelineDesc(
            shader.SlangCode, reflection, entry, shader.GetSlangEntryBytes(target, entry), name,
            CodeBlob: shader.GetSlangEntryBlob(target, entry))).GetAwaiter().GetResult();
        return new ClusterComputePipeline(reflection, pipeline,
            ShaderParameterResources.Create(shader, pipelines.Device.Capabilities.CompileTarget, entry.Name));
    }
}

internal sealed class ClusterRasterPipeline : ClusterPipeline
{
    private ClusterRasterPipeline(ShaderReflection reflection, GraphicsPipeline pipeline,
        bool usesMesh, string geometryEntry, ShaderParameterResources parameterResources) : base(reflection, pipeline, parameterResources)
    {
        UsesMesh = usesMesh;
        GeometryEntry = geometryEntry;
    }

    internal bool UsesMesh { get; }
    internal string GeometryEntry { get; }
    internal bool UsesMaterialRanges =>
        GeometryEntry is "MSVisBufferMeshlet" or ClusterMaterialEntries.HardwareMeshlet or "ClusterRasterMeshletTarget";

    internal static bool SupportsMeshlets(IGraphicsBackend backend, Device device) =>
        SupportsMesh(backend, device) && backend.TryGetCapability(device, out MeshShaders? mesh) &&
        mesh!.MaximumOutputVertices >= 64 && mesh.MaximumOutputPrimitives >= 128;

    internal static bool SupportsMesh(IGraphicsBackend backend, Device device) =>
        backend.TryGetCapability(device, out MeshShaders? mesh) && mesh is not null && mesh.IndirectDispatch &&
        mesh.MaximumThreadsPerGroup >= 32 && mesh.MaximumOutputVertices >= 96 && mesh.MaximumOutputPrimitives >= 64;

    internal static ClusterRasterPipeline Create(
        RenderPipelines pipelines, Shader shader, string geometryEntryPoint, string pixelEntryPoint,
        ReadOnlySpan<Format> colorFormats, Format? depthStencilFormat = null,
        RasterizerState rasterizer = default, DepthStencilState depthStencil = default,
        ReadOnlySpan<BlendAttachmentState> blendAttachments = default, uint sampleCount = 1,
        string? name = null, uint sampleMask = uint.MaxValue, bool alphaToCoverage = false,
        ReadOnlySpan<VertexBufferLayout> vertexBuffers = default,
        ReadOnlySpan<VertexAttribute> vertexAttributes = default, SlangStage geometryStage = SlangStage.Vertex)
    {
        int target = shader.RequireSlangTarget(pipelines.Device.Capabilities.CompileTarget);
        ShaderReflection reflection = shader.GetSlangReflection(target);
        EntryPointReflection geometry = shader.RequireSlangEntry(target, geometryEntryPoint, geometryStage);
        EntryPointReflection pixel = shader.RequireSlangEntry(target, pixelEntryPoint, SlangStage.Fragment);
        ReadOnlySequence<byte> geometryCode = shader.GetSlangEntryBytes(target, geometry);
        ReadOnlySequence<byte> pixelCode = shader.GetSlangEntryBytes(target, pixel);
        ISlangBlob? geometryBlob = shader.GetSlangEntryBlob(target, geometry);
        ISlangBlob? pixelBlob = shader.GetSlangEntryBlob(target, pixel);
        var multisample = new MultisampleState(sampleCount, sampleMask, alphaToCoverage);
        var blend = new BlendState(blendAttachments, independentBlend: blendAttachments.Length > 1);
        var attachments = new AttachmentFormatSignature(colorFormats, depthStencilFormat, sampleCount);
        bool usesMesh = geometryStage == SlangStage.Mesh;
        GraphicsPipeline pipeline = usesMesh
            ? pipelines.CreateMesh(new MeshPipelineDesc(shader.SlangCode, reflection, geometry, geometryCode,
                EntryPointReflection.Null, ReadOnlySequence<byte>.Empty, pixel, pixelCode,
                rasterizer, multisample, depthStencil, blend, attachments,
                DynamicStates.Viewport | DynamicStates.Scissor, name,
                meshCodeBlob: geometryBlob, pixelCodeBlob: pixelBlob)).GetAwaiter().GetResult()
            : pipelines.CreateGraphics(new GraphicsPipelineDesc(shader.SlangCode, reflection, geometry, geometryCode,
                pixel, pixelCode, vertexBuffers, vertexAttributes, PrimitiveTopology.TriangleList, StripCut.Disabled,
                rasterizer, multisample, depthStencil, blend, attachments,
                DynamicStates.Viewport | DynamicStates.Scissor, name,
                vertexCodeBlob: geometryBlob, pixelCodeBlob: pixelBlob)).GetAwaiter().GetResult();
        return new ClusterRasterPipeline(reflection, pipeline, usesMesh, geometryEntryPoint,
            ShaderParameterResources.Create(shader, pipelines.Device.Capabilities.CompileTarget, geometryEntryPoint, pixelEntryPoint));
    }
}
