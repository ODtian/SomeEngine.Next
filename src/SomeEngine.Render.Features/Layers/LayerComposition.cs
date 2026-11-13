using System.Runtime.InteropServices;
using SomeEngine.Graphics;
using SomeEngine.Render.Assets;
using SomeEngine.Render.Systems;
using SomeEngine.RenderGraph;
using SomeEngine.Resources;
using SomeEngine.Resources.Layers;

namespace SomeEngine.Render.Features.Layers;

/// <summary>Stores visible fragments and composes the registered passes in their declared order.</summary>
public sealed class LayerComposition : IDisposable, IGraphReadbackReceiver
{
    private const BufferUsages WorkUsages = BufferUsages.ShaderRead | BufferUsages.ShaderWrite | BufferUsages.CopySource;
    private readonly global::SomeEngine.RenderGraph.RenderGraph _graph;
    private readonly RenderPipelines _pipelines;
    private readonly RenderPrepareSystems _prepareSystems;
    private readonly int _prepareSystemIndex;
    private LinkedComputePipeline _clear, _initialize, _composite;
    private readonly GraphData _data;
    private readonly uint _width, _height, _views, _tileCountX, _tileCountY, _tileCount;
    private LayerPassDesc[] _descriptions = [];
    private PassGraph[] _passes = [];
    private uint _nextPass = 1;
    private uint _feedbackCapacity, _feedbackBlocks;
    private GraphRegionId _globalShadingRegion;
    private LayerStagePasses _globalShading;
    private GraphRegionId _globalVisibilityRegion;
    private LayerStagePasses _globalVisibility;
    private StageViews _globalVisibilityViews, _globalShadingViews;
    private readonly uint _maxTessellationFactor;
    private readonly float _targetEdgePixels;
    private RenderGeometryResources? _geometry;
    private readonly LayerViewEntities? _layerViews;
    private readonly TileListReadback _tileListReadback;
    private Shader? _listScanShader;
    private uint _requiredTileIndices;

    public LayerComposition(RenderPipelines pipelines, global::SomeEngine.RenderGraph.RenderGraph graph,
        RenderPrepareSystems prepareSystems, uint width, uint height, uint views,
        Shader storeShader, Shader compositeShader, uint tileSize = 16, uint blockSize = 128,
        uint blockCapacity = 1024, GraphRegionId region = default,
        uint maxTessellationFactor = 64, float targetEdgePixels = 8f,
        RenderWorld? world = null, GraphRegionId visibilityRegion = default,
        GraphRegionId shadingRegion = default, GraphRegionId compositeRegion = default,
        uint tileListCapacity = 1_048_576, Format opaqueDepthFormat = Format.R32Float)
    {
        _graph = graph;
        _pipelines = pipelines;
        _prepareSystems = prepareSystems;
        _tileListReadback = new(this);
        (_maxTessellationFactor, _targetEdgePixels) = (maxTessellationFactor, targetEdgePixels);
        (_width, _height, _views, TileSize, BlockSize) = (width, height, views, tileSize, blockSize);
        if (tileSize is not (16 or 32)) throw new ArgumentOutOfRangeException(nameof(tileSize));
        if (blockSize == 0) throw new ArgumentOutOfRangeException(nameof(blockSize));
        if (blockCapacity == 0) throw new ArgumentOutOfRangeException(nameof(blockCapacity));
        _tileCountX = (width + tileSize - 1u) / tileSize;
        _tileCountY = (height + tileSize - 1u) / tileSize;
        _tileCount = _tileCountX * _tileCountY * views;
        _clear = LinkedComputePipeline.Create(pipelines, storeShader, "LayerClearFragmentStore", "Clear layer fragment store");
        _initialize = LinkedComputePipeline.Create(pipelines, compositeShader, "LayerInitializeAccumulators", "Initialize layer accumulators");
        _composite = LinkedComputePipeline.Create(pipelines, compositeShader, "LayerComposite", "Composite stored layer fragments");
        RenderGraphEdit edit = graph.BeginEdit();
        try
        {
            GraphData data = CreateResources(ref edit, blockCapacity, region, visibilityRegion, shadingRegion, compositeRegion,
                opaqueDepthFormat);
            StageViews visibilityViews = CreateStageViews(ref edit, data);
            StageViews shadingViews = CreateStageViews(ref edit, data);
            data.VisibilityBegin = AddCompute(ref edit, _clear, "Clear layer fragment store", data.Visibility,
                FlatDispatch(Math.Max(_tileCount, 8u), 256), (name, type) => name switch
                {
                    "LayerFragmentStore" => GraphParameterResourceBinding.ConstantBuffer(data.StoreParametersView, PipelineSync.ComputeShading),
                    "LayerTileHeads" => GraphParameterResourceBinding.WritableBuffer(data.Fragments.TileHeadsUav,
                        PipelineSync.ComputeShading, GraphAccessMode.Write, WriteCoverage.Complete),
                    "LayerFrameCounters" => GraphParameterResourceBinding.WritableBuffer(data.Fragments.FrameCountersUav,
                        PipelineSync.ComputeShading, GraphAccessMode.Write, WriteCoverage.Complete),
                    _ => null,
                });
            // Clearing the chain heads establishes an empty set of valid fragment ranges.
            PassDefinition clear = edit.GetPass(data.VisibilityBegin);
            foreach (GraphBufferId buffer in new[] { data.Fragments.Blocks, data.Fragments.Payloads })
                clear.Write(buffer, BufferRange.Whole, PipelineSync.ComputeShading, ResourceAccess.UnorderedAccess,
                    WriteCoverage.Partial, ResourceContentState.Defined, contentScope: BufferContentScope.CountBoundedRanges);
            data.VisibilityComplete = AddFence(ref edit, "Layer visibility complete", data.Visibility);
            edit.OrderAfter(data.VisibilityComplete, data.VisibilityBegin);
            GraphRegionId globalVisibility = edit.AddRegion("Layer shared geometry", data.Visibility);
            data.ShadingBegin = AddFence(ref edit, "Layer shading begin", data.Shading);
            edit.OrderAfter(data.ShadingBegin, data.VisibilityComplete);
            data.ShadingComplete = AddFence(ref edit, "Layer shading complete", data.Shading);
            edit.OrderAfter(data.ShadingComplete, data.ShadingBegin);
            GraphRegionId globalShading = edit.AddRegion("Layer shared material shading", data.Shading);
            data.Initialize = AddCompute(ref edit, _initialize, "Initialize layer accumulators", data.Composite,
                new((width + 7u) / 8u, (height + 7u) / 8u, views),
                (name, type) => ResolveComposite(data, data.InitialParametersView, name, type, GraphAccessMode.Write));
            edit.OrderAfter(data.Initialize, data.ShadingComplete);
            data.Readback = edit.AddReadback(data.Fragments.FrameCounters, new(0, 32), this,
                "Layer fragment measurements", data.Composite);
            edit.OrderAfter(data.Readback, data.Initialize);
            edit.Commit();
            _data = data;
            BlockCapacity = blockCapacity;
            TileListCapacity = tileListCapacity;
            _globalShadingRegion = globalShading;
            _globalVisibilityRegion = globalVisibility;
            _globalVisibilityViews = visibilityViews;
            _globalShadingViews = shadingViews;
        }
        finally { edit.Dispose(); }
        Stencil = new LayerStencil(graph, pipelines, _data.Stencil, width, height, views);
        _layerViews = world is null ? null : new LayerViewEntities(world);
        _prepareSystemIndex = prepareSystems.Add(new LayerCompositionPrepareSystem(this));
    }

