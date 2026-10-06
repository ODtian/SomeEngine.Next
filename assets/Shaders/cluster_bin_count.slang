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
StructuredBuffer<uint2> VisibleClusterRasterWindows;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWStructuredBuffer<uint> PolicyRasterWorkCounter;
#include "cluster_raster_target_view.slang"

void WriteRasterBatch(uint bin, uint visibleIndex, uint workIndex, bool isSoftware)
{
    if (!IsBin(bin)) return;
    if (Uniforms.PolicyTarget != 0u)
    {
        uint4 pages = ClusterUnpackRasterTargetPageWindow(VisibleClusterRasterWindows[visibleIndex]);
        uint2 extent = pages.zw - pages.xy + 1u;
        // Page-batch area is invariant under partitioning. Software remains one
        // bounded geometry batch; do not lower resolution just for more HW windows.
        uint area = isSoftware ? 64u : extent.x * extent.y;
        InterlockedAdd(PolicyRasterWorkCounter[0], area);
    }
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
    uint entries, stride;
    BinnedClusterIndexBuffer.GetDimensions(entries, stride);
    if (index >= entries)
    {
        RejectClusterWork(16u);
        return;
    }
    BinnedClusterIndexBuffer[index] = visibleIndex | (workIndex << 22u);
}

#include "cluster_bin_batches.slang"
#include "cluster_work_status.slang"
StructuredBuffer<ClusterRasterTargetView> WorkTargetViews;

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
    if (Uniforms.PolicyTarget != 0u)
    {
        ClusterRasterTargetView target = WorkTargetViews[DrawRequestViewIndex(entry)];
        if (target.Padding.y != 0u)
            InterlockedAdd(PolicyRasterWorkCounter[1u + target.Padding.x * 2u + (isSoftware ? 1u : 0u)], 1u);
    }
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
