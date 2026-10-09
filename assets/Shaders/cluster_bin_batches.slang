#ifndef CLUSTER_BIN_BATCHES_SLANG
#define CLUSTER_BIN_BATCHES_SLANG

void EmitRasterBatch(uint bin, uint visible, uint workIndex, bool software, bool depthBuckets, inout uint batchCount)
{
    if (!IsBin(bin)) return;
    batchCount++;
    WriteRasterBatch(bin, visible, workIndex, software, depthBuckets);
}

void BuildRasterBatches(
    uint visibleIndex,
    uint slotOffset,
    ClusterMaterialRanges materials,
    uint vertexCount,
    uint triangleCount,
    bool isSoftware)
{
    if (triangleCount == 0u)
        return;

    uint emittedBatches = 0u;
    uint rangeCount = ClusterMaterialRangeCount(materials, triangleCount);
    uint triangleStart = 0u;
    uint flatBatchIndex = 0u;
    uint rangeIndex = 0u;
    while (rangeIndex < rangeCount)
    {
        uint range = ClusterMaterialRange(PageHeap, materials, triangleCount, rangeIndex);
        uint rangeEnd = (range & 0xFFu) + ((range >> 8u) & 0xFFu);
        uint material = (range >> 16u) & 0xFFu;
        uint bin = RasterKey(RasterSlots, Uniforms, slotOffset, material);
        bool depthBuckets = Uniforms.DepthBucketsEnabled != 0u &&
            (GetStageSlot(RasterSlots, slotOffset, material, Uniforms.SlotCapacity) & 0x10000000u) != 0u;
        uint batchCount = ClusterMaterialBatchBits(PageHeap, materials, rangeIndex * 4u, 4u);
        bool useVertReuseBatches = RasterUsesVertReuseBatches(
            RasterSlots, Uniforms, slotOffset, material, isSoftware, vertexCount, triangleCount);
        if (!useVertReuseBatches)
        {
            uint mergedRangeCount = 1u;
            uint mergedBatchCount = batchCount;
            while (rangeIndex + mergedRangeCount < rangeCount && mergedRangeCount < 4u)
            {
                uint nextRange = rangeIndex + mergedRangeCount;
                uint next = ClusterMaterialRange(PageHeap, materials, triangleCount, nextRange);
                uint nextMaterial = (next >> 16u) & 0xFFu;
                uint nextBin = RasterKey(RasterSlots, Uniforms, slotOffset, nextMaterial);
                bool nextUsesBatches = RasterUsesVertReuseBatches(
                    RasterSlots, Uniforms, slotOffset, nextMaterial, isSoftware, vertexCount, triangleCount);
                if (nextBin != bin || nextUsesBatches)
                    break;
                mergedBatchCount += ClusterMaterialBatchBits(PageHeap, materials, nextRange * 4u, 4u);
                mergedRangeCount++;
            }

            EmitRasterBatch(bin, visibleIndex,
                RasterRangeWorkIndex(rangeIndex, mergedRangeCount), isSoftware, depthBuckets, emittedBatches);
            flatBatchIndex += mergedBatchCount;
            rangeIndex += mergedRangeCount;
            uint last = ClusterMaterialRange(PageHeap, materials, triangleCount, rangeIndex - 1u);
            triangleStart = (last & 0xFFu) + ((last >> 8u) & 0xFFu);
            continue;
        }

        for (uint rangeBatchIndex = 0u; rangeBatchIndex < batchCount; rangeBatchIndex++)
        {
            EmitRasterBatch(bin, visibleIndex, flatBatchIndex, isSoftware, depthBuckets, emittedBatches);
            triangleStart = min(
                triangleStart + ClusterMaterialBatchBits(PageHeap, materials,
                    max(rangeCount, 3u) * 4u + flatBatchIndex * 5u, 5u) + 1u,
                rangeEnd);
            flatBatchIndex++;
        }
        triangleStart = rangeEnd;
        rangeIndex++;
    }
}

#endif