    public uint TileSize { get; }
    public uint BlockSize { get; }
    public uint TileCount => _tileCount;
    public uint BlockCapacity { get; private set; }
    public uint TileListCapacity { get; private set; }
    public uint FragmentCapacity => checked(BlockCapacity * BlockSize);
    public uint HardwareWindowTiles { get; set; } = 64;
    public Device Device => _pipelines.Device;
    public LayerCompositionMetrics Metrics { get; private set; }
    public ReadOnlySpan<LayerPassDesc> Passes => _descriptions;
    public LayerStencil Stencil { get; }
    public GraphTextureId Color => _data.Color;
    public GraphTextureId Depth => _data.Depth;
    public GraphTextureId WorkingOpaqueDepth => _data.WorkingDepth;
    public LayerFragmentBuffers Fragments => _data.Fragments;
    public GraphRegionId VisibilityRegion => _data.Visibility;
    public GraphRegionId ShadingRegion => _data.Shading;
    public GraphRegionId CompositeRegion => _data.Composite;
    public GraphPassId VisibilityBegin => _data.VisibilityBegin;
    public GraphPassId VisibilityComplete => _data.VisibilityComplete;
    public GraphPassId ShadingBegin => _data.ShadingBegin;
    public GraphPassId ShadingComplete => _data.ShadingComplete;
    public GraphPassId CompositeBegin => _data.Initialize;
    public GraphPassId CompletePass => _passes.Length == 0 ? _data.Initialize : _passes[^1].Composite;
    public LayerShadingResources ShadingBins => _globalShading.ShadingBins;
    public LayerMaterialShadingResources MaterialShading => _globalShading.MaterialShading;
    public LayerTileBinningResources TileBinning => _globalVisibility.TileBinning;
    public LayerSoftwareRasterResources SoftwareRaster => _globalVisibility.SoftwareRaster;
    public LayerHardwareBinningResources HardwareBinning => _globalVisibility.HardwareBinning;
    public LayerHardwareRasterResources HardwareRaster => _globalVisibility.HardwareRaster;
    public RenderTessellationRasterRegistration TessellationRaster => RegisterTessellationRaster;

    private GraphPassId RegisterTessellationRaster(ref RenderGraphEdit edit, in RenderTessellationRasterData data,
        Material material, IndirectCommandLayout drawLayout, RenderRasterResourceBinder bindResource)
    {
        var resources = new LayerStageResources(_data.Visibility, _data.Shading,
            _data.VisibilityBegin, _data.ShadingBegin, _data.StoreParameters, _data.StoreParametersView,
            _data.InitialParameters, _data.InitialParametersView, _data.Fragments, _data.OpaqueDepthView,
            _data.RasterMaterialsView);
        GraphPassId pass = LayerHardwareRasterPasses.AddTessellation(ref edit, _pipelines, in resources,
            in data, material, drawLayout, bindResource);
        edit.OrderAfter(pass, _data.VisibilityBegin);
        edit.OrderAfter(_data.VisibilityComplete, pass);
        return pass;
    }
    public GraphBufferInput RasterMaterialsInput => _data.RasterMaterials;

    public void ReplaceShader(Shader previous, Shader next)
    {
        LinkedComputePipeline clear = _pipelines.CreateComputeWithShader(_clear.Pipeline, previous, next) ?? _clear;
        LinkedComputePipeline initialize = _pipelines.CreateComputeWithShader(_initialize.Pipeline, previous, next) ?? _initialize;
        LinkedComputePipeline composite = _pipelines.CreateComputeWithShader(_composite.Pipeline, previous, next) ?? _composite;
        if (ReferenceEquals(clear, _clear) && ReferenceEquals(initialize, _initialize) && ReferenceEquals(composite, _composite)) return;
        RenderGraphEdit edit = _graph.BeginEdit();
        try
        {
            if (!ReferenceEquals(clear, _clear))
                BindCompute(ref edit, _data.VisibilityBegin, clear, (name, _) => name switch
                {
                    "LayerFragmentStore" => GraphParameterResourceBinding.ConstantBuffer(_data.StoreParametersView, PipelineSync.ComputeShading),
                    "LayerTileHeads" => GraphParameterResourceBinding.WritableBuffer(_data.Fragments.TileHeadsUav,
                        PipelineSync.ComputeShading, GraphAccessMode.Write, WriteCoverage.Complete),
                    "LayerFrameCounters" => GraphParameterResourceBinding.WritableBuffer(_data.Fragments.FrameCountersUav,
                        PipelineSync.ComputeShading, GraphAccessMode.Write, WriteCoverage.Complete),
                    _ => null,
                });
            if (!ReferenceEquals(initialize, _initialize))
                BindCompute(ref edit, _data.Initialize, initialize, (name, type) =>
                    ResolveComposite(_data, _data.InitialParametersView, name, type, GraphAccessMode.Write));
            if (!ReferenceEquals(composite, _composite))
                foreach (PassGraph pass in _passes)
                    BindCompute(ref edit, pass.Composite, composite, (name, type) =>
                        ResolveComposite(_data, pass.ParametersView, name, type, GraphAccessMode.ReadWrite, pass.CompositeViews));
            edit.Commit();
            (_clear, _initialize, _composite) = (clear, initialize, composite);
        }
        finally { edit.Dispose(); }
    }

