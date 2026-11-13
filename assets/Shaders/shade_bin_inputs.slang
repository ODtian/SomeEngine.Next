#ifndef SHADE_BIN_INPUTS_SLANG
#define SHADE_BIN_INPUTS_SLANG

#include "shade_bin_common.slang"
__exported import layer_fragment_store;

ConstantBuffer<RenderInstancePropertyMetadata> InstanceProperties;
ByteAddressBuffer InstanceData;
ConstantBuffer<ShadeBinUniforms> Uniforms;
Texture2DArray<uint> VisBuffer;
StructuredBuffer<VisibleCluster> VisibleClusters;
StructuredBuffer<uint> ShadeSlots;
ByteAddressBuffer PageHeap;

interface IShadeBinInput
{
    uint ReadBin(uint3 group, uint lane);
    uint WorkIndex(uint3 group, uint lane, bool fullTile);
};

uint ReadVisibilityShadeBin(uint visibility, uint view, out uint materialAddress)
{
    materialAddress = 0xffffffffu;
    if (IsVisBufferEmpty(visibility)) return 0xffffffffu;
    DrawRequest cluster = VisibleClusterRequest(VisibleClusters[DecodeVisBufferVisibleClusterIndex(visibility)]);
    if (DrawRequestViewIndex(cluster) != view) return 0xffffffffu;
    uint offset = MaterialSlotOffset(InstanceData, InstanceProperties, cluster.InstanceID);
    return FindShadeBin(ShadeSlots, PageHeap, Uniforms, offset, cluster,
        DecodeVisBufferTriangleID(visibility), materialAddress);
}

struct VisibilityShadeBinInput : IShadeBinInput
{
    uint ReadBin(uint3 group, uint lane)
    {
        uint3 pixel = uint3(group.xy * 8u + uint2(lane & 7u, lane >> 3u), group.z);
        if (pixel.x >= Uniforms.ScreenWidth || pixel.y >= Uniforms.ScreenHeight) return 0xffffffffu;
        uint materialAddress;
        return ReadVisibilityShadeBin(VisBuffer[pixel], pixel.z, materialAddress);
    }

    uint WorkIndex(uint3 group, uint lane, bool fullTile)
    {
        uint2 pixel = group.xy * 8u;
        if (!fullTile) pixel += uint2(lane & 6u, (lane >> 3u) & 6u);
        return (group.z * Uniforms.ScreenHeight + pixel.y) * Uniforms.ScreenWidth + pixel.x;
    }
};

struct LayerShadeBinInput : IShadeBinInput
{
    uint BaseIndex;
    uint Count;

    uint ReadBin(uint3 group, uint lane)
    {
        if (lane >= Count) return 0xffffffffu;
        uint index = BaseIndex + lane;
        LayerFragment fragment = LoadLayerFragment(index);
        DrawRequest cluster = VisibleClusterRequest(VisibleClusters[DecodeVisBufferVisibleClusterIndex(fragment.Identity)]);
        uint materialAddress;
        return ReadVisibilityShadeBin(fragment.Identity, DrawRequestViewIndex(cluster), materialAddress);
    }

    uint WorkIndex(uint3 group, uint lane, bool fullTile)
    {
        return BaseIndex + (fullTile ? 0u : lane & ~9u);
    }
};

#endif
