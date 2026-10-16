#include "cluster_bin_common.slang"
#include "cluster_view_access.slang"
#include "cluster_instance_properties.slang"
#include "resource_effects.slang"

ConstantBuffer<RenderInstancePropertyMetadata> InstanceProperties;
ByteAddressBuffer InstanceData;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWStructuredBuffer<RasterBinEntry> RasterBinMeta;
ConstantBuffer<RasterBinningUniforms> Uniforms;
StructuredBuffer<VisibleCluster> VisibleClusters;
ByteAddressBuffer DrawArgs;
ByteAddressBuffer ClusterReadOffsetArgs;
ByteAddressBuffer ClusterReadOffsetArgs1;
ByteAddressBuffer ClusterReadOffsetArgs2;
StructuredBuffer<uint> RasterSlots;
ByteAddressBuffer PageHeap;

[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWStructuredBuffer<uint> PolicyRasterWorkCounter;
#include "cluster_raster_target_view.slang"

void WriteRasterBatch(uint bin, uint visibleIndex, uint workIndex, bool isSoftware, bool depthBuckets)
{
    if (!IsBin(bin)) return;
    if (Uniforms.PolicyTarget != 0u)
    {
        uint4 pages = ClusterUnpackRasterTargetPageWindow(VisibleClusterRasterWindow(VisibleClusters[visibleIndex]));
        uint2 extent = pages.zw - pages.xy + 1u;
        // Page-batch area is invariant under partitioning. Software remains one
        // bounded geometry batch; do not lower resolution just for more HW windows.
        uint area = isSoftware ? 64u : extent.x * extent.y;
        InterlockedAdd(PolicyRasterWorkCounter[0], area);
    }
    CountRaster(RasterBinMeta, bin, isSoftware);
    if (depthBuckets)
    {
        uint ignored;
        InterlockedOr(RasterBinMeta[bin].DepthBucketEnabled, 1u, ignored);
        InterlockedAdd(DepthBuckets[bin * 512u + (isSoftware ? 0u : 256u) +
            VisibleClusterDepthBucket(VisibleClusters[visibleIndex])], 1u, ignored);
    }
}

#include "cluster_bin_batches.slang"
#include "cluster_work_status.slang"
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWStructuredBuffer<uint> DepthBuckets;

StructuredBuffer<ClusterRasterTargetView> WorkTargetViews;
#include "cluster_depth_cache.slang"
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWStructuredBuffer<uint> DepthWorkCounter;

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

    DrawRequest entry = VisibleClusterRequest(VisibleClusters[visibleIndex]);
    if (Uniforms.PolicyTarget == 0u && LoadInstanceXray(InstanceData, InstanceProperties, entry.InstanceID).Enabled != 0u) return;
    if (Uniforms.PolicyTarget != 0u)
    {
        ClusterRasterTargetView target = WorkTargetViews[ClusterPhysicalViewIndex(DrawRequestViewIndex(entry))];
        if ((target.Padding.y & 1u) != 0u)
            InterlockedAdd(PolicyRasterWorkCounter[1u + target.Padding.x * 2u + (isSoftware ? 1u : 0u)], 1u);
        if ((target.Padding.y & 2u) != 0u)
        {
            uint depthClass = LoadTargetDepthCacheClass(entry.InstanceID) == 1u ? 1u : 0u;
            InterlockedAdd(DepthWorkCounter[target.Padding.x * 4u + depthClass * 2u + (isSoftware ? 1u : 0u)], 1u);
        }
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
    uint clusterAddress = entry.PageOffset + PageHeap.Load(entry.PageOffset + 16u) +
        DrawRequestClusterID(entry) * PACKED_CLUSTER_STRIDE_BYTES;
    ClusterMaterialRanges materials = LoadClusterMaterialRanges(PageHeap, clusterAddress, entry.PageOffset);
    uint slotOffset = LoadClusterMaterialSlotOffset(
        InstanceData,
        LoadRenderInstanceMetadata(
            InstanceProperties,
            CLUSTER_MATERIAL_SLOT_OFFSET_METADATA_WORD),
        entry.InstanceID);
    BuildRasterBatches(
        visibleIndex,
        slotOffset,
        materials,
        vertexCount,
        triangleCount,
        isSoftware);
}