    public LayerPassId AddPass(in LayerPassDesc description)
    {
        RenderGraphEdit edit = _graph.BeginEdit();
        try
        {
            if (_passes.Length == 64) throw new ArgumentOutOfRangeException(nameof(description));
            LayerPassId id = new(_nextPass);
            GraphBufferId parameters = CreateUniformBuffer(ref edit, 64, description.Name + " composition parameters");
            GraphBufferCbvId view = edit.CreateBufferCbv(parameters);
            CompositeViews compositeViews = CreateCompositeViews(ref edit, _data, _geometry);
            GraphPassId composite = AddCompute(ref edit, _composite, description.Name + " composition", _data.Composite,
                FlatDispatch(_tileCount, 1), (name, type) => ResolveComposite(_data, view, name, type, GraphAccessMode.ReadWrite, compositeViews));
            edit.OrderAfter(composite, CompletePass);
            edit.OrderAfter(_data.Readback, composite);
            var added = new PassGraph(id, parameters, view, composite, compositeViews);
            PassGraph[] passes = [.. _passes, added];
            LayerPassDesc[] descriptions = [.. _descriptions, description];
            edit.Commit();
            _passes = passes;
            _descriptions = descriptions;
            _nextPass++;
            return id;
        }
        finally { edit.Dispose(); }
    }

    public void RemovePass(LayerPassId pass)
    {
        int index = FindPass(pass);
        PassGraph removed = _passes[index];
        RenderGraphEdit edit = _graph.BeginEdit();
        try
        {
            edit.Remove(removed.Composite);
            RemoveViews(ref edit, removed.CompositeViews);
            edit.Remove(removed.ParametersView);
            edit.Remove(removed.Parameters);
            if (index + 1 < _passes.Length)
                edit.OrderAfter(_passes[index + 1].Composite, index == 0 ? _data.Initialize : _passes[index - 1].Composite);
            PassGraph[] passes = [.. _passes.AsSpan(0, index), .. _passes.AsSpan(index + 1)];
            LayerPassDesc[] descriptions = [.. _descriptions.AsSpan(0, index), .. _descriptions.AsSpan(index + 1)];
            edit.Commit();
            _passes = passes;
            _descriptions = descriptions;
        }
        finally { edit.Dispose(); }
    }

    public byte GetPassIndex(LayerPassId pass) => (byte)FindPass(pass);

    /// <summary>Registers one classification and material-shading stage for all layer passes.</summary>
    public LayerStagePasses ConfigureShading(RenderGeometryResources geometry, LayerStageRegistration register)
    {
        RenderGraphEdit edit = _graph.BeginEdit();
        try
        {
            edit.Remove(_globalShadingRegion);
            RemoveViews(ref edit, _globalShadingViews);
            if (_globalShading.ShadingBins.Region != default)
                LayerShadingPasses.RemoveResources(ref edit, _globalShading.ShadingBins);
            if (_globalShading.MaterialShading.Region != default)
                LayerShadingPasses.RemoveMaterialResources(ref edit, _globalShading.MaterialShading);
            GraphRegionId region = edit.AddRegion("Layer shared material shading", _data.Shading);
            StageViews views = CreateStageViews(ref edit, _data);
            var resources = new LayerStageResources(default, region, _data.VisibilityBegin, _data.ShadingBegin,
                _data.StoreParameters, _data.StoreParametersView, _data.InitialParameters, _data.InitialParametersView,
                views.Fragments, _data.OpaqueDepthView, views.RasterMaterials, views.Tessellation);
            LayerStagePasses stages = register(ref edit, in resources);
            if (stages.ShadingFirst != default) edit.OrderAfter(stages.ShadingFirst, _data.ShadingBegin);
            if (stages.ShadingComplete != default) edit.OrderAfter(_data.ShadingComplete, stages.ShadingComplete);
            var passes = new PassGraph[_passes.Length];
            GraphPassId previous = _data.Initialize;
            for (int index = 0; index < _passes.Length; index++)
            {
                PassGraph source = _passes[index];
                edit.Remove(source.Composite);
                RemoveViews(ref edit, source.CompositeViews);
                CompositeViews compositeViews = CreateCompositeViews(ref edit, _data, geometry);
                GraphPassId composite = AddCompute(ref edit, _composite, _descriptions[index].Name + " composition", _data.Composite,
                    FlatDispatch(_tileCount, 1), (name, type) =>
                        ResolveComposite(_data, source.ParametersView, name, type, GraphAccessMode.ReadWrite, compositeViews));
                edit.OrderAfter(composite, previous);
                edit.OrderAfter(_data.Readback, composite);
                passes[index] = source with { Composite = composite, CompositeViews = compositeViews };
                previous = composite;
            }
            edit.Commit();
            _globalShadingRegion = region;
            _globalShading = stages;
            _globalShadingViews = views;
            _passes = passes;
            _geometry = geometry;
            return stages;
        }
        finally { edit.Dispose(); }
    }

    public void RecordShading(ref RenderGraphFrame frame, RenderGeometryResources geometry, IRenderMaterialResources materials)
    {
        LayerShadingPasses.Record(ref frame, _globalShading.ShadingBins, geometry, materials, _tileCount);
        if (_globalShading.MaterialShading.Region != default)
            LayerShadingPasses.RecordMaterials(ref frame, _globalShading.MaterialShading, geometry, materials, _width, _height, _views);
    }

    /// <summary>Registers shared culling, tile binning and visibility work for all layer passes.</summary>
    public LayerStagePasses ConfigureVisibility(LayerStageRegistration register) => ConfigureVisibilityCore(null, register);

    public LayerStagePasses ConfigureVisibility(Shader binningShader, LayerStageRegistration register) =>
        ConfigureVisibilityCore(binningShader, register);

