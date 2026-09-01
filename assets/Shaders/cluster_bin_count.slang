#include "cluster_bin_common.slang"
#include "cluster_instance_properties.slang"
#include "resource_effects.slang"

ConstantBuffer<RenderInstancePropertyMetadata> InstanceProperties;
ByteAddressBuffer InstanceData;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWStructuredBuffer<RasterBinEntry> RasterBinMeta;
ConstantBuffer<RasterDeformBinningUniforms> Uniforms;
StructuredBuffer<uint4> VisibleClusters;
ByteAddressBuffer DrawArgs;
ByteAddressBuffer ClusterReadOffsetArgs;
StructuredBuffer<uint> RasterSlots;
StructuredBuffer<uint> DeformSlots;
ByteAddressBuffer PageHeap;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWStructuredBuffer<DeformBinEntry> DeformBinMeta;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWStructuredBuffer<uint> CacheOffsetsWrite;

static const uint CACHE_OFFSET_OVERFLOW = 0xFFFFFFFDu;

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

void CountDeformCluster(
    uint slotOffset,
    uint triangleCount,
    uint deformMaterialIndex)
{
    if (triangleCount == 0u)
        return;
    CountDeform(
        DeformBinMeta,
        DeformKey(DeformSlots, Uniforms, slotOffset, deformMaterialIndex));
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
        Uniforms.MaxVisibleClusters,
        threadId.x,
        visibleIndex,
        isSoftware);

    uint4 entry = VisibleClusters[visibleIndex];
    CacheOffsetsWrite[visibleIndex] = CACHE_OFFSET_OVERFLOW;
    uint vrbInfo;
    uint triangleCount;
    uint deformMaterialIndex;
    uint packedMaterials;
    uint packedRanges;
    ReadClusterBinData(
        PageHeap,
        entry,
        vrbInfo,
        triangleCount,
        deformMaterialIndex,
        packedMaterials,
        packedRanges);
    uint slotOffset = LoadClusterMaterialSlotOffset(
        InstanceData,
        LoadRenderInstanceMetadata(
            InstanceProperties,
            CLUSTER_MATERIAL_SLOT_OFFSET_METADATA_WORD),
        entry.z);
    CountRasterBatches(
        slotOffset,
        vrbInfo,
        triangleCount,
        packedMaterials,
        packedRanges,
        isSoftware);
    CountDeformCluster(slotOffset, triangleCount, deformMaterialIndex);
}
