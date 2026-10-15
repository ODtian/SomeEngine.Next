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
    bool ReusedIndex);
