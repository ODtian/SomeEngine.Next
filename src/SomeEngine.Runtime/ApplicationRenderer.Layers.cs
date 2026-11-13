using SomeEngine.Assets;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Schema;
using SomeEngine.Graphics;
using SomeEngine.Render;
using SomeEngine.Render.Cluster.Pipeline;
using SomeEngine.Render.Features.Layers;
using SomeEngine.RenderGraph;
using SomeEngine.Resources;

namespace SomeEngine.Runtime;

internal sealed partial class ApplicationRenderer
{
    internal LayerComposition layers = null!;
    private Shader _layerTilesShader = null!, _layerClusterShader = null!;
    private Shader _layerShadeReset = null!, _layerShadeCount = null!, _layerShadeReserve = null!, _layerShadeScatter = null!;
    private IndirectCommandLayout _layerDispatchLayout = null!, _layerDrawLayout = null!;
    private IReadOnlyList<RenderMaterialEntry>? _layerMaterials;
    private RenderGeometryResources? _layerGeometry;
    private uint _layerDebugMode;

    private Shader LoadLayerShader(AssetId<ShaderAsset> asset) =>
        ShaderLibrary.GetAsync(_shaderFiles, asset).AsTask().GetAwaiter().GetResult();

    private void InitializeLayers(ClusterShaders configuration, ClusterPipelineOptions geometryOptions)
    {
        Shader store = LoadLayerShader(EngineShaderAssets.LayerFragmentStore);
        Shader composite = LoadLayerShader(EngineShaderAssets.LayerComposite);
        _layerTilesShader = LoadLayerShader(EngineShaderAssets.LayerTileBinning);
        _layerClusterShader = LoadLayerShader(EngineShaderAssets.LayerClusterBinning);
        _layerShadeReset = Operation(ClusterShaderOperationRole.MaterialBinningReset);
        _layerShadeCount = Operation(ClusterShaderOperationRole.MaterialBinningCount);
        _layerShadeReserve = Operation(ClusterShaderOperationRole.MaterialBinningReserve);
        _layerShadeScatter = Operation(ClusterShaderOperationRole.MaterialBinningScatter);
        _layerDispatchLayout = renderDevice.Backend.CreateIndirectCommandLayout(renderDevice.Device,
            new([new(IndirectArgumentType.Dispatch)], 12, label: "Layer material and tile dispatch"));
        _layerDrawLayout = renderDevice.Backend.CreateIndirectCommandLayout(renderDevice.Device,
            new([new(IndirectArgumentType.Draw)], 16, label: "Layer cluster draws"));
        layers = new(pipelines, renderGraph, prepareSystems,
            (uint)outputDescription.Width, (uint)outputDescription.Height, outputDescription.ViewCount,
            store, composite, maxTessellationFactor: geometryOptions.MaxTessellationFactor,
            targetEdgePixels: geometryOptions.TessellationTargetEdgePixels,
            world: renderWorld, visibilityRegion: layerVisibilityRegion,
            shadingRegion: layerShadingRegion, compositeRegion: layerCompositionRegion,
            opaqueDepthFormat: Format.D32Float);
        if (pipeline.VisibilityMode == ClusterVisibilityMode.HardwareOnly)
            layers.HardwareWindowTiles = 0;
        pipeline.ConfigureLayerTiles(layers.TileSize, layers.RasterMaterialsInput, layers.TessellationRaster,
            layers.HardwareWindowTiles);

        Shader Operation(ClusterShaderOperationRole role) =>
            configuration.Operations.Single(operation => operation.Role == role).Shaders[0];
    }

    private void ReplaceLayerShaders(Shader previous, Shader next)
    {
        layers.ReplaceShader(previous, next);
        bool changed = Replace(ref _layerTilesShader, previous, next)
            | Replace(ref _layerClusterShader, previous, next)
            | Replace(ref _layerShadeReset, previous, next)
            | Replace(ref _layerShadeCount, previous, next)
            | Replace(ref _layerShadeReserve, previous, next)
            | Replace(ref _layerShadeScatter, previous, next);
        if (changed) _layerGeometry = null;

        static bool Replace(ref Shader current, Shader previous, Shader next)
        {
            if (!ReferenceEquals(current, previous)) return false;
            current = next;
            return true;
        }
    }

