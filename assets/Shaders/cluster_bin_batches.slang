#ifndef CLUSTER_BIN_BATCHES_SLANG
#define CLUSTER_BIN_BATCHES_SLANG

// The existing buffer budget permits at most nine batches per visible item.
// Reject unsupported expansion before it can alias another item's bin region.
void EmitRasterBatch(uint bin, uint visible, uint workIndex, bool software, inout uint batchCount)
{
    if (!IsBin(bin)) return;
    if (batchCount >= 9u)
    {
        RejectClusterWork(16u);
        return;
    }
    batchCount++;
    WriteRasterBatch(bin, visible, workIndex, software);
}

void BuildRasterBatches(
    uint visibleIndex,
    uint slotOffset,
    uint4 vertReuseBatchWords,
    uint vertexCount,
    uint triangleCount,
    uint packedMaterials,
    uint packedRanges,
    bool isSoftware)
{
    if (triangleCount == 0u)
        return;

    uint emittedBatches = 0u;
    uint rangeCount = MaterialRangeCount(packedRanges, triangleCount);
    uint triangleStart = 0u;
    uint flatBatchIndex = 0u;
    uint rangeIndex = 0u;
    while (rangeIndex < rangeCount)
    {
        uint rangeEnd = MaterialRangeEnd(packedRanges, triangleCount, rangeIndex);
        uint material = LocalMaterial(packedMaterials, packedRanges, triangleStart);
        uint bin = RasterKey(RasterSlots, Uniforms, slotOffset, material);
        uint batchCount = VertReuseBatchCount(vertReuseBatchWords, rangeIndex);
        bool useVertReuseBatches = RasterUsesVertReuseBatches(
            RasterSlots, Uniforms, slotOffset, material, isSoftware, vertexCount, triangleCount);
        if (!useVertReuseBatches)
        {
            uint mergedRangeCount = 1u;
            uint mergedBatchCount = batchCount;
            while (rangeIndex + mergedRangeCount < rangeCount)
            {
                uint nextRange = rangeIndex + mergedRangeCount;
                uint nextStart = MaterialRangeEnd(packedRanges, triangleCount, nextRange - 1u);
                uint nextMaterial = LocalMaterial(packedMaterials, packedRanges, nextStart);
                uint nextBin = RasterKey(RasterSlots, Uniforms, slotOffset, nextMaterial);
                bool nextUsesBatches = RasterUsesVertReuseBatches(
                    RasterSlots, Uniforms, slotOffset, nextMaterial, isSoftware, vertexCount, triangleCount);
                if (nextBin != bin || nextUsesBatches)
                    break;
                mergedBatchCount += VertReuseBatchCount(vertReuseBatchWords, nextRange);
                mergedRangeCount++;
            }

            EmitRasterBatch(bin, visibleIndex,
                RasterRangeWorkIndex(rangeIndex, mergedRangeCount), isSoftware, emittedBatches);
            flatBatchIndex += mergedBatchCount;
            rangeIndex += mergedRangeCount;
            triangleStart = MaterialRangeEnd(
                packedRanges, triangleCount, rangeIndex - 1u);
            continue;
        }

        for (uint rangeBatchIndex = 0u; rangeBatchIndex < batchCount; rangeBatchIndex++)
        {
            EmitRasterBatch(bin, visibleIndex, flatBatchIndex, isSoftware, emittedBatches);
            triangleStart = min(
                triangleStart + VertReuseBatchTriangleCount(vertReuseBatchWords, flatBatchIndex),
                rangeEnd);
            flatBatchIndex++;
        }
        triangleStart = rangeEnd;
        rangeIndex++;
    }
}

#endif
