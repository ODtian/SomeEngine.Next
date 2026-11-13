__exported import cluster_bin_common;
#include "cluster_instance_properties.slang"
__exported import resource_effects;

ConstantBuffer<RenderInstancePropertyMetadata> InstanceProperties;
ByteAddressBuffer InstanceData;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWStructuredBuffer<RasterBinEntry> RasterBinMeta;
ConstantBuffer<RasterBinningUniforms> Uniforms;
StructuredBuffer<VisibleCluster> VisibleClusters;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWStructuredBuffer<uint> BinnedClusterIndexBuffer;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWStructuredBuffer<uint> RasterSpillClusters;
ByteAddressBuffer DrawArgs;
ByteAddressBuffer ClusterReadOffsetArgs;
ByteAddressBuffer ClusterReadOffsetArgs1;
ByteAddressBuffer ClusterReadOffsetArgs2;
StructuredBuffer<uint> RasterSlots;
ByteAddressBuffer PageHeap;

bool WriteRasterBatch(uint bin, uint visibleIndex, uint workIndex, bool isSoftware, bool depthBuckets)
{
    bool stored = ScatterRaster(RasterBinMeta, BinnedClusterIndexBuffer, bin, visibleIndex, workIndex,
        isSoftware, DepthBuckets, VisibleClusterDepthBucket(VisibleClusters[visibleIndex]));
    if (!stored)
        RejectClusterWork(16u);
    return stored;
}

#include "cluster_bin_batches.slang"
__exported import cluster_work_status;
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

    RasterSpillClusters[visibleIndex] = 0xffffffffu;

    DrawRequest entry = VisibleClusterRequest(VisibleClusters[visibleIndex]);
    uint4 vertReuseBatchWords;
    uint vertexCount;
    uint triangleCount;
    uint3 packedMaterials;
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
    bool spilled = BuildRasterBatches(
        visibleIndex,
        slotOffset,
        materials,
        vertexCount,
        triangleCount,
        isSoftware);
    if (spilled) RasterSpillClusters[visibleIndex] = visibleIndex;
}
