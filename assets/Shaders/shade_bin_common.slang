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
    uint BinItemCapacity;
    uint Pad1;
    uint Pad2;
    uint Pad3;
};

uint FindShadeBin(
    StructuredBuffer<uint> slots,
    ByteAddressBuffer pageHeap,
    ShadeBinUniforms uniforms,
    uint slotOffset,
    DrawRequest cluster,
    uint triangle)
{
    uint clusterOffset =
        cluster.PageOffset + pageHeap.Load(cluster.PageOffset + 16u) +
        DrawRequestClusterID(cluster) * PACKED_CLUSTER_STRIDE_BYTES;
    uint material = LocalMaterial(
        pageHeap.Load(clusterOffset + 68u),
        pageHeap.Load(clusterOffset + 72u),
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

groupshared uint ShadeTileBins[64];
groupshared uint ShadeTileMixed;

// The tile is row-major; each 2x2 quad has a single representative for each bin.
// A full tile becomes one work item. Boundary quads retain their exact write mask.
bool ClassifyShadeTile(uint bin, uint lane, out uint mask, out bool fullTile)
{
    if (lane == 0u) ShadeTileMixed = 0u;
    ShadeTileBins[lane] = bin;
    GroupMemoryBarrierWithGroupSync();
    bool mixed = WaveActiveAnyTrue(bin != ShadeTileBins[0]);
    if (WaveIsFirstLane() && mixed) InterlockedOr(ShadeTileMixed, 1u);
    GroupMemoryBarrierWithGroupSync();
    fullTile = ShadeTileMixed == 0u;
    mask = 0u;
    if (!IsBin(bin)) return false;
    if (fullTile) return lane == 0u;
    uint quadBase = lane & ~9u;
    uint corner = (lane & 1u) | ((lane >> 2u) & 2u);
    uint first = 4u;
    [unroll]
    for (uint i = 0u; i < 4u; i++)
    {
        uint other = quadBase + (i & 1u) + ((i >> 1u) * 8u);
        if (ShadeTileBins[other] == bin)
        {
            mask |= 1u << i;
            first = min(first, i);
        }
    }
    return corner == first;
}

#endif
