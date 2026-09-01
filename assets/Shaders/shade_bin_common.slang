#ifndef SHADE_BIN_COMMON_SLANG
#define SHADE_BIN_COMMON_SLANG

#include "cluster_structures.slang"
#include "cluster_instance_properties.slang"
#include "cluster_bin_io.slang"
#include "visbuffer_encoding.slang"

struct ShadeBinUniforms
{
    uint ScreenWidth;
    uint ScreenHeight;
    uint ShadeBinCount;
    uint SlotCapacity;
    uint Pad0;
    uint Pad1;
    uint Pad2;
    uint Pad3;
};

uint FindShadeBin(
    StructuredBuffer<uint> slots,
    ByteAddressBuffer pageHeap,
    ShadeBinUniforms uniforms,
    uint slotOffset,
    uint4 cluster,
    uint triangle)
{
    uint clusterOffset =
        cluster.x + pageHeap.Load(cluster.x + 16u) + cluster.y * GPU_CLUSTER_STRIDE_BYTES;
    uint material = LocalMaterial(
        pageHeap.Load(clusterOffset + 48u),
        pageHeap.Load(clusterOffset + 52u),
        triangle);
    return SlotBin(
        slots,
        slotOffset,
        material,
        uniforms.SlotCapacity,
        uniforms.ShadeBinCount);
}

uint MaterialSlotOffset(
    ByteAddressBuffer instanceData,
    ConstantBuffer<RenderInstancePropertyMetadata> properties,
    uint instanceId)
{
    return LoadClusterMaterialSlotOffset(
        instanceData,
        LoadRenderInstanceMetadata(
            properties,
            CLUSTER_MATERIAL_SLOT_OFFSET_METADATA_WORD),
        instanceId);
}

#endif
