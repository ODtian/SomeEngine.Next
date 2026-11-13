#include "shade_bin_inputs.slang"
[ResourceEffect(ResourceEffects.Read, ResourceQualifiers.None)]
RWStructuredBuffer<uint> BinOffsets;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWStructuredBuffer<uint> ScatterCounts;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWStructuredBuffer<ShadePixelWork> PixelCoordinates;

void ScatterShadeBinsCore<TInput : IShadeBinInput>(TInput input, uint3 group, uint lane)
{
    uint bin = input.ReadBin(group, lane);
    uint mask;
    bool fullTile;
    if (!ClassifyShadeTile(bin, lane, mask, fullTile)) return;
    uint counter = bin * 2u + (fullTile ? 0u : 1u);
    uint coordinate = input.WorkIndex(group, lane, fullTile);
    uint4 matching = WaveMatchingLanes(counter);
    uint waveLane = WaveGetLaneIndex();
    uint leader = WaveMaskFirstLane(matching);
    uint writeBase = 0u;
    if (waveLane == leader)
        InterlockedAdd(ScatterCounts[counter], WaveMaskBitCount(matching), writeBase);
    writeBase = WaveReadLaneAt(writeBase, leader);
    ShadePixelWork work;
    work.PixelIndex = coordinate;
    work.Mask = mask;
    PixelCoordinates[BinOffsets[counter] + writeBase + WaveMaskPrefix(matching, waveLane)] = work;
}

[shader("compute")]
[numthreads(8, 8, 1)]
void ScatterShadeBins(uint3 group : SV_GroupID, uint lane : SV_GroupIndex)
{
    ScatterShadeBinsCore(VisibilityShadeBinInput(), group, lane);
}

[shader("compute")]
[numthreads(8, 8, 1)]
void ScatterLayerShadeBins(uint3 group : SV_GroupID, uint lane : SV_GroupIndex)
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
            ScatterShadeBinsCore(input, group, lane);
            GroupMemoryBarrierWithGroupSync();
        }
        block = header.Next;
    }
}
