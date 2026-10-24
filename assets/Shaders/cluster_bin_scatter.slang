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

void WriteRasterBatch(uint bin, uint visibleIndex, uint workIndex, bool isSoftware)
{
    ScatterRaster(RasterBinMeta, BinnedClusterIndexBuffer, bin, visibleIndex, workIndex, isSoftware);
}

#include "cluster_bin_batches.slang"

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
