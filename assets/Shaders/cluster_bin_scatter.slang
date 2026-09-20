#include "cluster_bin_common.slang"
#include "cluster_instance_properties.slang"
#include "resource_effects.slang"

ConstantBuffer<RenderInstancePropertyMetadata> InstanceProperties;
ByteAddressBuffer InstanceData;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWStructuredBuffer<RasterBinEntry> RasterBinMeta;
ConstantBuffer<RasterBinningUniforms> Uniforms;
StructuredBuffer<DrawRequest> VisibleClusters;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWStructuredBuffer<uint> BinnedClusterIndexBuffer;
ByteAddressBuffer DrawArgs;
ByteAddressBuffer ClusterReadOffsetArgs;
ByteAddressBuffer ClusterReadOffsetArgs1;
ByteAddressBuffer ClusterReadOffsetArgs2;
StructuredBuffer<uint> RasterSlots;
ByteAddressBuffer PageHeap;

void ScatterRasterBatches(
    uint visibleIndex,
    uint slotOffset,
    uint4 vertReuseBatchWords,
    uint triangleCount,
    uint packedMaterials,
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
        bool useVertReuseBatches = !isSoftware || RasterSoftwareUsesVertReuseBatches(
            RasterSlots, Uniforms, slotOffset, material);
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
                bool nextUsesBatches = RasterSoftwareUsesVertReuseBatches(
                    RasterSlots, Uniforms, slotOffset, nextMaterial);
                if (nextBin != bin || nextUsesBatches)
                    break;
                mergedBatchCount += VertReuseBatchCount(vertReuseBatchWords, nextRange);
                mergedRangeCount++;
            }

            ScatterRaster(
                RasterBinMeta,
                BinnedClusterIndexBuffer,
                bin,
                visibleIndex,
                RasterRangeWorkIndex(rangeIndex, mergedRangeCount),
                true);
            flatBatchIndex += mergedBatchCount;
            rangeIndex += mergedRangeCount;
            triangleStart = MaterialRangeEnd(
                packedRanges, triangleCount, rangeIndex - 1u);
            continue;
        }

        for (uint rangeBatchIndex = 0u; rangeBatchIndex < batchCount; rangeBatchIndex++)
        {
            ScatterRaster(
                RasterBinMeta,
                BinnedClusterIndexBuffer,
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

[shader("compute")]
[numthreads(64, 1, 1)]
void ScatterBins(uint3 threadId : SV_DispatchThreadID)
{
    CullDrawArgs args = LoadCullDrawArgs(DrawArgs, 0u);
    uint visibleCount = args.SWCount + args.HWCount;
    if (threadId.x >= visibleCount)
        return;

    uint visibleIndex;
    bool isSoftware;
    DecodeVisibleCluster(
        DrawArgs,
        ClusterReadOffsetArgs,
        ClusterReadOffsetArgs1,
        ClusterReadOffsetArgs2,
        Uniforms.MaxVisibleClusters,
        threadId.x,
        visibleIndex,
        isSoftware);

    DrawRequest entry = VisibleClusters[visibleIndex];
    uint4 vertReuseBatchWords;
    uint triangleCount;
    uint packedMaterials;
    uint packedRanges;
    ReadClusterBinData(
        PageHeap,
        entry,
        vertReuseBatchWords,
        triangleCount,
        packedMaterials,
        packedRanges);
    uint slotOffset = LoadClusterMaterialSlotOffset(
        InstanceData,
        LoadRenderInstanceMetadata(
            InstanceProperties,
            CLUSTER_MATERIAL_SLOT_OFFSET_METADATA_WORD),
        entry.InstanceID);
    ScatterRasterBatches(
        visibleIndex,
        slotOffset,
        vertReuseBatchWords,
        triangleCount,
        packedMaterials,
        packedRanges,
        isSoftware);
}
