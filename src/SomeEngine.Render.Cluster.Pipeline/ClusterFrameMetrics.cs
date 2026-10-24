namespace SomeEngine.Render.Cluster.Pipeline;

/// <summary>Scalar measurements from one completed Cluster frame.</summary>
public readonly record struct ClusterFrameMetrics(
    ulong FrameIndex,
    uint CandidateCount,
    uint CandidateDispatchGroups,
    uint PhaseOneSoftwareClusters,
    uint PhaseOneHardwareClusters,
    uint PhaseTwoCandidateCount,
    uint PhaseTwoDispatchGroups,
    uint PhaseTwoSoftwareClusters,
    uint PhaseTwoHardwareClusters,
    uint RasterBatches,
    uint SoftwareRasterBatches,
    uint FirstShadeBinTiles,
    uint ShadingWorkItems,
    uint TessellationFinalPatches,
    uint DroppedTessellationPatches,
    uint CulledTessellationPatches,
    uint QualityLimitedTessellationPatches,
    uint SoftwareRasterDebugRecords,
    uint VisibilityProbePixels)
{
    public ulong FrameIdentity { get; init; }
    /// <summary>Vertex items in selected primary-view clusters, including duplicates across clusters and instances.</summary>
    public uint SelectedVertices { get; init; }
    /// <summary>Triangle items in selected primary-view clusters across both occlusion phases.</summary>
    public uint SelectedTriangles { get; init; }
    /// <summary>Bytes reserved by user deformation regions, including alignment, across this frame's views.</summary>
    public uint DeformCacheBytes { get; init; }
}