    private LayerStagePasses ConfigureVisibilityCore(Shader? binningShader, LayerStageRegistration register)
    {
        RenderGraphEdit edit = _graph.BeginEdit();
        try
        {
            if (_globalVisibility.TileBinning.Region != default)
                LayerTileBinningPasses.Remove(ref edit, _globalVisibility.TileBinning);
            if (_globalVisibility.HardwareBinning.Region != default)
                LayerHardwareBinningPasses.Remove(ref edit, _globalVisibility.HardwareBinning);
            edit.Remove(_globalVisibilityRegion);
            if (_globalVisibility.SoftwareRaster.Region != default)
                LayerRasterPasses.RemoveResources(ref edit, _globalVisibility.SoftwareRaster);
            if (_globalVisibility.HardwareRaster.Region != default)
                LayerHardwareRasterPasses.RemoveResources(ref edit, _globalVisibility.HardwareRaster);
            RemoveViews(ref edit, _globalVisibilityViews);
            GraphRegionId region = edit.AddRegion("Layer shared geometry", _data.Visibility);
            StageViews views = CreateStageViews(ref edit, _data);
            var resources = new LayerStageResources(region, default, _data.VisibilityBegin, _data.ShadingBegin,
                _data.StoreParameters, _data.StoreParametersView, _data.InitialParameters, _data.InitialParametersView,
                views.Fragments, _data.OpaqueDepthView, views.RasterMaterials, views.Tessellation)
            {
                ListCapacity = TileListCapacity,
                ListDiagnostics = _tileListReadback,
            };
            LayerStagePasses stages = register(ref edit, in resources);
            if (stages.VisibilityFirst != default) edit.OrderAfter(stages.VisibilityFirst, _data.VisibilityBegin);
            if (stages.VisibilityComplete != default) edit.OrderAfter(_data.VisibilityComplete, stages.VisibilityComplete);
            edit.Commit();
            _globalVisibilityRegion = region;
            _globalVisibility = stages;
            _globalVisibilityViews = views;
            _listScanShader = binningShader;
            return stages;
        }
        finally { edit.Dispose(); }
    }

    public void RecordVisibility(ref RenderGraphFrame frame, RenderGeometryResources geometry)
    {
        if (_globalVisibility.SoftwareRaster.Region != default)
            LayerRasterPasses.RecordSoftware(ref frame, _globalVisibility.SoftwareRaster, geometry);
        if (_globalVisibility.HardwareRaster.Region != default)
        {
            LayerHardwareBinningPasses.Record(ref frame, _globalVisibility.HardwareBinning,
                geometry.VisibleClusterCapacity, _globalVisibility.TileBinning.RasterStageCount);
            LayerHardwareRasterPasses.Record(ref frame, _globalVisibility.HardwareRaster, geometry);
        }
    }

    /// <summary>Builds composition and visibility tables from the existing rendering material addresses.</summary>
    public void Record(ref RenderGraphFrame frame, GraphTextureId opaqueColor, GraphTextureId opaqueDepth,
        IRenderMaterialResources materials)
    {
        int capacity = Math.Max(1, checked((int)materials.MaterialCapacity));
        var composition = new LayerMaterialComposition[capacity];
        var raster = new LayerRasterMaterial[capacity];
        Array.Fill(raster, new LayerRasterMaterial(uint.MaxValue, 0));
        var operations = new LayerOperationData[capacity];
        int count = 0;
        foreach (RenderMaterialEntry material in materials.Materials)
        {
            if (material.Material.Layer is not { } layer) continue;
            int pass = Array.FindIndex(_passes, value => value.Id == layer.Pass);
            if (pass < 0) continue;
            LayerOperation value = layer.Operation;
            operations[count] = new(in value);
            composition[material.Address] = new((uint)count++, layer.StencilReference);
            raster[material.Address] = new((uint)pass,
                layer.DrawOrder | (_descriptions[pass].ClipByOpaqueDepth ? 256u : 0u));
        }
        if (count == 0)
        {
            LayerOperation operation = new();
            operations[0] = new(in operation);
        }
        Record(ref frame, opaqueColor, opaqueDepth,
            frame.Upload<LayerOperationData>(operations.AsSpan(0, Math.Max(1, count)), BufferUsages.ShaderRead, "Layer operations"),
            frame.Upload<LayerMaterialComposition>(composition, BufferUsages.ShaderRead, "Layer material addresses"),
            frame.Upload<LayerRasterMaterial>(raster, BufferUsages.ShaderRead, "Layer raster material addresses"));
    }

    public void Record(ref RenderGraphFrame frame, GraphTextureId opaqueColor, GraphTextureId opaqueDepth,
        scoped ReadOnlySpan<LayerOperation> operations, scoped ReadOnlySpan<LayerMaterialComposition> materials)
    {
        var encoded = new LayerOperationData[Math.Max(1, operations.Length)];
        if (operations.IsEmpty)
        {
            LayerOperation value = new();
            encoded[0] = new(in value);
        }
        else
            for (int index = 0; index < operations.Length; index++) encoded[index] = new(in operations[index]);
        LayerMaterialComposition empty = default;
        ReadOnlySpan<LayerMaterialComposition> materialValues = materials.IsEmpty
            ? MemoryMarshal.CreateReadOnlySpan(ref empty, 1) : materials;
        Record(ref frame, opaqueColor, opaqueDepth,
            frame.Upload<LayerOperationData>(encoded, BufferUsages.ShaderRead, "Layer composition operations"),
            frame.Upload<LayerMaterialComposition>(materialValues, BufferUsages.ShaderRead, "Layer material composition"));
    }

    /// <summary>Uses existing frame uploads from the renderer's material data path.</summary>
    public void Record(ref RenderGraphFrame frame, GraphTextureId opaqueColor, GraphTextureId opaqueDepth,
        GraphBufferSlice operations, GraphBufferSlice materials)
    {
        LayerRasterMaterial opaque = new(uint.MaxValue, 0);
        Record(ref frame, opaqueColor, opaqueDepth, operations, materials,
            frame.Upload(in opaque, BufferUsages.ShaderRead, "Layer default visibility material"));
    }

    public void Record(ref RenderGraphFrame frame, GraphTextureId opaqueColor, GraphTextureId opaqueDepth,
        GraphBufferSlice operations, GraphBufferSlice materials, GraphBufferSlice rasterMaterials)
    {
        frame.SetTextureInput(_data.OpaqueColor, opaqueColor);
        frame.SetTextureInput(_data.OpaqueDepth, opaqueDepth);
        frame.SetBufferInput(_data.Operations, operations);
        frame.SetBufferInput(_data.Materials, materials);
        frame.SetBufferInput(_data.RasterMaterials, rasterMaterials);
        var store = new StoreParameters(_tileCount, BlockSize, BlockCapacity);
        MemoryMarshal.Write(frame.GetBufferDataWriteSpan(_data.StoreParameters), in store);
        var tessellation = new TessellationParameters(_maxTessellationFactor, _targetEdgePixels);
        MemoryMarshal.Write(frame.GetBufferDataWriteSpan(_data.TessellationParameters), in tessellation);
        CompositionParameters initial = Parameters(0, default);
        MemoryMarshal.Write(frame.GetBufferDataWriteSpan(_data.InitialParameters), in initial);
        for (int index = 0; index < _passes.Length; index++)
        {
            CompositionParameters values = Parameters((uint)index, _descriptions[index]);
            MemoryMarshal.Write(frame.GetBufferDataWriteSpan(_passes[index].Parameters), in values);
        }
        frame.SetReadbackTag(_data.Readback, frame.FrameIdentity);
        if (_geometry is { } geometry)
            foreach (PassGraph value in _passes)
            {
                PassDefinition pass = frame.GetPass(value.Composite);
                geometry.ReadPageHeap(ref frame, ref pass, PipelineSync.ComputeShading);
            }
        Stencil.Record(ref frame);
    }

