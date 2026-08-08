namespace SomeEngine.Render.Cluster.Pipeline;

/// <summary>GPU-authored counters from the most recently completed full Cluster frame.</summary>
public sealed record ClusterFrameDiagnostics(
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