    private void ReconcileLayers()
    {
        pipeline.ConfigureLayerTiles(layers.TileSize, layers.RasterMaterialsInput, layers.TessellationRaster,
            layers.HardwareWindowTiles);
        RenderGeometryResources geometry = pipeline.LayerGeometry;
        if (ReferenceEquals(_layerGeometry, geometry) && ReferenceEquals(_layerMaterials, shading.Materials)
            && _layerDebugMode == shading.DebugMode) return;

        RenderRasterStage[] rasterStages = pipeline.RasterStages.ToArray();
        layers.ConfigureVisibility(_layerTilesShader, (ref RenderGraphEdit edit, in LayerStageResources resources) =>
            RegisterLayerVisibility(ref edit, in resources, geometry, rasterStages));
        layers.ConfigureShading(geometry, (ref RenderGraphEdit edit, in LayerStageResources resources) =>
        {
            LayerShadingResources bins = LayerShadingPasses.Add(ref edit, pipelines, in resources,
                geometry, shading, _layerShadeReset, _layerShadeCount, _layerShadeReserve, _layerShadeScatter,
                layers.TileCount, layers.FragmentCapacity);
            LayerMaterialShadingResources materials = LayerShadingPasses.AddMaterials(ref edit, pipelines,
                in resources, in bins, geometry, shading, _layerDispatchLayout);
            return new(default, default, bins.ResetPass, materials.CompletePass)
            {
                ShadingBins = bins,
                MaterialShading = materials,
            };
        });

        RenderGraphEdit edit = renderGraph.BeginEdit();
        try
        {
            edit.OrderAfter(virtualShadows.OpaqueRequestPass, layers.VisibilityComplete);
            foreach (GraphPassId pass in shading.MaterialPasses)
                edit.OrderAfter(layers.ShadingBegin, pass);
            colorFeature?.OrderAfter(ref edit, layers.CompletePass);
            edit.Commit();
        }
        finally { edit.Dispose(); }
        _layerGeometry = geometry;
        _layerMaterials = shading.Materials;
        _layerDebugMode = shading.DebugMode;
    }