    internal void GrowStorage()
    {
        bool growFragments = _feedbackCapacity == BlockCapacity &&
            (Metrics.FragmentOverflow != 0 || Metrics.BlocksUsed > BlockCapacity * 3u / 4u);
        bool growLists = _requiredTileIndices > TileListCapacity;
        if (!growFragments && !growLists) return;
        uint capacity = BlockCapacity;
        if (growFragments)
        {
            uint missingBlocks = (Metrics.FragmentOverflow + BlockSize - 1u) / BlockSize;
            uint demand = Math.Max(BlockCapacity, Math.Max(_feedbackBlocks, Metrics.BlocksUsed + missingBlocks));
            capacity = checked(demand * 2u);
        }
        uint listCapacity = growLists ? _requiredTileIndices : TileListCapacity;
        LayerTileBinningResources tileBinning = _globalVisibility.TileBinning;
        RenderGraphEdit edit = _graph.BeginEdit();
        try
        {
            if (growFragments)
            {
                edit.SetBufferDescription(_data.Fragments.Blocks, BlockDescription(capacity));
                edit.SetBufferDescription(_data.Fragments.Payloads, PayloadDescription(capacity));
                edit.SetBufferDescription(_data.SortScratch, ScratchDescription(capacity, "Layer sort scratch"));
                edit.SetBufferDescription(_data.MergeScratch, ScratchDescription(capacity, "Layer merge scratch"));
                uint fragments = checked(capacity * BlockSize);
                if (_globalShading.ShadingBins.Region != default)
                    LayerShadingPasses.ResizeWork(ref edit, _globalShading.ShadingBins, fragments);
            }
            if (growLists)
                tileBinning = LayerTileBinningPasses.ResizeLists(ref edit, in tileBinning,
                    _pipelines, _listScanShader!, listCapacity);
            edit.Commit();
            BlockCapacity = capacity;
            TileListCapacity = listCapacity;
            _globalVisibility = _globalVisibility with { TileBinning = tileBinning };
        }
        finally { edit.Dispose(); }
    }

    internal void PrepareViews() => _layerViews?.Update();

    void IGraphReadbackReceiver.OnReadback(ReadOnlySpan<byte> bytes, ulong tag)
    {
        ReadOnlySpan<uint> values = MemoryMarshal.Cast<byte, uint>(bytes);
        Metrics = LayerCompositionMetrics.Read(bytes, tag);
        _feedbackCapacity = values[7];
        _feedbackBlocks = values[1];
    }

    private sealed class TileListReadback(LayerComposition composition) : IGraphReadbackReceiver
    {
        public void OnReadback(ReadOnlySpan<byte> bytes, ulong tag) =>
            composition._requiredTileIndices = MemoryMarshal.Cast<byte, uint>(bytes)[1];
    }

    public void Dispose()
    {
        _prepareSystems.Remove(_prepareSystemIndex);
        _layerViews?.Dispose();
        Stencil.Dispose();
        if (!_graph.IsDisposed && !_graph.IsLost)
        {
            RenderGraphEdit edit = _graph.BeginEdit();
            try
            {
                if (_globalVisibility.TileBinning.Region != default)
                    LayerTileBinningPasses.Remove(ref edit, _globalVisibility.TileBinning);
                if (_globalVisibility.HardwareBinning.Region != default)
                    LayerHardwareBinningPasses.Remove(ref edit, _globalVisibility.HardwareBinning);
                edit.Remove(_data.Visibility);
                if (_globalVisibility.SoftwareRaster.Region != default)
                    LayerRasterPasses.RemoveResources(ref edit, _globalVisibility.SoftwareRaster);
                if (_globalVisibility.HardwareRaster.Region != default)
                    LayerHardwareRasterPasses.RemoveResources(ref edit, _globalVisibility.HardwareRaster);
                edit.Remove(_data.Shading);
                edit.Remove(_data.Composite);
                RemoveViews(ref edit, _globalVisibilityViews);
                RemoveViews(ref edit, _globalShadingViews);
                if (_globalShading.ShadingBins.Region != default)
                    LayerShadingPasses.RemoveResources(ref edit, _globalShading.ShadingBins);
                if (_globalShading.MaterialShading.Region != default)
                    LayerShadingPasses.RemoveMaterialResources(ref edit, _globalShading.MaterialShading);
                foreach (PassGraph pass in _passes)
                {
                    edit.Remove(pass.ParametersView);
                    edit.Remove(pass.Parameters);
                    RemoveViews(ref edit, pass.CompositeViews);
                }
                foreach (GraphBufferCbvId view in _data.Cbvs) edit.Remove(view);
                foreach (GraphBufferSrvId view in _data.Srvs) edit.Remove(view);
                foreach (GraphBufferUavId view in _data.Uavs) edit.Remove(view);
                foreach (GraphTextureSrvId view in _data.TextureSrvs) edit.Remove(view);
                foreach (GraphTextureUavId view in _data.TextureUavs) edit.Remove(view);
                foreach (GraphBufferId buffer in _data.Buffers) edit.Remove(buffer);
                foreach (GraphTextureId texture in _data.Textures) edit.Remove(texture);
                edit.Commit();
            }
            finally { edit.Dispose(); }
        }
        _passes = [];
        _descriptions = [];
        _data.Buffers.Clear(); _data.Cbvs.Clear(); _data.Srvs.Clear(); _data.Uavs.Clear();
        _data.Textures.Clear(); _data.TextureSrvs.Clear(); _data.TextureUavs.Clear();
    }

    private int FindPass(LayerPassId pass)
    {
        for (int index = 0; index < _passes.Length; index++) if (_passes[index].Id == pass) return index;
        throw new ArgumentOutOfRangeException(nameof(pass));
    }

    private CompositionParameters Parameters(uint passIndex, in LayerPassDesc description) => new(
        _width, _height, _views, TileSize, _tileCountX, _tileCountY, _tileCount, BlockSize,
        BlockCapacity, FragmentCapacity, (uint)_passes.Length, passIndex, (uint)description.SortMode,
        description.ClipByOpaqueDepth ? 1u : 0u, (uint)description.Accumulator, HardwareWindowTiles);

