#include "feature_visible_clusters.slang"

struct GeometryExportUniforms
{
    uint Capacity;
    uint Enabled;
    uint2 Padding;
};

ConstantBuffer<GeometryExportUniforms> ExportUniforms;
ByteAddressBuffer PhaseOneCounts;
ByteAddressBuffer PhaseTwoCounts;
StructuredBuffer<DrawRequest> VisibleClusters;
RWStructuredBuffer<DrawRequest> StreamOutputClusters;
RWByteAddressBuffer StreamOutputCount;

[numthreads(64, 1, 1)]
void BuildStreamOutput(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    uint count = ExportUniforms.Enabled != 0
        ? ActiveVisibleClusterCount(PhaseOneCounts, PhaseTwoCounts, ExportUniforms.Capacity)
        : 0u;
    if (dispatchThreadId.x == 0u)
        StreamOutputCount.Store(0u, count);
    if (dispatchThreadId.x >= count)
        return;
    uint visibleIndex = ActiveVisibleClusterIndex(
        PhaseOneCounts,
        PhaseTwoCounts,
        ExportUniforms.Capacity,
        dispatchThreadId.x);
    StreamOutputClusters[dispatchThreadId.x] = VisibleClusters[visibleIndex];
}
