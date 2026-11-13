using SomeEngine.Graphics;
using SomeEngine.RenderGraph;

namespace SomeEngine.Render.Cluster.Pipeline;

public sealed partial class ClusterGraphInstaller
{
    internal void RecordLayerTessellationHardware(ref RenderGraphEdit graph, in FrameResources frame,
        ClusterRasterDispatch stage, GraphRegionId region)
    {
        GraphBufferSlice uniforms = UploadUniform(ref graph,
            CreateTessellationUniforms(frame, stage.BinIndex, false, 0u), "Layer tessellation raster parameters");
        var data = new RenderTessellationRasterData(stage.BinIndex, frame.TessellationHardwareDrawArgs,
            (ulong)stage.BinIndex * 8u * ClusterIndirectLayout.DrawIndexedBytes,
            frame.TessellationPatternIndices, frame.TessellationWorkPatches,
            (uint)frame.Width, (uint)frame.Height, region);
        var materialResources = new ClusterShadingPipeline.RasterMaterialResources(_geometry, _shading._stageResourceProvider);
        void BindResource(ref RenderGraphEdit edit, ref PassDefinition pass,
            in ShaderParameterResource resource, PipelineSync sync)
        {
            FrameResources layers = GetRasterFrameResources(GraphData.Resources, false, true);
            if (resource.Name is "LayerRasterViews" or "LayerVisibleClusters" or "LayerMaterialRasterStages")
            {
                GraphBufferId buffer = resource.Name switch
                {
                    "LayerRasterViews" => layers.PackedViews,
                    "LayerVisibleClusters" => layers.VisibleClusters,
                    _ => layers.RasterBinIndices,
                };
                uint stride = resource.Name == "LayerRasterViews" ? checked((uint)ClusterViewUniforms.SizeInBytes) :
                    checked((uint)(resource.Name == "LayerVisibleClusters" ? VisibleClusterStride : sizeof(uint)));
                pass.Bind(GraphParameterResourceBinding.ReadOnlyBuffer(edit.CreateBufferSrv(buffer, structureStride: stride), sync,
                    resource.Name == "LayerVisibleClusters" ? BufferContentScope.CountBoundedRanges : BufferContentScope.FullRange));
                return;
            }
            ClusterTessellationCoreResource core = ClusterTessellationCoreResources.Resolve(resource.Name, resource.Type);
            if (core != ClusterTessellationCoreResource.None)
                _shading.BindTessellationCoreResource(ref edit, ref pass, core, uniforms,
                    false, 0u, sync, false, true);
            else if (_shading.BindNeutralRasterTargetResource(ref edit, ref pass, in resource))
                return;
            else if (materialResources.TryResolve(resource.Name, resource.Type, out GraphResourceBinder binder))
                binder(ref edit, ref pass, sync);
            else
            {
                ClusterRasterStageResources<ClusterTessellationCoreResource> inputs = stage.Tessellation!.SeedResources;
                int index = FindResource(inputs, resource);
                if (index < 0)
                {
                    inputs = stage.Tessellation.SplitResources;
                    index = FindResource(inputs, resource);
                }
                if (index >= 0 && inputs.FeatureBinders[index] is { } feature) feature(ref edit, ref pass, sync);
                else
                {
                    string name = resource.Name;
                    var property = stage.Material.Properties.First(value => value.Name == name);
                    var binding = new ClusterMaterialStageBinding(property.Index, resource.Type, true);
                    _shading.BindStageMaterialResource(ref edit, ref pass, in resource, in binding, stage.Material, sync);
                }
            }
        }
        GraphPassId pass = GraphData.LayerTessellation!(ref graph, in data, stage.Material, _geometry._drawIndexedIndirectLayout!, BindResource);
        PassDefinition definition = graph.GetPass(pass);
        _shading.ReadIndexedMaterialTextures(ref graph, ref definition, _geometry._rasterInputs!.TextureReads, PipelineSync.AllShading);
        graph.SetPassTimeGroup(pass, ClusterGeometryPipeline.RasterTimeGroup);

        static int FindResource(ClusterRasterStageResources<ClusterTessellationCoreResource> inputs,
            in ShaderParameterResource resource)
        {
            ReadOnlySpan<ShaderParameterResource> resources = inputs.Resources.Resources;
            for (int index = 0; index < resources.Length; index++)
                if (resources[index].Name == resource.Name && resources[index].Type == resource.Type) return index;
            return -1;
        }
    }