    private GraphData CreateResources(ref RenderGraphEdit edit, uint capacity, GraphRegionId region,
        GraphRegionId visibilityRegion, GraphRegionId shadingRegion, GraphRegionId compositeRegion, Format opaqueDepthFormat)
    {
        var data = new GraphData
        {
            Visibility = edit.AddRegion("Layer visibility", visibilityRegion == default ? region : visibilityRegion),
            Shading = edit.AddRegion("Layer shading", shadingRegion == default ? region : shadingRegion),
            Composite = edit.AddRegion("Layer composition", compositeRegion == default ? region : compositeRegion),
            OpaqueColor = edit.CreateTextureInput(),
            OpaqueDepth = edit.CreateTextureInput(),
            Operations = edit.CreateBufferInput(BufferUsages.ShaderRead),
            Materials = edit.CreateBufferInput(BufferUsages.ShaderRead),
            RasterMaterials = edit.CreateBufferInput(BufferUsages.ShaderRead),
        };
        data.StoreParameters = CreateUniformBuffer(ref edit, 16, "Layer fragment store parameters");
        data.InitialParameters = CreateUniformBuffer(ref edit, 64, "Layer initial composition parameters");
        data.TessellationParameters = CreateUniformBuffer(ref edit, 16, "Layer tessellation parameters");
        data.Buffers.Add(data.StoreParameters);
        data.Buffers.Add(data.InitialParameters);
        data.Buffers.Add(data.TessellationParameters);
        data.StoreParametersView = edit.CreateBufferCbv(data.StoreParameters);
        data.InitialParametersView = edit.CreateBufferCbv(data.InitialParameters);
        data.Cbvs.Add(data.StoreParametersView);
        data.Cbvs.Add(data.InitialParametersView);
        GraphBufferId blocks = Buffer(ref edit, data, BlockDescription(capacity));
        GraphBufferId payloads = Buffer(ref edit, data, PayloadDescription(capacity));
        GraphBufferId heads = Buffer(ref edit, data, new((ulong)_tileCount * 4u, WorkUsages, "Layer tile heads"));
        GraphBufferId counters = Buffer(ref edit, data, new(32, WorkUsages, "Layer frame counters"));
        data.Fragments = new(blocks, payloads, heads, counters,
            Uav(ref edit, data, blocks, 0), Uav(ref edit, data, payloads, 12),
            Uav(ref edit, data, heads, 4), Uav(ref edit, data, counters, 4),
            Srv(ref edit, data, blocks, 0), Srv(ref edit, data, payloads, 12),
            Srv(ref edit, data, heads, 4), Srv(ref edit, data, counters, 4));
        data.SortScratch = Buffer(ref edit, data, ScratchDescription(capacity, "Layer sort scratch"));
        data.MergeScratch = Buffer(ref edit, data, ScratchDescription(capacity, "Layer merge scratch"));
        data.SortScratchView = Uav(ref edit, data, data.SortScratch, 4);
        data.MergeScratchView = Uav(ref edit, data, data.MergeScratch, 4);
        data.OperationsView = edit.CreateBufferSrv(data.Operations, structureStride: 112);
        data.MaterialsView = edit.CreateBufferSrv(data.Materials, structureStride: 8);
        data.RasterMaterialsView = edit.CreateBufferSrv(data.RasterMaterials, structureStride: 8);
        data.Srvs.Add(data.OperationsView);
        data.Srvs.Add(data.MaterialsView);
        data.Srvs.Add(data.RasterMaterialsView);
        data.OpaqueColorView = edit.CreateTextureSrv(data.OpaqueColor, format: Format.R16G16B16A16Float, dimension: TextureViewDimension.Texture2DArray);
        TextureAspects depthAspect = opaqueDepthFormat is Format.D16UNorm or Format.D32Float
            or Format.D24UNormS8UInt or Format.D32FloatS8UInt ? TextureAspects.Depth : TextureAspects.Color;
        data.OpaqueDepthView = edit.CreateTextureSrv(data.OpaqueDepth, new(0, 1, 0, _views, depthAspect),
            opaqueDepthFormat, TextureViewDimension.Texture2DArray);
        data.TextureSrvs.Add(data.OpaqueColorView);
        data.TextureSrvs.Add(data.OpaqueDepthView);
        data.Color = Texture(ref edit, data, Format.R16G16B16A16Float, "Layer accumulator color");
        data.Depth = Texture(ref edit, data, Format.R32Float, "Layer accumulator depth");
        data.WorkingDepth = Texture(ref edit, data, Format.R32Float, "Layer working opaque depth");
        data.Stencil = Texture(ref edit, data, Format.R32UInt, "Layer stencil");
        data.ColorView = TextureUav(ref edit, data, data.Color, Format.R16G16B16A16Float);
        data.DepthView = TextureUav(ref edit, data, data.Depth, Format.R32Float);
        data.WorkingDepthView = TextureUav(ref edit, data, data.WorkingDepth, Format.R32Float);
        data.StencilView = TextureUav(ref edit, data, data.Stencil, Format.R32UInt);
        return data;
    }

    private static GraphParameterResourceBinding? ResolveComposite(GraphData data, GraphBufferCbvId parameters,
        string name, ResourceBindingType type, GraphAccessMode textureMode, CompositeViews? views = null) => name switch
    {
        "LayerCompositionUniforms" => GraphParameterResourceBinding.ConstantBuffer(parameters, PipelineSync.ComputeShading),
        "LayerOpaqueColor" => GraphParameterResourceBinding.SampledTexture(data.OpaqueColorView, PipelineSync.ComputeShading),
        "LayerOpaqueDepth" => GraphParameterResourceBinding.SampledTexture(data.OpaqueDepthView, PipelineSync.ComputeShading),
        "LayerFragmentBlocks" => Read(views!.Blocks),
        "LayerFragmentPayloads" => Read(views!.Payloads),
        "LayerTileHeads" => Read(views!.Heads),
        "VisibleClusters" => views!.VisibleClusters == default ? GraphParameterResourceBinding.Null(type) : Read(views.VisibleClusters),
        "InstanceData" => views!.InstanceData == default ? GraphParameterResourceBinding.Null(type) : Read(views.InstanceData),
        "PageHeap" => views!.PageHeap == default ? GraphParameterResourceBinding.Null(type) :
            GraphParameterResourceBinding.ReadOnlyBufferDescriptor(views.PageHeap),
        "InstanceProperties" => views!.Properties is null ? GraphParameterResourceBinding.Null(type) :
            GraphParameterResourceBinding.ConstantBuffer(views.Properties, PipelineSync.ComputeShading),
        "MaterialLayers" => Read(views!.MaterialTable),
        "LayerOperations" => Read(views!.Operations),
        "LayerFrameCounters" => GraphParameterResourceBinding.WritableBuffer(views!.Counters,
            PipelineSync.ComputeShading),
        "LayerSortScratch" => Scratch(views!.Sort),
        "LayerMergeScratch" => Scratch(views!.Merge),
        "LayerAccumulatorColor" => WriteTexture(data.ColorView, textureMode),
        "LayerAccumulatorDepth" => WriteTexture(data.DepthView, textureMode),
        "LayerWorkingOpaqueDepth" => WriteTexture(data.WorkingDepthView, textureMode),
        "LayerStencil" => WriteTexture(data.StencilView, textureMode),
        _ => null,
    };

