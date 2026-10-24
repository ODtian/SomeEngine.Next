#ifndef CLUSTER_BIN_BATCHES_SLANG
#define CLUSTER_BIN_BATCHES_SLANG

void BuildRasterBatches(
    uint visibleIndex,
    uint slotOffset,
    uint4 vertReuseBatchWords,
    uint vertexCount,
    uint triangleCount,
    uint3 packedMaterials,
    uint packedRanges,
    bool isSoftware)
{
    if (triangleCount == 0u)
        return;

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

            WriteRasterBatch(
                bin,
                visibleIndex,
                RasterRangeWorkIndex(rangeIndex, mergedRangeCount),
                isSoftware);
            flatBatchIndex += mergedBatchCount;
            rangeIndex += mergedRangeCount;
            triangleStart = MaterialRangeEnd(
                packedRanges, triangleCount, rangeIndex - 1u);
            continue;
        }

        for (uint rangeBatchIndex = 0u; rangeBatchIndex < batchCount; rangeBatchIndex++)
        {
            WriteRasterBatch(
                bin,
                visibleIndex,
                flatBatchIndex,
                isSoftware);
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
