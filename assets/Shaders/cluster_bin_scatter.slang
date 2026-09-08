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
    uint vrbInfo,
    uint triangleCount,
    uint packedMaterials,
    uint packedRanges,
    bool isSoftware)
{
    if (triangleCount == 0u)
        return;

    VRBDecoded vrb = DecodeVRB(vrbInfo);
    uint triangleStart = 0u;
    uint batch = 0u;
    for (; batch < vrb.batchCount && triangleStart < triangleCount; batch++)
    {
        uint triangleEnd = min(triangleStart + vrb.triCounts[batch], triangleCount);
        uint material = LocalMaterial(packedMaterials, packedRanges, triangleStart);
        ScatterRaster(
            RasterBinMeta,
            BinnedClusterIndexBuffer,
            RasterKey(RasterSlots, Uniforms, slotOffset, material),
            visibleIndex,
            batch,
            EmitsSw(isSoftware, batch, vrb.batchCount));
        triangleStart = triangleEnd;
    }

    while (triangleStart < triangleCount)
    {
        uint triangleEnd = min(
            triangleStart + 32u,
            MaterialEnd(packedRanges, triangleStart, triangleCount));
        uint material = LocalMaterial(packedMaterials, packedRanges, triangleStart);
        ScatterRaster(
            RasterBinMeta,
            BinnedClusterIndexBuffer,
            RasterKey(RasterSlots, Uniforms, slotOffset, material),
            visibleIndex,
            batch,
            false);
        batch++;
        triangleStart = triangleEnd;
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
    uint vrbInfo;
    uint triangleCount;
    uint packedMaterials;
    uint packedRanges;
    ReadClusterBinData(
        PageHeap,
        entry,
        vrbInfo,
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
        vrbInfo,
        triangleCount,
        packedMaterials,
        packedRanges,
        isSoftware);
}