    private static GraphParameterResourceBinding Read(GraphBufferSrvId view) =>
        GraphParameterResourceBinding.ReadOnlyBuffer(view, PipelineSync.ComputeShading, BufferContentScope.CountBoundedRanges);
    private static GraphParameterResourceBinding Scratch(GraphBufferUavId view) =>
        GraphParameterResourceBinding.WritableBuffer(view, PipelineSync.ComputeShading, GraphAccessMode.Write,
            WriteCoverage.Partial, ResourceContentState.Defined, BufferContentScope.CountBoundedRanges);
    private static GraphParameterResourceBinding WriteTexture(GraphTextureUavId view, GraphAccessMode mode) =>
        GraphParameterResourceBinding.StorageTexture(view, PipelineSync.ComputeShading, mode,
            mode == GraphAccessMode.Write ? WriteCoverage.Complete : WriteCoverage.Partial);

    private static StageViews CreateStageViews(ref RenderGraphEdit edit, GraphData data)
    {
        LayerFragmentBuffers buffers = data.Fragments;
        var fragments = new LayerFragmentBuffers(buffers.Blocks, buffers.Payloads,
            buffers.TileHeads, buffers.FrameCounters,
            edit.CreateBufferUav(buffers.Blocks), edit.CreateBufferUav(buffers.Payloads, structureStride: 12),
            edit.CreateBufferUav(buffers.TileHeads, structureStride: 4),
            edit.CreateBufferUav(buffers.FrameCounters, structureStride: 4),
            edit.CreateBufferSrv(buffers.Blocks), edit.CreateBufferSrv(buffers.Payloads, structureStride: 12),
            edit.CreateBufferSrv(buffers.TileHeads, structureStride: 4),
            edit.CreateBufferSrv(buffers.FrameCounters, structureStride: 4));
        return new(fragments, edit.CreateBufferSrv(data.RasterMaterials, structureStride: 8),
            edit.CreateBufferCbv(data.TessellationParameters));
    }

    private static CompositeViews CreateCompositeViews(ref RenderGraphEdit edit, GraphData data,
        RenderGeometryResources? geometry) => new(
        edit.CreateBufferSrv(data.Fragments.Blocks),
        edit.CreateBufferSrv(data.Fragments.Payloads, structureStride: 12),
        edit.CreateBufferSrv(data.Fragments.TileHeads, structureStride: 4),
        edit.CreateBufferSrv(data.Materials, structureStride: 8), edit.CreateBufferSrv(data.Operations, structureStride: 112),
        geometry is null ? default : edit.CreateBufferSrv(geometry.VisibleClusters, structureStride: 16),
        geometry is null ? default : edit.CreateBufferSrv(geometry.InstanceData),
        geometry is null ? default : edit.CreateBufferSrv(geometry.PageHeap), geometry?.InstanceProperties,
        edit.CreateBufferUav(data.Fragments.FrameCounters, structureStride: 4),
        edit.CreateBufferUav(data.SortScratch, structureStride: 4), edit.CreateBufferUav(data.MergeScratch, structureStride: 4));

    private static void RemoveViews(ref RenderGraphEdit edit, StageViews views)
    {
        LayerFragmentBuffers values = views.Fragments;
        foreach (GraphBufferSrvId view in new[] { values.BlocksSrv, values.PayloadsSrv,
            values.TileHeadsSrv, values.FrameCountersSrv, views.RasterMaterials }) edit.Remove(view);
        foreach (GraphBufferUavId view in new[] { values.BlocksUav, values.PayloadsUav,
            values.TileHeadsUav, values.FrameCountersUav }) edit.Remove(view);
        edit.Remove(views.Tessellation);
    }

    private static void RemoveViews(ref RenderGraphEdit edit, CompositeViews views)
    {
        foreach (GraphBufferSrvId view in new[] { views.Blocks, views.Payloads, views.Heads,
            views.MaterialTable, views.Operations, views.VisibleClusters, views.InstanceData, views.PageHeap })
            if (view != default) edit.Remove(view);
        foreach (GraphBufferUavId view in new[] { views.Counters, views.Sort, views.Merge }) edit.Remove(view);
    }

    private GraphBufferId CreateUniformBuffer(ref RenderGraphEdit edit, uint size, string label)
    {
        uint alignment = Math.Max(1u, _pipelines.Device.Capabilities.Limits.ConstantBufferAlignment);
        return edit.CreatePersistentBuffer(new((size + alignment - 1u) / alignment * alignment, BufferUsages.Constant, label), MemoryType.Upload);
    }

    private BufferDesc BlockDescription(uint capacity) => new((ulong)capacity * (12ul + BlockSize * 12ul), WorkUsages, "Layer fragment blocks");
    private BufferDesc PayloadDescription(uint capacity) => new((ulong)capacity * BlockSize * 12ul, WorkUsages, "Layer fragment payloads");
    private BufferDesc ScratchDescription(uint capacity, string label) => new((ulong)capacity * BlockSize * 4ul, WorkUsages, label);

