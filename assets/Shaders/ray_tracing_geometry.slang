#include "feature_visible_clusters.slang"

struct GeometryExportUniforms
{
    uint Capacity;
    uint Enabled;
    uint2 Padding;
};

struct RayTracingClusterRecord
{
    DrawRequest Request;
    uint VertexBlobAddress;
    uint VertexBlobSize;
    uint TriangleStart;
    uint TriangleCount;
};

ConstantBuffer<GeometryExportUniforms> ExportUniforms;
ByteAddressBuffer PhaseOneCounts;
ByteAddressBuffer PhaseTwoCounts;
StructuredBuffer<VisibleCluster> VisibleClusters;
ByteAddressBuffer PageHeap;
RWStructuredBuffer<RayTracingClusterRecord> RayTracingClusters;
RWByteAddressBuffer RayTracingClusterCount;

void LoadRayTracingClusterInfo(
    ByteAddressBuffer pageHeap,
    uint pageOffset,
    uint clusterId,
    out uint vertexBlobAddress,
    out uint vertexBlobSize,
    out uint triangleStart,
    out uint packedCounts)
{
    uint clustersOffset = pageHeap.Load(pageOffset + 16u);
    uint vertexBlobsOffset = pageHeap.Load(pageOffset + 20u);
    uint clusterOffset = pageOffset + clustersOffset + clusterId * PACKED_CLUSTER_STRIDE_BYTES;
    vertexBlobAddress = pageOffset + vertexBlobsOffset + pageHeap.Load(clusterOffset + 0u);
    vertexBlobSize = pageHeap.Load(clusterOffset + 4u);
    triangleStart = pageHeap.Load(clusterOffset + 56u) >> 16u;
    packedCounts = pageHeap.Load(clusterOffset + 60u);
}

[numthreads(64, 1, 1)]
void BuildRayTracingGeometry(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    uint count = ExportUniforms.Enabled != 0
        ? ActiveVisibleClusterCount(PhaseOneCounts, PhaseTwoCounts, ExportUniforms.Capacity)
        : 0u;
    if (dispatchThreadId.x == 0u)
        RayTracingClusterCount.Store(0u, count);
    if (dispatchThreadId.x >= count)
        return;
    uint visibleIndex = ActiveVisibleClusterIndex(
        PhaseOneCounts,
        PhaseTwoCounts,
        ExportUniforms.Capacity,
        dispatchThreadId.x);
    DrawRequest request = VisibleClusterRequest(VisibleClusters[visibleIndex]);
    uint vertexBlobAddress;
    uint vertexBlobSize;
    uint triangleStart;
    uint packedCounts;
    LoadRayTracingClusterInfo(
        PageHeap,
        request.PageOffset,
        DrawRequestClusterID(request),
        vertexBlobAddress,
        vertexBlobSize,
        triangleStart,
        packedCounts);
    RayTracingClusterRecord output;
    output.Request = request;
    output.VertexBlobAddress = vertexBlobAddress;
    output.VertexBlobSize = vertexBlobSize;
    output.TriangleStart = triangleStart;
    output.TriangleCount = (packedCounts >> 8u) & 0xFFu;
    RayTracingClusters[dispatchThreadId.x] = output;
}