    internal void InstallLayers(ref RenderGraphEdit graph, ClusterRasterStageSet rasterStages, ClusterShadeStageSet shadeStages)
    {
        FrameResources frame = GraphData.Resources;
        if (frame.LayerPackedViews == default)
        {
            ulong capacity = _geometry._options.MaxCandidates;
            frame.LayerPackedViews = graph.CreateTransientBuffer(new BufferDesc(
                (ulong)frame.ViewCount * ClusterViewUniforms.SizeInBytes, BufferUsages.ShaderRead,
                "Cluster layer views"), MemoryType.Upload);
            frame.LayerVisibleClusters = frame.AddBuffer(ref graph, capacity * VisibleClusterStride,
                BufferUsages.None, "Cluster layer visible clusters");
            frame.LayerTileWindows = frame.AddBuffer(ref graph, capacity * 4u * sizeof(uint),
                BufferUsages.None, "Cluster layer tile windows");
            frame.LayerDrawArgs = frame.AddBuffer(ref graph, 16, BufferUsages.CopySource, "Cluster layer counts");
            frame.LayerSecondDrawArgs = frame.AddBuffer(ref graph, 16, BufferUsages.CopySource, "Cluster layer second counts");
            frame.LayerDeferredWork = frame.AddBuffer(ref graph, capacity * CandidateStride, BufferUsages.None, "Cluster layer deferred work");
            frame.LayerDeferredCount = frame.AddBuffer(ref graph, sizeof(uint), BufferUsages.None, "Cluster layer deferred count");
            frame.LayerDeferredArgs = frame.AddBuffer(ref graph, 16, BufferUsages.Indirect, "Cluster layer deferred dispatch");
            GraphData.Resources = frame;
        }
        if (frame.TessellationPatternIndices == default)
        {
            frame.TessellationPatternIndices = UploadStructs<ushort>(ref graph, CreateTessellationPatternIndices(),
                BufferUsages.Index | BufferUsages.ShaderRead, "Cluster tessellation pattern indices");
            GraphData.Resources = frame;
        }
        if (GraphData.RasterPhases.TryGetValue(4, out ClusterGeometryPipeline.RasterPhase? previous))
        {
            if (GraphData.RasterBinning[4] is { } binning) RemoveRasterScans(ref graph, binning);
            foreach (ClusterGeometryPipeline.RasterBin bin in previous.Deformation.Values) _geometry.ReleaseRasterInputs(ref graph, bin.Inputs);
            foreach (ClusterGeometryPipeline.RasterBin bin in previous.Bins.Values) _geometry.ReleaseRasterInputs(ref graph, bin.Inputs);
            foreach (ClusterGeometryPipeline.TessellationGroup group in previous.TessellationGroups.Values)
            {
                foreach (ClusterGeometryPipeline.RasterBin bin in group.Seeds.Values) _geometry.ReleaseRasterInputs(ref graph, bin.Inputs);
                foreach (ClusterGeometryPipeline.TessellationSplitStage split in group.Split.Values) _geometry.ReleaseRasterInputs(ref graph, split.Inputs);
            }
            foreach (ClusterGeometryPipeline.RasterInputs input in previous.Patches.Values) _geometry.ReleaseRasterInputs(ref graph, input);
            graph.Remove(previous.Region);
        }
        if (GraphData.LayerCullingRegion != default) graph.Remove(GraphData.LayerCullingRegion);

        FrameResources layers = GetRasterFrameResources(frame, false, true);
        GraphRegionId region = _geometry._regions.LayerVisibility;
        GraphRegionId culling = graph.AddRegion("Layer cluster culling", region);
        GraphData.LayerCullingRegion = culling;
        var reset = new ClusterContinuationResetParameters(layers.CandidateCount, layers.TraversalArguments0,
            layers.CandidateArgsInitialization, layers.CandidateArgs, frame.LayerDrawArgs,
            layers.Phase2CandidateCount, layers.Phase2CandidateArgs, frame.LayerSecondDrawArgs);
        graph.AddCopyFramePass("Reset Cluster layer work", PassQueueSelection.AnyOfType(QueueType.Graphics),
            reset, new PassOptions(Culling: PassCullingMode.NeverCull, RepeatCommands: true),
            ClusterContinuationResetParameters.Declare, ClusterContinuationResetParameters.Record, region: culling);
        RecordWorkSeed(ref graph, layers, layers.PackedViews, layers.ViewCount, 0u, "layer", culling, layerTiles: true);
        GraphPassId culled = _geometry.RecordPersistentCull(ref graph, layers, layers.PackedViews, 2u,
            frame.LayerDrawArgs, frame.ReadOffsetZero, frame.ReadOffsetZero, frame.ReadOffsetZero,
            false, 4, "layer", culling, layerTiles: true);
        ClusterGeometryPipeline.RasterBin[] bins = _geometry.CreateRasterBins(rasterStages, shadeStages);
        ClusterGeometryPipeline.RasterPhase phase = RecordRasterPhase(ref graph, frame, bins,
            frame.LayerDrawArgs, frame.ReadOffsetZero, frame.ReadOffsetZero, frame.ReadOffsetZero, 4, false);
        GraphData.RasterPhases = GraphData.RasterPhases.SetItem(4, phase);
        GraphData.LayerGeometry = new RenderGeometryResources(layers.PackedViews, layers.InstanceData,
            layers.InstanceProperties, layers.InstancePropertiesRange, layers.VisibleClusters,
            frame.LayerDrawArgs, frame.LayerSecondDrawArgs, _geometry._options.MaxCandidates,
            layers.PageHeap, layers.Bvh, culled)
        {
            DeformCache = frame.DeformCache, DeformRegions = frame.DeformRegions,
            InstanceWorldBounds = GraphData.InstanceWorldBounds, WorkStatus = frame.WorkStatus,
            BoundsReady = GraphData.SceneBounds.Pass,
        };
    }
}
