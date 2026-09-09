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

void CountRasterBatches(
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
    for (uint batch = 0u; batch < vrb.batchCount && triangleStart < triangleCount; batch++)
    {
        uint triangleEnd = min(triangleStart + vrb.triCounts[batch], triangleCount);
        uint material = LocalMaterial(packedMaterials, packedRanges, triangleStart);
        CountRaster(
            RasterBinMeta,
            RasterKey(RasterSlots, Uniforms, slotOffset, material),
            EmitsSw(isSoftware, batch, vrb.batchCount));
        triangleStart = triangleEnd;
    }

    while (triangleStart < triangleCount)
    {
        uint material = LocalMaterial(packedMaterials, packedRanges, triangleStart);
        CountRaster(
            RasterBinMeta,
            RasterKey(RasterSlots, Uniforms, slotOffset, material),
            false);
        triangleStart = min(
            triangleStart + 32u,
            MaterialEnd(packedRanges, triangleStart, triangleCount));
    }
}

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
    CountRasterBatches(
        slotOffset,
        vrbInfo,
        triangleCount,
        packedMaterials,
        packedRanges,
        isSoftware);
}
