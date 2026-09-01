#include "cluster_bin_common.slang"
#include "cluster_instance_properties.slang"
#include "resource_effects.slang"

ConstantBuffer<RenderInstancePropertyMetadata> InstanceProperties;
ByteAddressBuffer InstanceData;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWStructuredBuffer<RasterBinEntry> RasterBinMeta;
ConstantBuffer<RasterDeformBinningUniforms> Uniforms;
StructuredBuffer<uint4> VisibleClusters;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWStructuredBuffer<uint3> BinnedClusterIndexBuffer;
ByteAddressBuffer DrawArgs;
ByteAddressBuffer ClusterReadOffsetArgs;
StructuredBuffer<uint> RasterSlots;
StructuredBuffer<uint> DeformSlots;
ByteAddressBuffer PageHeap;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWStructuredBuffer<DeformBinEntry> DeformBinMeta;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWStructuredBuffer<uint3> DeformBinnedClusterIndexBuffer;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWStructuredBuffer<uint> CacheOffsetsWrite;

static const uint CACHE_OFFSET_UNINITIALIZED = 0xFFFFFFFEu;

void ScatterRasterBatches(
    uint visibleIndex,
    uint viewIndex,
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
        ScatterRaster(
            RasterBinMeta,
            BinnedClusterIndexBuffer,
            RasterKey(RasterSlots, Uniforms, slotOffset, material),
            visibleIndex,
            viewIndex,
            triangleStart,
            triangleEnd,
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
            viewIndex,
            triangleStart,
            triangleEnd,
            false);
        triangleStart = triangleEnd;
    }
}

void ScatterDeformCluster(
    uint visibleIndex,
    uint viewIndex,
    uint slotOffset,
    uint triangleCount,
    uint deformMaterialIndex)
{
    if (triangleCount == 0u)
        return;

    ScatterDeform(
        DeformBinMeta,
        DeformBinnedClusterIndexBuffer,
        CacheOffsetsWrite,
        DeformKey(DeformSlots, Uniforms, slotOffset, deformMaterialIndex),
        visibleIndex,
        viewIndex,
        CACHE_OFFSET_UNINITIALIZED);
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
        Uniforms.MaxVisibleClusters,
        threadId.x,
        visibleIndex,
        isSoftware);

    uint4 entry = VisibleClusters[visibleIndex];
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
    ScatterRasterBatches(
        visibleIndex,
        entry.w,
        slotOffset,
        vrbInfo,
        triangleCount,
        packedMaterials,
        packedRanges,
        isSoftware);
    ScatterDeformCluster(
        visibleIndex,
        entry.w,
        slotOffset,
        triangleCount,
        deformMaterialIndex);
}
