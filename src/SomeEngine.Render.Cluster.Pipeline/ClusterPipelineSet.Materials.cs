using SomeEngine.Graphics;
using SomeEngine.Render.Assets;
using SomeEngine.Resources;
using SlangShaderSharp;

namespace SomeEngine.Render.Cluster.Pipeline;

internal readonly record struct ClusterComputeKey(
    SlangShaderData SlangCode,
    EntryPointReflection EntryPoint);

internal readonly record struct ClusterRasterHardwareKey(
    SlangShaderData SlangCode,
    EntryPointReflection Vertex,
    EntryPointReflection Pixel);

internal sealed partial class ClusterPipelineSet
{
    private readonly Dictionary<ClusterComputeKey, LinkedComputePipeline> _materialComputePipelines = [];
    private readonly Dictionary<ClusterRasterHardwareKey, LinkedRasterPipeline> _materialHardwarePipelines = [];
    private readonly HashSet<HashSet<IDisposable>> _materialPipelineUses = [];

    internal HashSet<IDisposable> RegisterMaterialPipelineUse()
    {
        HashSet<IDisposable> use = [];
        _materialPipelineUses.Add(use);
        return use;
    }

    internal LinkedComputePipeline GetMaterialComputePipeline(Shader shader, EntryPointReflection entry)
    {
        var key = new ClusterComputeKey(shader.SlangCode, entry);
        if (!_materialComputePipelines.TryGetValue(key, out LinkedComputePipeline? pipeline))
        {
            pipeline = LinkedComputePipeline.Create(
                _backend, _device, shader, entry, $"Cluster material compute {entry.Name}");
            _materialComputePipelines.Add(key, pipeline);
        }
        return pipeline;
    }

    internal LinkedRasterPipeline GetMaterialHardwarePipeline(
        Shader shader, EntryPointReflection vertex, EntryPointReflection pixel)
    {
        var key = new ClusterRasterHardwareKey(shader.SlangCode, vertex, pixel);
        if (_materialHardwarePipelines.TryGetValue(key, out LinkedRasterPipeline? pipeline))
            return pipeline;
        string vertexEntry = vertex.Name;
        string pixelEntry = pixel.Name;
        bool targetOutput = pixelEntry is ClusterMaterialEntries.HardwarePixelTarget or
            ClusterMaterialEntries.HardwareMeshPixelTarget;
        bool depthOutput = !targetOutput && VisibilityMode == ClusterVisibilityMode.HardwareOnly;
        bool tessellationVertex = vertexEntry == ClusterMaterialEntries.TessellationHardwareVertex;
        pipeline = LinkedRasterPipeline.Create(
            _backend,
            _device,
            shader,
            vertex.Stage,
            vertexEntry,
            pixelEntry,
            depthOutput ? [Format.R32UInt] : [],
            depthStencilFormat: depthOutput ? Format.D32Float : null,
            rasterizer: new RasterizerState(Cull: ClusterGeometryRequirements.UsesBothRasterFaces(pixel, targetOutput)
                ? CullType.None : CullType.Back),
            depthStencil: depthOutput
                ? new DepthStencilState(DepthTest: true, DepthWrite: true, DepthComparison: CompareOperation.LessOrEqual)
                : default,
            label: $"Cluster material hardware raster {vertexEntry}/{pixelEntry}",
            vertexBuffers: tessellationVertex ? [new VertexBufferLayout(0, sizeof(uint), PerInstance: true)] : [],
            vertexAttributes: tessellationVertex ? [new VertexAttribute(0, 0, Format.R32UInt, 0)] : []);
        _materialHardwarePipelines.Add(key, pipeline);
        return pipeline;
    }

    // Called after the graph accepts its material changes, when future recordings use these stages.
    internal void UpdateMaterialPipelineUse(HashSet<IDisposable> use,
        ClusterRasterStageSet rasterStages, ClusterShadeStageSet shadeStages)
    {
        use.Clear();
        foreach (ClusterRasterInvocation raster in rasterStages.Invocations)
        {
            if (raster.Software is { } software) use.Add(software);
            use.Add(raster.Hardware);
            if (raster.TargetStages is { } target)
            {
                if (target.Software is { } targetSoftware) use.Add(targetSoftware);
                if (target.Hardware is { } targetHardware) use.Add(targetHardware);
            }
            if (raster.Tessellation is { } tessellation)
            {
                use.Add(tessellation.Seed);
                use.Add(tessellation.Split);
                if (tessellation.Dice is { } dice) use.Add(dice);
                if (tessellation.Hardware is { } hardware) use.Add(hardware);
            }
            if (raster.Deformation is { } deformation)
                foreach (ClusterDeformStage stage in deformation.Stages) use.Add(stage.Pipeline);
        }
        foreach (ClusterShadeInvocation shade in shadeStages.Invocations)
        {
            use.Add(shade.Pipeline);
            if (shade.RequestShader is { } receiver) use.Add(receiver.Pipeline);
        }
        CollectUnusedMaterialPipelines();
    }

    internal void ReleaseMaterialPipelineUse(HashSet<IDisposable> use)
    {
        _materialPipelineUses.Remove(use);
        // The final view's current set bridges scene reload and the existing weak Shader values.
        // The next accepted material graph replaces it with the newly used pipelines.
        CollectUnusedMaterialPipelines(_materialPipelineUses.Count == 0 ? use : null);
        use.Clear();
    }

    private void CollectUnusedMaterialPipelines(HashSet<IDisposable>? previousUse = null)
    {
        HashSet<IDisposable> used = previousUse ?? [];
        foreach (HashSet<IDisposable> use in _materialPipelineUses) used.UnionWith(use);
        List<ClusterComputeKey> computeKeys = [];
        foreach ((ClusterComputeKey key, LinkedComputePipeline pipeline) in _materialComputePipelines)
            if (!used.Contains(pipeline)) computeKeys.Add(key);
        List<ClusterRasterHardwareKey> hardwareKeys = [];
        foreach ((ClusterRasterHardwareKey key, LinkedRasterPipeline pipeline) in _materialHardwarePipelines)
            if (!used.Contains(pipeline)) hardwareKeys.Add(key);
        List<Exception>? failures = null;
        foreach (ClusterComputeKey key in computeKeys)
        {
            _materialComputePipelines.Remove(key, out LinkedComputePipeline? pipeline);
            TryDispose(pipeline!, ref failures);
        }
        foreach (ClusterRasterHardwareKey key in hardwareKeys)
        {
            _materialHardwarePipelines.Remove(key, out LinkedRasterPipeline? pipeline);
            TryDispose(pipeline!, ref failures);
        }
        if (failures is not null)
            throw failures.Count == 1 ? failures[0] : new AggregateException(failures);
    }

    private void DisposeMaterialPipelines(ref List<Exception>? failures)
    {
        foreach (LinkedComputePipeline pipeline in _materialComputePipelines.Values)
            TryDispose(pipeline, ref failures);
        foreach (LinkedRasterPipeline pipeline in _materialHardwarePipelines.Values)
            TryDispose(pipeline, ref failures);
        _materialComputePipelines.Clear();
        _materialHardwarePipelines.Clear();
        _materialPipelineUses.Clear();
    }
}