    private LayerStagePasses RegisterLayerVisibility(ref RenderGraphEdit edit, in LayerStageResources resources,
        RenderGeometryResources geometry, RenderRasterStage[] rasterStages)
    {
        GraphBufferSrvId visible = edit.CreateBufferSrv(geometry.VisibleClusters, structureStride: 16);
        GraphBufferSrvId windows = edit.CreateBufferSrv(pipeline.LayerTileWindows, structureStride: 16);
        GraphBufferSrvId views = edit.CreateBufferSrv(geometry.PackedViews, structureStride: geometry.PackedViewStride);
        GraphBufferSrvId first = edit.CreateBufferSrv(geometry.PhaseOneCounts);
        GraphBufferSrvId second = edit.CreateBufferSrv(geometry.PhaseTwoCounts);
        GraphBufferSrvId stages = edit.CreateBufferSrv(pipeline.RasterStageIndices, structureStride: 4);
        GraphBufferSrvId instances = edit.CreateBufferSrv(geometry.InstanceData);
        GraphBufferSrvId pages = edit.CreateBufferSrv(geometry.PageHeap);
        LayerStageResources inputs = resources with { RasterStageIndicesView = stages };
        GraphParameterResourceBinding? Resolve(string name, ResourceBindingType type) => name switch
        {
            "LayerVisibleClusters" => Read(visible, BufferContentScope.CountBoundedRanges),
            "LayerClusterTileWindows" => Read(windows, BufferContentScope.CountBoundedRanges),
            "LayerPackedViews" => Read(views),
            "LayerFirstDraw" => Read(first),
            "LayerSecondDraw" => Read(second),
            "LayerMaterialRasterStages" => Read(stages),
            "InstanceData" => Read(instances),
            "InstanceProperties" => GraphParameterResourceBinding.ConstantBuffer(geometry.InstanceProperties, PipelineSync.ComputeShading),
            "PageHeap" => GraphParameterResourceBinding.ReadOnlyBufferDescriptor(pages),
            _ => inputs.ResolveVisibility(name, type),
        };
        uint stageCount = rasterStages.Aggregate(1u, (count, stage) => Math.Max(count, stage.Index + 1u));
        LayerTileBinningResources tiles = LayerTileBinningPasses.Add(ref edit, pipelines,
            _layerTilesShader, _layerTilesShader, _layerClusterShader, _layerDispatchLayout,
            geometry.VisibleClusterCapacity, layers.TileCount, stageCount, resources.ListCapacity,
            Resolve, [visible, windows, views, first, second, stages, instances],
            resources.ListDiagnostics, resources.VisibilityRegion) with
        {
            InputViews = [visible, windows, views, first, second, stages, instances, pages],
        };
        edit.OrderAfter(tiles.Binning.CountPass, geometry.CompletePass);
        LayerHardwareBinningResources hardware = LayerHardwareBinningPasses.Add(ref edit, pipelines,
            _layerTilesShader, _layerClusterShader, geometry.VisibleClusterCapacity, stageCount,
            Resolve, geometry.CompletePass, resources.VisibilityRegion);
        LayerRasterStage[] softwareStages = rasterStages.Select(stage => new LayerRasterStage(stage.Index, stage.Material)).ToArray();
        LayerRasterStage[] hardwareStages = rasterStages.Where(stage => !stage.Tessellated)
            .Select(stage => new LayerRasterStage(stage.Index, stage.Material)).ToArray();
        LayerSoftwareRasterResources software = LayerRasterPasses.AddSoftware(ref edit, pipelines,
            in inputs, in tiles, geometry, shading, softwareStages, _layerDispatchLayout, stageResources);
        LayerHardwareRasterResources raster = LayerHardwareRasterPasses.Add(ref edit, pipelines,
            in inputs, in hardware, geometry, shading, hardwareStages, _layerDrawLayout,
            (uint)outputDescription.Width, (uint)outputDescription.Height, stageResources);
        GraphPassId complete = edit.AddComputeFramePass("Layer software and hardware complete", PassQueueSelection.AnyCompatible(), 0,
            new PassOptions(Culling: PassCullingMode.NeverCull), static (ref PassDefinition _, ref int _) => { },
            static (ref ComputePassCommandScope _, in int _) => { }, resources.VisibilityRegion);
        edit.OrderAfter(complete, software.CompletePass);
        edit.OrderAfter(complete, raster.CompletePass);
        return new(tiles.Binning.CountPass, complete, default, default)
        {
            TileBinning = tiles,
            SoftwareRaster = software,
            HardwareBinning = hardware,
            HardwareRaster = raster,
        };

        static GraphParameterResourceBinding Read(GraphBufferSrvId view, BufferContentScope scope = BufferContentScope.FullRange) =>
            GraphParameterResourceBinding.ReadOnlyBuffer(view, PipelineSync.ComputeShading, scope);
    }

    private void RecordLayers(ref RenderGraphFrame frame, in ClusterPipelineOutputs output)
    {
        layers.Record(ref frame, output.MaterialColor, output.Depth, shading);
        LayerTileBinningResources tiles = layers.TileBinning;
        LayerTileBinningPasses.SetFrameParameters(ref frame, in tiles, _layerGeometry!.VisibleClusterCapacity,
            _layerGeometry.VisibleClusterCapacity, layers.TileCount, tiles.RasterStageCount,
            layers.TileListCapacity);
        ReadOnlySpan<GraphPassId> reads = [tiles.Binning.CountPass, tiles.Binning.ExpandPass, tiles.Binning.CompletePass,
            tiles.MarkPass, layers.HardwareBinning.CountPass, layers.HardwareBinning.CompletePass];
        foreach (GraphPassId id in reads)
        {
            PassDefinition pass = frame.GetPass(id);
            _layerGeometry.ReadPageHeap(ref frame, ref pass, PipelineSync.ComputeShading);
        }
        layers.RecordVisibility(ref frame, _layerGeometry);
        layers.RecordShading(ref frame, _layerGeometry, shading);
    }
}
