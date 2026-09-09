#include "shade_bin_common.slang"
#include "resource_effects.slang"

ConstantBuffer<RenderInstancePropertyMetadata> InstanceProperties;
ByteAddressBuffer InstanceData;
ConstantBuffer<ShadeBinUniforms> Uniforms;
Texture2DArray<uint> VisBuffer;
[ResourceEffect(ResourceEffects.Read, ResourceQualifiers.None)]
RWStructuredBuffer<uint> BinOffsets;
StructuredBuffer<DrawRequest> VisibleClusters;
StructuredBuffer<uint> ShadeSlots;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWStructuredBuffer<uint> ScatterCounts;
ByteAddressBuffer PageHeap;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWStructuredBuffer<uint2> PixelCoordinates;

[shader("compute")]
[numthreads(8, 8, 1)]
void ScatterShadeBins(uint3 threadId : SV_DispatchThreadID, uint lane : SV_GroupIndex)
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
    uint2 topLeft = fullTile ? threadId.xy & ~7u.xx : threadId.xy & ~1u.xx;
    uint coordinate = (threadId.z * Uniforms.ScreenHeight + topLeft.y) * Uniforms.ScreenWidth + topLeft.x;
    uint4 matching = WaveMatchingLanes(counter);
    uint waveLane = WaveGetLaneIndex();
    uint leader = WaveMaskFirstLane(matching);
    uint writeBase = 0u;
    if (waveLane == leader)
        InterlockedAdd(ScatterCounts[counter], WaveMaskBitCount(matching), writeBase);
    writeBase = WaveReadLaneAt(writeBase, leader);
    PixelCoordinates[BinOffsets[counter] + writeBase + WaveMaskPrefix(matching, waveLane)] =
        uint2(coordinate, mask);
}
