using SomeEngine.Render;
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
    public ClusterWorkMetrics Work { get; init; }
}

/// <summary>Distinct completed-frame work units, not queue/storage descriptions.
/// Target tests include main and post-HiZ attempts. Empty partitions enter no raster bin.
/// Raster batches and surface patches include both primary and target work.</summary>
public readonly record struct ClusterWorkMetrics(
    RenderGeometryWorkFlags Overflow,
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
    /// <summary>Required geometry completed, including batches replayed through the spill input.</summary>
    public bool Complete => (Overflow & ~RenderGeometryWorkFlags.RasterBatches) == RenderGeometryWorkFlags.None;
}
