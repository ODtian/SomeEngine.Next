#include "shade_bin_inputs.slang"
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWStructuredBuffer<uint> BinCounts;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWStructuredBuffer<ShadePixelWork> PixelCoordinates;

void CountShadeBinsCore<TInput : IShadeBinInput>(TInput input, uint3 group, uint lane)
{
    uint bin = input.ReadBin(group, lane);
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
    uint coordinate = input.WorkIndex(group, lane, fullTile);
    ShadePixelWork work;
    work.PixelIndex = coordinate;
    work.Mask = mask;
    PixelCoordinates[offset] = work;
}

[shader("compute")]
[numthreads(8, 8, 1)]
void CountShadeBins(uint3 group : SV_GroupID, uint lane : SV_GroupIndex)
{
    CountShadeBinsCore(VisibilityShadeBinInput(), group, lane);
}

[shader("compute")]
[numthreads(8, 8, 1)]
void CountLayerShadeBins(uint3 group : SV_GroupID, uint lane : SV_GroupIndex)
{
    uint tile = group.y * 65535u + group.x;
    if (tile >= LayerFragmentStore.TileCount) return;
    for (uint block = LayerTileHeads[tile]; block != LAYER_BLOCK_EMPTY;)
    {
        LayerBlockHeader header = LoadLayerBlock(block);
        for (uint record = 0u; record < header.Count; record += 64u)
        {
            LayerShadeBinInput input;
            input.BaseIndex = block * LayerFragmentStore.BlockSize + record;
            input.Count = min(header.Count - record, 64u);
            CountShadeBinsCore(input, group, lane);
            GroupMemoryBarrierWithGroupSync();
        }
        block = header.Next;
    }
}
