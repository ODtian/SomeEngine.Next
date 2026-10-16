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
    /// </summary>
    public void UninstallGraph()
    {
        if (_graphRemoved) return;
        if (_hasPendingFrame) throw new InvalidOperationException("A Cluster frame must finish before its graph is removed.");
        using RenderGraphEdit edit = _graph.BeginEdit();
        RenderGraphEdit measurementEdit = edit;
        RemoveBlendMeasurements(ref measurementEdit);
        if (GraphData.Transparency is { } transparency)
        {
            RenderGraphEdit transparencyEdit = edit;
            RemoveTransparency(ref transparencyEdit, transparency);
            GraphData.Transparency = null;
        }
        if (_surfaceReceiverRegion != default) edit.Remove(_surfaceReceiverRegion);
        if (GraphData.Stencil is { } stencil)
        {
            RenderGraphEdit stencilEdit = edit;
            stencil.Remove(ref stencilEdit);
            GraphData.Stencil = null;
        }
        edit.Remove(_regions.PrimaryGeometry);
        edit.Remove(_regions.TargetGeometry);
        edit.Remove(_regions.Shading);
        edit.Remove(_regions.Feedback);
        if (_graphInstalled)
        {
            FrameResources frame = GraphData.Resources;
            HashSet<GraphBufferId> buffers = [
                frame.WorkStatus,
                frame.FrameMetricsBuffer,
                frame.PackedViews,
                frame.RasterSlots,
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
                frame.SurfaceCounts,
                frame.SurfaceScatterCounts,
                frame.SurfaceDataOffsets,
                frame.SurfaceDataSizes,
                frame.MaterialSurfaces,
                frame.SurfaceReceivers,
                frame.AdditionalSurfaceReceivers,
                GraphData.ShaderParameterBytes, GraphData.SceneChunks, GraphData.InstanceWorldBounds,
                frame.Work.Nodes, frame.Work.Clusters, frame.Work.NodeReady, frame.Work.ClusterReady, frame.Work.Arguments,
            ];
            if (GraphData.Decals is { } decals)
                buffers.UnionWith([decals.Counts, decals.Offsets, decals.Writes, decals.References, decals.Ranges, decals.Work]);
            buffers.UnionWith(frame.TessellationLevelDispatchArgs);
            buffers.UnionWith(GraphData.BufferDescriptions.Keys);
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
        edit.Commit();
        _graphRemoved = true;
        GraphData.Outputs = default;
    }
}
