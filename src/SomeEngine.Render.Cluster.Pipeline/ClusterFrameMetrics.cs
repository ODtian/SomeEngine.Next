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
    public ulong ResidencyEpoch { get; init; }
}
