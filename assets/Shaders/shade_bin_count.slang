#include "shade_bin_common.slang"
#include "resource_effects.slang"

ConstantBuffer<RenderInstancePropertyMetadata> InstanceProperties;
ByteAddressBuffer InstanceData;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWStructuredBuffer<uint> BinCounts;
ConstantBuffer<ShadeBinUniforms> Uniforms;
Texture2DArray<uint> VisBuffer;
StructuredBuffer<DrawRequest> VisibleClusters;
StructuredBuffer<uint> ShadeSlots;
ByteAddressBuffer PageHeap;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWStructuredBuffer<uint2> PixelCoordinates;

[shader("compute")]
[numthreads(8, 8, 1)]
void CountShadeBins(uint3 threadId : SV_DispatchThreadID, uint lane : SV_GroupIndex)
{
    uint bin = 0xFFFFFFFFu;
    if (threadId.x < Uniforms.ScreenWidth && threadId.y < Uniforms.ScreenHeight)
    {
        uint visibility = VisBuffer[threadId];
        if (!IsVisBufferEmpty(visibility))
        {
            DrawRequest cluster = VisibleClusters[DecodeVisBufferVisibleClusterIndex(visibility)];
            if (DrawRequestViewIndex(cluster) == threadId.z)
            {
                uint slotOffset = MaterialSlotOffset(InstanceData, InstanceProperties, cluster.InstanceID);
                bin = FindShadeBin(ShadeSlots, PageHeap, Uniforms, slotOffset, cluster,
                    DecodeVisBufferTriangleID(visibility));
            }
        }
    }
    uint mask;
    bool fullTile;
    if (!ClassifyShadeTile(bin, lane, mask, fullTile)) return;
    uint counter = bin * 2u + (fullTile ? 0u : 1u);
    if (Uniforms.BinItemCapacity == 0u)
    {
        CountShade(BinCounts, counter);
        return;
    }
    uint4 matching = WaveMatchingLanes(counter);
    uint waveLane = WaveGetLaneIndex();
    uint leader = WaveMaskFirstLane(matching);
    uint first = 0u;
    if (waveLane == leader)
        InterlockedAdd(BinCounts[counter], WaveMaskBitCount(matching), first);
    uint item = WaveReadLaneAt(first, leader) + WaveMaskPrefix(matching, waveLane);
    // Full tiles grow from the front; partial quads grow from the back.
    // Each tile replaces sixteen quads, so the two ranges cannot overlap.
    uint offset = bin * Uniforms.BinItemCapacity +
        (fullTile ? item : Uniforms.BinItemCapacity - 1u - item);
    uint2 topLeft = fullTile ? threadId.xy & ~7u.xx : threadId.xy & ~1u.xx;
    uint coordinate = (threadId.z * Uniforms.ScreenHeight + topLeft.y) * Uniforms.ScreenWidth + topLeft.x;
    PixelCoordinates[offset] = uint2(coordinate, mask);
}
