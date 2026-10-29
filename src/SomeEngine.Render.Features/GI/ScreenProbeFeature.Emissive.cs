using SomeEngine.Graphics;
using SomeEngine.RenderGraph;
using SomeEngine.Resources;
using TextureViewDimension = SomeEngine.Graphics.TextureViewDimension;

namespace SomeEngine.Render.Features.GI;

public sealed partial class ScreenProbeFeature
{
    private GiEmissiveLights? _emissive;
    private uint DirectPixelCount => (uint)_width * (uint)_height * _views;
    private uint DirectTileCount => (((uint)_width + 7u) / 8u) * (((uint)_height + 7u) / 8u) * _views;

    private void CreateEmissiveResources(ref RenderGraphEdit edit)
    {
        GraphBufferInput settings = edit.CreateBufferInput(BufferUsages.Constant);
        var uniforms = new FeatureUniformBuffer(ref edit, [settings],
            _device.Capabilities.Limits.ConstantBufferAlignment, "Emissive direct-light constants");
        GraphTextureId raw = Texture(ref edit, _views, "Emissive direct-light samples");
        GraphTextureId filtered = Texture(ref edit, _views, "Emissive direct-light filtered samples");
        GraphTextureId history = Texture(ref edit, _views * 2u, "Emissive direct-light history");
        _emissive = new(settings, uniforms, raw, filtered, history);
        AddBuffer(ref edit, "GiEmissionBounds", 2, 16);
        AddBuffer(ref edit, "GiEmitterInstanceRanges", 1, 8);
        AddBuffer(ref edit, "GiEmitterState", 2, 16);
        AddBuffer(ref edit, "GiEmissiveTriangles", _candidateCapacity, 112);
        AddBuffer(ref edit, "GiEmitterPrefix", _candidateCapacity, 4);
        AddBuffer(ref edit, "GiEmitterGroupPrefix", (_candidateCapacity + 63u) / 64u, 4);
        AddBuffer(ref edit, "GiDirectProposals", DirectTileCount * 2u, 8);
        AddBuffer(ref edit, "GiDirectGuides", DirectTileCount * 2u, 32);
        AddBuffer(ref edit, "GiDirectWeights", DirectPixelCount, 16);
        AddBuffer(ref edit, "GiDirectSurfaces", DirectPixelCount * 2u, 16);
        AddBuffer(ref edit, "GiEmissionDispatch", 3, 4, BufferUsages.Indirect);

        GraphTextureId Texture(ref RenderGraphEdit graph, uint layers, string label) =>
            graph.CreatePersistentTexture(new(TextureDimension.Texture2D, (uint)_width, (uint)_height,
                1, 1, layers, 1, Format.R16G16B16A16Float, TextureUsages.Storage | TextureUsages.Sampled, label: label));
    }

    private void InitializeEmissiveHistory(ref RenderGraphEdit edit)
    {
        GiEmissiveLights lights = _emissive!;
        lights.Initialize = AddPass(ref edit, "InitializeGiEmissiveHistory", DirectPixelCount * 2u, initializesBuffers: true);
        edit.OrderAfter(lights.Initialize, _clearBuffers);
        edit.OrderAfter(ClearSurfacesPass, lights.Initialize);
    }

    private GraphPassId ConnectEmissive(ref RenderGraphEdit edit, GraphPassId previous)
    {
        GiEmissiveLights lights = _emissive!;
        lights.Begin = Then(ref edit, previous, "BeginGiEmissiveWork", Math.Max(_materialCapacity, 3u));
        lights.Count = Then(ref edit, lights.Begin, "CountGiEmissiveTriangles", 64);
        GraphPassId last = Then(ref edit, lights.Count, "BuildGiEmitterInstanceRanges", 1, threads: 1);
        lights.Collect = Then(ref edit, last, "CollectGiEmissiveTriangles", 64);
        last = Then(ref edit, lights.Collect, "BuildGiMaterialWorkRanges", 1, threads: 1);
        lights.GeometryWork = Then(ref edit, last, "ScatterGiTriangleCandidates", _candidateCapacity);
        lights.Distribution = Then(ref edit, lights.GeometryWork, "BuildGiEmitterDistribution", _candidateCapacity);
        last = Then(ref edit, lights.Distribution, "BuildGiEmitterGroupDistribution", 1, threads: 1);
        last = Then(ref edit, last, "SampleGiDirectProposals", DirectTileCount * 2u);
        lights.ClearDirectWork = Then(ref edit, last, "ClearGiMaterialWork", _materialCapacity);
        last = Then(ref edit, lights.ClearDirectWork, "CreateGiDirectSamples", DirectPixelCount);
        last = Then(ref edit, last, "BuildGiMaterialWorkRanges", 1, threads: 1);
        lights.DirectWork = Then(ref edit, last, "ScatterGiDirectSamples", DirectPixelCount);
        lights.Resolve = Then(ref edit, lights.DirectWork, "ResolveGiDirectLight", DirectPixelCount);
        last = Then(ref edit, lights.Resolve, "UpdateGiDirectGuides", DirectTileCount);
        last = Then(ref edit, last, "FilterGiDirectLight", DirectPixelCount);
        return Then(ref edit, last, "AccumulateGiDirectLight", DirectPixelCount);
    }

