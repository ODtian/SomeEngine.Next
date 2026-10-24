#include "shade_bin_common.slang"
#include "resource_effects.slang"
#include "surface_receiver.slang"

ConstantBuffer<RenderInstancePropertyMetadata> InstanceProperties;
ByteAddressBuffer InstanceData;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWStructuredBuffer<uint> BinCounts;
ConstantBuffer<ShadeBinUniforms> Uniforms;
Texture2DArray<uint> VisBuffer;
StructuredBuffer<VisibleCluster> VisibleClusters;
StructuredBuffer<uint> ShadeSlots;
ByteAddressBuffer PageHeap;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWStructuredBuffer<ShadePixelWork> PixelCoordinates;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWStructuredBuffer<SurfaceReceiver> SurfaceReceivers;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWStructuredBuffer<SurfaceReceiver> AdditionalSurfaceReceivers;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWStructuredBuffer<uint> SurfaceCounts;


void CountShadeBinsCore<let writeReceivers : bool>(uint3 threadId, uint lane)
{
    uint bin = 0xFFFFFFFFu;
    if (threadId.x < Uniforms.ScreenWidth && threadId.y < Uniforms.ScreenHeight)
    {
        if (writeReceivers)
        {
            uint receiverIndex = (threadId.z * Uniforms.ScreenHeight + threadId.y) * Uniforms.ScreenWidth + threadId.x;
            SurfaceReceivers[receiverIndex] = (SurfaceReceiver)0;
            uint receiverLayerSize = Uniforms.ScreenWidth * Uniforms.ScreenHeight * Uniforms.ViewCount;
            for (uint layer = 0u; layer < Uniforms.AdditionalReceiverLayerCount; layer++)
                AdditionalSurfaceReceivers[layer * receiverLayerSize + receiverIndex] = (SurfaceReceiver)0;
            if (Uniforms.AdditionalReceiverLayerCount == 0u && receiverIndex == 0u)
                AdditionalSurfaceReceivers[0] = (SurfaceReceiver)0;
        }
        uint visibility = VisBuffer[threadId];
        if (!IsVisBufferEmpty(visibility))
        {
            DrawRequest cluster = VisibleClusterRequest(VisibleClusters[DecodeVisBufferVisibleClusterIndex(visibility)]);
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
    uint surfaceIndex = 0u;
    if (Uniforms.SurfaceDataStride != 0u)
        InterlockedAdd(SurfaceCounts[bin], fullTile ? 64u : countbits(mask), surfaceIndex);
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
    ShadePixelWork work;
    work.PixelIndex = coordinate;
    work.Mask = mask;
    work.SurfaceDataIndex = surfaceIndex;
    PixelCoordinates[offset] = work;
}

[shader("compute")]
[numthreads(8, 8, 1)]
void CountShadeBins(uint3 threadId : SV_DispatchThreadID, uint lane : SV_GroupIndex)
{
    CountShadeBinsCore<true>(threadId, lane);
}

[shader("compute")]
[numthreads(8, 8, 1)]
void CountShadeBinsWithoutReceivers(uint3 threadId : SV_DispatchThreadID, uint lane : SV_GroupIndex)
{
    CountShadeBinsCore<false>(threadId, lane);
}
