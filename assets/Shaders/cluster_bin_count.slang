#include "cluster_bin_common.slang"
#include "cluster_instance_properties.slang"
#include "resource_effects.slang"

ConstantBuffer<RenderInstancePropertyMetadata> InstanceProperties;
ByteAddressBuffer InstanceData;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWStructuredBuffer<RasterBinEntry> RasterBinMeta;
ConstantBuffer<RasterBinningUniforms> Uniforms;
StructuredBuffer<DrawRequest> VisibleClusters;
ByteAddressBuffer DrawArgs;
ByteAddressBuffer ClusterReadOffsetArgs;
ByteAddressBuffer ClusterReadOffsetArgs1;
ByteAddressBuffer ClusterReadOffsetArgs2;
StructuredBuffer<uint> RasterSlots;
ByteAddressBuffer PageHeap;

[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWStructuredBuffer<uint> BinnedClusterIndexBuffer;

void WriteRasterBatch(uint bin, uint visibleIndex, uint workIndex, bool isSoftware)
{
    if (!IsBin(bin)) return;
    uint capacity = RasterBinMeta[bin].BinCapacity;
    if (capacity == 0u)
    {
        CountRaster(RasterBinMeta, bin, isSoftware);
        return;
    }
    uint key = (bin << 1u) | (isSoftware ? 1u : 0u);
    uint4 matching = WaveMatchingLanes(key);
    uint lane = WaveGetLaneIndex();
    uint leader = WaveMaskFirstLane(matching);
    uint first = 0u;
    if (lane == leader)
    {
        if (isSoftware)
            InterlockedAdd(RasterBinMeta[bin].BinSWCount, WaveMaskBitCount(matching), first);
        else
            InterlockedAdd(RasterBinMeta[bin].BinHWCount, WaveMaskBitCount(matching), first);
    }
    uint item = WaveReadLaneAt(first, leader) + WaveMaskPrefix(matching, lane);
    // Keep both lists in append order. The hardware region starts after the
    // maximum software work derived from this phase's visible-cluster count.
    uint index = (isSoftware ? RasterBinMeta[bin].ClusterOffset : RasterBinMeta[bin].HardwareOffset) + item;
    BinnedClusterIndexBuffer[index] = visibleIndex | (workIndex << 22u);
}

#include "cluster_bin_batches.slang"

[shader("compute")]
[numthreads(64, 1, 1)]
void CountBins(uint3 threadId : SV_DispatchThreadID)
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
    uint vertexCount;
    uint triangleCount;
    uint packedMaterials;
    uint packedRanges;
    ReadClusterBinData(
        PageHeap,
        entry,
        vertReuseBatchWords,
        vertexCount,
        triangleCount,
        packedMaterials,
        packedRanges);
    uint slotOffset = LoadClusterMaterialSlotOffset(
        InstanceData,
        LoadRenderInstanceMetadata(
            InstanceProperties,
            CLUSTER_MATERIAL_SLOT_OFFSET_METADATA_WORD),
        entry.InstanceID);
    BuildRasterBatches(
        visibleIndex,
        slotOffset,
        vertReuseBatchWords,
        vertexCount,
        triangleCount,
        packedMaterials,
        packedRanges,
        isSoftware);
}
