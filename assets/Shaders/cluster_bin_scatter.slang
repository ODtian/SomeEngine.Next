#include "cluster_bin_common.slang"
#include "cluster_instance_properties.slang"
#include "resource_effects.slang"

ConstantBuffer<RenderInstancePropertyMetadata> InstanceProperties;
ByteAddressBuffer InstanceData;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWStructuredBuffer<RasterBinEntry> RasterBinMeta;
ConstantBuffer<RasterBinningUniforms> Uniforms;
StructuredBuffer<VisibleCluster> VisibleClusters;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWStructuredBuffer<uint> BinnedClusterIndexBuffer;
ByteAddressBuffer DrawArgs;
ByteAddressBuffer ClusterReadOffsetArgs;
ByteAddressBuffer ClusterReadOffsetArgs1;
ByteAddressBuffer ClusterReadOffsetArgs2;
StructuredBuffer<uint> RasterSlots;
ByteAddressBuffer PageHeap;

void WriteRasterBatch(uint bin, uint visibleIndex, uint workIndex, bool isSoftware, bool depthBuckets)
{
    if (!ScatterRaster(RasterBinMeta, BinnedClusterIndexBuffer, bin, visibleIndex, workIndex, isSoftware, DepthBuckets, VisibleClusterDepthBucket(VisibleClusters[visibleIndex])))
        RejectClusterWork(16u);
}

#include "cluster_bin_batches.slang"
#include "cluster_work_status.slang"
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWStructuredBuffer<uint> DepthBuckets;


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

    DrawRequest entry = VisibleClusterRequest(VisibleClusters[visibleIndex]);
    if (Uniforms.PolicyTarget == 0u && LoadInstanceXray(InstanceData, InstanceProperties, entry.InstanceID).Enabled != 0u) return;
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
