#ifndef CLUSTER_TARGET_SOURCE_RANGES_SLANG
#define CLUSTER_TARGET_SOURCE_RANGES_SLANG

struct ClusterTargetBatchUniforms
{
    uint64_t FirstSourceCluster;
    uint SourceClusterCount;
    uint CollectDeferredOnly;
    uint FirstTargetView;
    uint TargetViewCount;
    uint Padding0;
    uint Padding;
};

StructuredBuffer<uint2> SourceClusterRanges;
StructuredBuffer<uint64_t> TargetInstanceSourceOffsets;
ConstantBuffer<ClusterTargetBatchUniforms> TargetBatch;

uint64_t2 GetClusterTargetSourceRange()
{
    return uint64_t2(TargetBatch.FirstSourceCluster, uint64_t(TargetBatch.SourceClusterCount));
}

bool ClusterTargetNodeIntersectsSourceRange(uint nodeIndex, uint instanceIndex)
{
    if (TargetBatch.SourceClusterCount == 0u) return true;
    uint64_t2 batch = GetClusterTargetSourceRange();
    if (batch.y == 0u) return false;
    uint2 range = SourceClusterRanges[nodeIndex];
    if (range.y == 0u) return true;
    uint64_t first = TargetInstanceSourceOffsets[instanceIndex] + uint64_t(range.x);
    uint64_t end = first + uint64_t(range.y);
    return first < uint64_t(batch.x) + batch.y && uint64_t(batch.x) < end;
}

void ClipClusterTargetSourceRange(uint nodeIndex, uint instanceIndex,
    inout uint firstCluster, inout uint clusterCount)
{
    if (TargetBatch.SourceClusterCount == 0u) return;
    uint64_t2 batch = GetClusterTargetSourceRange();
    uint2 range = SourceClusterRanges[nodeIndex];
    uint64_t first = TargetInstanceSourceOffsets[instanceIndex] + uint64_t(range.x);
    uint64_t end = first + clusterCount;
    uint64_t clippedFirst = max(first, uint64_t(batch.x));
    uint64_t clippedEnd = min(end, uint64_t(batch.x) + batch.y);
    firstCluster += uint(clippedFirst - first);
    clusterCount = uint(clippedEnd - clippedFirst);
}

#endif
