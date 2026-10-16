namespace SomeEngine.Render.Cluster.Pipeline;

/// <summary>Completed GPU work for the blend effects of one simulation frame.</summary>
public readonly record struct ClusterBlendMetrics(
    int SimulationFrame,
    ulong SubmittedFrame,
    double ProjectionIndexMs,
    double CoverageReceiverMs,
    double MaterialCompositeMs,
    uint Triangles,
    uint References,
    ulong UsedBytes,
    ulong CapacityBytes,
    ulong AllocationBytes,
    bool ReusedIndex,
    double DecalIndexMs,
    double OpaqueMaterialMs,
    uint DecalVolumes,
    uint DecalReferences,
    uint DecalResidentReferences,
    ulong DecalUsedBytes,
    ulong DecalCapacityBytes,
    ulong DecalAllocationBytes,
    bool ReusedDecalIndex);
