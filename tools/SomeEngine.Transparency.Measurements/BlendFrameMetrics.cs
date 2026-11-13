using SomeEngine.Render.Features.Layers;

namespace SomeEngine.Transparency.Measurements;

/// <summary>Completed GPU layer work matched to one simulation frame.</summary>
public readonly record struct BlendFrameMetrics(
    int SimulationFrame,
    ulong SubmittedFrame,
    double LayerVisibilityMs,
    double LayerClassificationMs,
    double LayerShadingMs,
    double LayerCompositeMs,
    double OpaqueRequestsMs,
    double OpaqueShadingMs,
    double DecalBinningMs,
    LayerCompositionMetrics Layers,
    ulong RecordBytes,
    ulong PayloadBytes,
    ulong BlockPoolPeakBytes,
    ulong BlockPoolCapacityBytes,
    ulong FragmentStorageUsedBytes,
    ulong FragmentStorageCapacityBytes,
    ulong FragmentStorageAllocationBytes,
    uint DecalVolumes,
    uint DecalReferences,
    uint DecalRetainedReferences,
    ulong DecalUsedBytes,
    ulong DecalCapacityBytes,
    ulong DecalAllocationBytes,
    bool ReusedDecalBinning);
