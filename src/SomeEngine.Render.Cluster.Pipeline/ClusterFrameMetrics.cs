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
    /// <summary>Vertex items in selected primary-view clusters, including cluster and instance repetitions.</summary>
    public ulong SelectedVertices { get; init; }
    /// <summary>Triangle items selected across both primary-view occlusion phases.</summary>
    public ulong SelectedTriangles { get; init; }
    /// <summary>Bytes reserved by the user deformation regions, including alignment.</summary>
    public uint DeformCacheBytes { get; init; }
    public ulong ResidencyEpoch { get; init; }
    public ClusterWorkMetrics Work { get; init; }
}

/// <summary>Distinct completed-frame work units, not queue/storage descriptions.
/// Target tests include main and post-HiZ attempts. Empty partitions enter no raster bin.
/// Raster batches and surface patches include both primary and target work.</summary>
public readonly record struct ClusterWorkMetrics(
    ClusterWorkOverflow Overflow,
    ulong TargetCandidateTests,
    ulong PotentialHardwareWindows,
    ulong EmptyHardwareWindows,
    ulong RequiredHardwareWindows,
    ulong AcceptedHardwareWindows,
    ulong RequiredSoftwareItems,
    ulong AcceptedSoftwareItems,
    ulong HardwareRasterBatches,
    ulong SoftwareRasterBatches,
    ulong RequiredSurfaceRoots,
    ulong RequiredFinalSurfacePatches)
{
    public bool Complete => Overflow == ClusterWorkOverflow.None;
}