    private void ReconcileEmissiveResources(ref RenderGraphEdit edit, uint instanceCount, uint materialCount)
    {
        GiEmissiveLights lights = _emissive!;
        if (instanceCount > lights.InstanceCapacity)
        {
            Resize(ref edit, "GiEmitterInstanceRanges", instanceCount);
            lights.InstanceCapacity = instanceCount;
        }
        if (materialCount > lights.MaterialCapacity)
        {
            Resize(ref edit, "GiEmissionBounds", materialCount * 2u);
            lights.MaterialCapacity = materialCount;
        }

        void Resize(ref RenderGraphEdit graph, string name, uint count)
        {
            BufferData buffer = _buffers[name];
            graph.SetBufferDescription(buffer.Id, new BufferDesc((ulong)count * buffer.Stride, buffer.Usages, name));
            _resetBuffers = true;
        }
    }

    private GraphPassId[] AddEmissiveMaterialPasses(ref RenderGraphEdit edit, uint index,
        Shader shader, IGraphResourceProvider resources, IGraphResourceProvider surfaceResources)
    {
        GiEmissiveLights lights = _emissive!;
        GraphPassId classify = _cluster!.AddMaterialComputePass(ref edit, index, shader, "ClassifyGiMaterialEmission",
            _buffers["GiEmissionDispatch"].Id, 0, resources, _traceRegion);
        edit.OrderAfter(classify, lights.Begin);
        edit.OrderAfter(lights.Count, classify);
        GraphPassId geometry = _cluster.AddMaterialComputePass(ref edit, index, shader, "UpdateGiEmissiveTriangles",
            _buffers["GiMaterialDispatch"].Id, index * 12ul, resources, _traceRegion);
        edit.OrderAfter(geometry, lights.GeometryWork);
        edit.OrderAfter(lights.Distribution, geometry);
        GraphPassId shade = _cluster.AddMaterialComputePass(ref edit, index, shader, "ShadeGiMaterial",
            _buffers["GiMaterialDispatch"].Id, index * 12ul, surfaceResources, _traceRegion);
        edit.OrderAfter(shade, lights.DirectWork);
        edit.OrderAfter(lights.Resolve, shade);
        return [classify, geometry, shade];
    }

    private void RecordEmissive(ref RenderGraphFrame graph, bool cameraCut)
    {
        if (!Enabled) return;
        GiEmissiveLights lights = _emissive!;
        var settings = new GiEmissiveParameters((uint)_width, (uint)_height, _views, _frame,
            _candidateCapacity, (uint)_cluster!.CurrentInstanceCount, _materialCapacity,
            _frame != 0u && !_resetBuffers && !cameraCut ? 1u : 0u);
        lights.Uniforms.Write(ref graph, lights.Settings, in settings);
        lights.Uniforms.Upload(ref graph);
        graph.SetPassEnabled(lights.Initialize, _resetBuffers || cameraCut || _frame == 0u);
        graph.SetPassData(lights.Begin, new DispatchArguments((Math.Max(_materialCapacity, 3u) + 63u) / 64u, 1, 1));
        graph.SetPassData(lights.Count, new DispatchArguments(Math.Max(1u, settings.InstanceCount), 1, 1));
        graph.SetPassData(lights.Collect, new DispatchArguments(Math.Max(1u, settings.InstanceCount), 1, 1));
        graph.SetPassData(lights.ClearDirectWork, new DispatchArguments((_materialCapacity + 63u) / 64u, 1, 1));
    }

