#ifndef CLUSTER_BIN_BATCHES_SLANG
#define CLUSTER_BIN_BATCHES_SLANG

bool RasterBatchUsesSoftware(uint slotOffset, uint material, bool software)
{
    uint stage = GetStageSlot(RasterSlots, slotOffset, material, Uniforms.SlotCapacity);
    // Pixel-programmable ranges use the existing meshlet raster path when it
    // is available. Target windows and tessellated ranges keep their prior route.
    bool pixelMeshlet = (stage & 0x50000000u) == 0x50000000u;
    return software && (Uniforms.PolicyTarget != 0u || !pixelMeshlet);
}

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
        uint material = materials.Materials[rangeIndex];
        uint bin = RasterKey(RasterSlots, Uniforms, slotOffset, material);
        bool depthBuckets = Uniforms.DepthBucketsEnabled != 0u &&
            (GetStageSlot(RasterSlots, slotOffset, material, Uniforms.SlotCapacity) & 0x10000000u) != 0u;
        uint batchCount = ClusterMaterialBatchBits(PageHeap, materials, rangeIndex * 4u, 4u);
        bool software = RasterBatchUsesSoftware(slotOffset, material, isSoftware);
        bool useVertReuseBatches = RasterUsesVertReuseBatches(
            RasterSlots, Uniforms, slotOffset, material, software, vertexCount, triangleCount);
        if (!useVertReuseBatches)
        {
            uint mergedRangeCount = 1u;
            uint mergedBatchCount = batchCount;
            while (rangeIndex + mergedRangeCount < rangeCount && mergedRangeCount < 4u)
            {
                uint nextRange = rangeIndex + mergedRangeCount;
                uint next = ClusterMaterialRange(PageHeap, materials, triangleCount, nextRange);
                uint nextMaterial = materials.Materials[nextRange];
                uint nextBin = RasterKey(RasterSlots, Uniforms, slotOffset, nextMaterial);
                bool nextSoftware = RasterBatchUsesSoftware(slotOffset, nextMaterial, isSoftware);
                bool nextUsesBatches = RasterUsesVertReuseBatches(
                    RasterSlots, Uniforms, slotOffset, nextMaterial, nextSoftware, vertexCount, triangleCount);
                if (nextBin != bin || nextSoftware != software || nextUsesBatches)
                    break;
                mergedBatchCount += ClusterMaterialBatchBits(PageHeap, materials, nextRange * 4u, 4u);
                mergedRangeCount++;
            }

            EmitRasterBatch(bin, visibleIndex,
                RasterRangeWorkIndex(rangeIndex, mergedRangeCount), software, depthBuckets, emittedBatches);
            flatBatchIndex += mergedBatchCount;
            rangeIndex += mergedRangeCount;
            uint last = ClusterMaterialRange(PageHeap, materials, triangleCount, rangeIndex - 1u);
            triangleStart = (last & 0xFFu) + ((last >> 8u) & 0xFFu);
            continue;
        }

        for (uint rangeBatchIndex = 0u; rangeBatchIndex < batchCount; rangeBatchIndex++)
        {
            EmitRasterBatch(bin, visibleIndex, flatBatchIndex, software, depthBuckets, emittedBatches);
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
