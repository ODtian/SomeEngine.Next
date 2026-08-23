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
    uint ShadedPixels,
    uint BinnedDeformClusters,
    uint CachedDeformClusters,
    uint DeformCacheBytes,
    ulong DeformCacheCapacityBytes,
    uint SoftwareRasterDebugRecords);