    private static GraphBufferId Buffer(ref RenderGraphEdit edit, GraphData data, in BufferDesc description)
    {
        GraphBufferId buffer = edit.CreatePersistentBuffer(description);
        data.Buffers.Add(buffer);
        return buffer;
    }
    private static GraphBufferSrvId Srv(ref RenderGraphEdit edit, GraphData data, GraphBufferId buffer, uint stride)
    {
        GraphBufferSrvId view = edit.CreateBufferSrv(buffer, structureStride: stride);
        data.Srvs.Add(view);
        return view;
    }
    private static GraphBufferUavId Uav(ref RenderGraphEdit edit, GraphData data, GraphBufferId buffer, uint stride)
    {
        GraphBufferUavId view = edit.CreateBufferUav(buffer, structureStride: stride);
        data.Uavs.Add(view);
        return view;
    }
    private GraphTextureId Texture(ref RenderGraphEdit edit, GraphData data, Format format, string label)
    {
        GraphTextureId texture = edit.CreatePersistentTexture(new(TextureDimension.Texture2D, _width, _height, 1, 1, _views, 1,
            format, TextureUsages.Sampled | TextureUsages.Storage | TextureUsages.CopySource | TextureUsages.CopyDestination, label: label));
        data.Textures.Add(texture);
        return texture;
    }
    private GraphTextureUavId TextureUav(ref RenderGraphEdit edit, GraphData data, GraphTextureId texture, Format format)
    {
        GraphTextureUavId view = edit.CreateTextureUav(texture, new(0, 1, 0, _views, TextureAspects.Color), format, TextureViewDimension.Texture2DArray);
        data.TextureUavs.Add(view);
        return view;
    }
    private static GraphPassId AddFence(ref RenderGraphEdit edit, string label, GraphRegionId region) =>
        edit.AddComputeFramePass(label, PassQueueSelection.AnyCompatible(), 0,
            new PassOptions(Culling: PassCullingMode.NeverCull), static (ref PassDefinition _, ref int _) => { },
            static (ref ComputePassCommandScope _, in int _) => { }, region);
    private static GraphPassId AddCompute(ref RenderGraphEdit edit, LinkedComputePipeline pipeline, string label,
        GraphRegionId region, DispatchArguments dispatch, Func<string, ResourceBindingType, GraphParameterResourceBinding?> resolve)
    {
        GraphPassId id = edit.AddComputeFramePass(label, PassQueueSelection.AnyCompatible(), dispatch,
            new PassOptions(Culling: PassCullingMode.NeverCull, RepeatCommands: true),
            static (ref PassDefinition _, ref DispatchArguments _) => { },
            static (ref ComputePassCommandScope commands, in DispatchArguments data) => commands.Dispatch(data), region);
        BindCompute(ref edit, id, pipeline, resolve);
        return id;
    }

    private static void BindCompute(ref RenderGraphEdit edit, GraphPassId id, LinkedComputePipeline pipeline,
        Func<string, ResourceBindingType, GraphParameterResourceBinding?> resolve)
    {
        GraphParameterResourceBinding[] bindings = ShaderEntryBindings.Resolve(pipeline.Shader, pipeline, resolve);
        edit.SetPassParameterBindings(id, []);
        PassDefinition pass = edit.GetPass(id);
        pass.SetPipeline(pipeline.Pipeline);
        pass.SetParameterBlock(pipeline.ParameterLayout);
        foreach (GraphParameterResourceBinding binding in bindings) pass.Bind(binding);
    }
    private static DispatchArguments FlatDispatch(uint count, uint threads)
    {
        uint groups = (count + threads - 1u) / threads;
        return new(Math.Min(groups, 65535u), Math.Max((groups + 65534u) / 65535u, 1u), 1);
    }

    [StructLayout(LayoutKind.Sequential, Size = 16)]
    private readonly record struct StoreParameters(uint TileCount, uint BlockSize, uint BlockCapacity);
    [StructLayout(LayoutKind.Sequential, Size = 16)]
    private readonly record struct TessellationParameters(uint MaxFactor, float TargetEdgePixels);
    [StructLayout(LayoutKind.Sequential, Size = 64)]
    private readonly record struct CompositionParameters(uint Width, uint Height, uint ViewCount, uint TileSize,
        uint TileCountX, uint TileCountY, uint TileCount, uint BlockSize, uint BlockCapacity, uint FragmentCapacity,
        uint PassCount, uint PassIndex, uint SortMode, uint ClipByOpaqueDepth, uint Accumulator, uint HardwareWindowTiles);
    private readonly record struct PassGraph(LayerPassId Id, GraphBufferId Parameters,
        GraphBufferCbvId ParametersView, GraphPassId Composite, CompositeViews CompositeViews);
    private sealed record StageViews(LayerFragmentBuffers Fragments, GraphBufferSrvId RasterMaterials,
        GraphBufferCbvId Tessellation);
    private sealed record CompositeViews(GraphBufferSrvId Blocks, GraphBufferSrvId Payloads, GraphBufferSrvId Heads,
        GraphBufferSrvId MaterialTable, GraphBufferSrvId Operations,
        GraphBufferSrvId VisibleClusters, GraphBufferSrvId InstanceData, GraphBufferSrvId PageHeap, GraphBufferInput? Properties,
        GraphBufferUavId Counters, GraphBufferUavId Sort, GraphBufferUavId Merge);
    private sealed class GraphData
    {
        internal GraphRegionId Visibility, Shading, Composite;
        internal GraphPassId VisibilityBegin, VisibilityComplete, ShadingBegin, ShadingComplete, Initialize;
        internal GraphReadbackId Readback;
        internal GraphTextureInput OpaqueColor = null!, OpaqueDepth = null!;
        internal GraphBufferInput Operations = null!, Materials = null!, RasterMaterials = null!;
        internal LayerFragmentBuffers Fragments;
        internal GraphBufferId StoreParameters, InitialParameters, TessellationParameters, SortScratch, MergeScratch;
        internal GraphBufferCbvId StoreParametersView, InitialParametersView;
        internal GraphBufferUavId SortScratchView, MergeScratchView;
        internal GraphBufferSrvId OperationsView, MaterialsView, RasterMaterialsView;
        internal GraphTextureId Color, Depth, WorkingDepth, Stencil;
        internal GraphTextureSrvId OpaqueColorView, OpaqueDepthView;
        internal GraphTextureUavId ColorView, DepthView, WorkingDepthView, StencilView;
        internal readonly List<GraphBufferId> Buffers = [];
        internal readonly List<GraphBufferCbvId> Cbvs = [];
        internal readonly List<GraphBufferSrvId> Srvs = [];
        internal readonly List<GraphBufferUavId> Uavs = [];
        internal readonly List<GraphTextureId> Textures = [];
        internal readonly List<GraphTextureSrvId> TextureSrvs = [];
        internal readonly List<GraphTextureUavId> TextureUavs = [];
    }
}
