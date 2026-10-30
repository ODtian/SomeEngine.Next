using SomeEngine.RenderGraph;

namespace SomeEngine.Render.Cluster.Pipeline;

public sealed partial class ClusterPipelineSystem
{
    private bool _graphRemoved;

    private static ClusterGraphRegions CreateGraphRegions(ref RenderGraphEdit edit, in ClusterGraphRegions regions) => new(
        edit.AddRegion("Cluster pipeline geometry", regions.PrimaryGeometry),
        edit.AddRegion("Cluster pipeline targets", regions.TargetGeometry),
        edit.AddRegion("Cluster pipeline shading", regions.Shading),
        edit.AddRegion("Cluster pipeline feedback", regions.Feedback));

    /// <summary>
    /// Removes this pipeline at a frame boundary, after consumers have detached its outputs.
    /// Submitted work retires through RenderGraph; shared geometry remains installed.
    /// An uninstalled pipeline cannot be installed again. Dispose its frame-system container afterwards.
    /// Disposing that container alone does not uninstall the graph. Call this method before continuing
    /// to use the graph without this pipeline. During whole-graph shutdown, RenderGraph.Dispose releases
    /// installed resource uses whether the frame-system container is disposed before or after it.
    /// </summary>
    public void UninstallGraph()
    {
        if (_graphRemoved) return;
        if (_hasPendingFrame) throw new InvalidOperationException("A Cluster frame must finish before its graph is removed.");
        EditGraph((ref RenderGraphEdit edit) =>
        {
            RemoveBlendMeasurements(ref edit);
            if (GraphData.Transparency is { } transparency)
            {
                RemoveTransparency(ref edit, transparency);
                GraphData.Transparency = null;
            }
            if (GraphData.Stencil is { } stencil)
            {
                stencil.Remove(ref edit);
                GraphData.Stencil = null;
            }
            if (_transparencyRequestRegion != default) edit.Remove(_transparencyRequestRegion);
            if (_shadowRequestRegion != default) edit.Remove(_shadowRequestRegion);
            edit.Remove(_regions.PrimaryGeometry);
            edit.Remove(_regions.TargetGeometry);
            edit.Remove(_regions.Shading);
            edit.Remove(_regions.Feedback);
            foreach (MaterialBuffer buffer in GraphData.MaterialBuffers.Values)
                edit.ReleaseExternalBufferUse(buffer.Use);
            GraphData.MaterialBuffers = GraphData.MaterialBuffers.Clear();
            if (_graphInstalled)
            {
                FrameResources frame = GraphData.Resources;
                HashSet<GraphBufferId> buffers = [
                    frame.WorkStatus,
                    frame.FrameMetricsBuffer,
                    frame.PackedViews,
                    frame.RasterSlots,
                    frame.MaterialBounds,
                    frame.ShadeSlots,
                    frame.ReadOffsetZero,
                    frame.CandidateArgs,
                    frame.NeutralPolicyValue,
                    frame.NeutralPolicyCounter,
                    frame.NeutralPolicyView,
                    frame.TraversalNodes0,
                    frame.TraversalArguments0,
                    frame.CandidateArgsInitialization,
                    frame.CandidateClusters,
                    frame.CandidateCount,
                    frame.DrawArgs,
                    frame.PageFaults,
                    frame.Phase2CandidateArgs,
                    frame.Phase2CandidateClusters,
                    frame.Phase2CandidateCount,
                    frame.Phase2DrawArgs,
                    frame.ContinuationDrawArgs,
                    frame.ContinuationPostDrawArgs,
                    frame.VisibleClusters,
                    frame.TargetDeferredWork,
                    frame.TargetDeferredCount,
                    frame.TargetDeferredArgs,
                    frame.TargetVisibleClusters,
                    frame.RasterBinMeta,
                    frame.DepthBuckets,
                    frame.BinnedClusters,
                    frame.HardwareIndirectArgs,
                    frame.RasterWorkArguments,
                    frame.SoftwareDispatchArgs,
                    frame.RasterReserveCounters,
                    frame.TessellationCounters,
                    frame.PhaseOneTessellationCounters,
                    frame.TessellationWorkPatches,
                    frame.TessellationWorkPatchesAlternate,
                    frame.TessellationFinalPatches,
                    frame.TessellationLevelCounts,
                    frame.TessellationSourceDispatchArgs,
                    frame.TessellationSeedDispatchArgs,
                    frame.TessellationBinCounts,
                    frame.TessellationBinOffsets,
                    frame.TessellationBinScatterCounts,
                    frame.TessellationBinReserveCounter,
                    frame.TessellationBinDispatchArgs,
                    frame.TessellationHardwareDrawArgs,
                    frame.TessellationPatternIndices,
                    frame.SoftwareDebug,
                    frame.ShadeBinCounts,
                    frame.ShadeBinOffsets,
                    frame.ShadeIndirectArgs,
                    frame.ShadeScatterCounts,
                    frame.ShadeReserveCounters,
                    frame.PixelCoordinates,
                    frame.VisibilityWinnerBuffer,
                    GraphData.ShaderParameterBytes, GraphData.SceneChunks, GraphData.InstanceWorldBounds,
                    GraphData.ScalarBytes, GraphData.ScalarOffsets, GraphData.SceneInstanceData,
                    GraphData.LeafFlagEmptyData, GraphData.LeafFlagEmptyParameters,
                    frame.DeformCache, frame.DeformRegions, frame.DeformRequests, frame.DeformPrefixOffsets,
                    frame.DeformPrefixGroups, frame.DeformAllocation, frame.DeformArguments,
                    frame.Work.Nodes, frame.Work.Clusters, frame.Work.NodeReady, frame.Work.Arguments,
                ];
                buffers.UnionWith(frame.TessellationLevelDispatchArgs);
                foreach (GraphBufferId buffer in buffers)
                    if (buffer != default) edit.Remove(buffer);
                HashSet<GraphTextureId> textures = [
                    frame.NeutralPolicyOutput,
                    frame.VisBuffer,
                    frame.VisibilityWinnerImage,
                    frame.Depth,
                    frame.PolicyVisibilityScratch,
                    frame.PolicyDepthScratch,
                    frame.SceneColor,
                    frame.MotionVectors,
                ];
                textures.UnionWith(frame.HiZ);
                foreach (GraphTextureId texture in textures)
                    if (texture != default) edit.Remove(texture);
            }
            // Decal setup and the shared parameter arena are installed by OnCreate,
            // even when no full geometry reconciliation has happened yet.
            if (GraphData.Decals is { } decals)
                foreach (GraphBufferId buffer in new[] { decals.Volumes, decals.Properties, decals.EmptyViews,
                    decals.Counts, decals.Offsets, decals.Writes, decals.References, decals.Ranges, decals.Work })
                    if (buffer != default) edit.Remove(buffer);
            if (!_graphInstalled && GraphData.ShaderParameterBytes != default) edit.Remove(GraphData.ShaderParameterBytes);
            foreach (MaterialTexture texture in GraphData.MaterialTextures.Values)
                edit.ReleaseExternalTextureUse(texture.Use);
            if (GraphData.MissingMaterialTexture is { } missing)
                edit.ReleaseExternalTextureUse(missing.Use);
            GraphData.MaterialTextures = GraphData.MaterialTextures.Clear();
            GraphData.MissingMaterialTexture = null;
            GraphData.Outputs = default;
        }, () => _graphRemoved = true, passRegions: []);
    }
}