    private bool TryResolveEmissiveMaterial(string name, ResourceBindingType type, out GraphResourceBinder binder)
    {
        binder = name switch
        {
            "GiEmissive" => BindEmissiveSettings,
            "GiPreviousDirectLight" => BindPreviousDirectLight,
            "GiPreviousDirectSurfaces" => BindPreviousDirectSurfaces,
            _ => null!,
        };
        return binder is not null;
    }

    private void BindEmissiveSettings(ref RenderGraphEdit edit, ref PassDefinition pass, PipelineSync sync)
    {
        if (Enabled) pass.Bind(_emissive!.Settings, sync);
        else pass.Bind(GraphParameterResourceBinding.Null(ResourceBindingType.ConstantBuffer));
    }

    private void BindPreviousDirectLight(ref RenderGraphEdit edit, ref PassDefinition pass, PipelineSync sync)
    {
        if (Enabled)
        {
            edit.OrderAfter(pass.Id, _emissive!.Initialize);
            pass.Bind(edit.CreateTextureSrv(_emissive.History, format: Format.R16G16B16A16Float,
                dimension: TextureViewDimension.Texture2DArray), sync);
        }
        else pass.Bind(GraphParameterResourceBinding.Null(ResourceBindingType.TextureSrv));
    }

    private void BindPreviousDirectSurfaces(ref RenderGraphEdit edit, ref PassDefinition pass, PipelineSync sync)
    {
        if (Enabled) edit.OrderAfter(pass.Id, _emissive!.Initialize);
        BindBuffer(ref edit, ref pass, "GiDirectSurfaces", ResourceBindingType.BufferSrv, sync);
    }

    private bool BindEmissiveResource(ref RenderGraphEdit edit, ref PassDefinition pass,
        string name, ResourceBindingType type, PipelineSync sync = PipelineSync.ComputeShading)
    {
        GiEmissiveLights lights = _emissive!;
        if (name == "GiEmissive") { pass.Bind(lights.Settings, sync); return true; }
        GraphTextureId texture;
        GraphAccessMode access = GraphAccessMode.Write;
        WriteCoverage coverage = WriteCoverage.Complete;
        switch (name)
        {
            case "GiDirectRaw": texture = lights.Raw; break;
            case "GiDirectRawValues": texture = lights.Raw; access = GraphAccessMode.Read; break;
            case "GiDirectFiltered": texture = lights.Filtered; break;
            case "GiDirectFilteredValues": texture = lights.Filtered; access = GraphAccessMode.Read; break;
            case "GiInitialDirectHistory": texture = lights.History; break;
            case "GiDirectHistory": texture = lights.History; access = GraphAccessMode.ReadWrite; coverage = WriteCoverage.Partial; break;
            default: return false;
        }
        if (access == GraphAccessMode.Read)
            pass.Bind(edit.CreateTextureSrv(texture, format: Format.R16G16B16A16Float,
                dimension: TextureViewDimension.Texture2DArray), sync);
        else
            pass.Bind(edit.CreateTextureUav(texture, format: Format.R16G16B16A16Float,
                dimension: TextureViewDimension.Texture2DArray), sync, access, coverage);
        return true;
    }

    private sealed class GiEmissiveLights(GraphBufferInput settings, FeatureUniformBuffer uniforms,
        GraphTextureId raw, GraphTextureId filtered, GraphTextureId history)
    {
        internal GraphBufferInput Settings { get; } = settings;
        internal FeatureUniformBuffer Uniforms { get; } = uniforms;
        internal GraphTextureId Raw { get; } = raw;
        internal GraphTextureId Filtered { get; } = filtered;
        internal GraphTextureId History { get; } = history;
        internal uint InstanceCapacity = 1, MaterialCapacity = 1;
        internal GraphPassId Initialize, Begin, Count, Collect, GeometryWork, Distribution, ClearDirectWork, DirectWork, Resolve;
    }

    private readonly record struct GiEmissiveParameters(uint Width, uint Height, uint ViewCount, uint Frame,
        uint EmitterCapacity, uint InstanceCount, uint MaterialCount, uint UseHistory);
}
