#ifndef LAYER_FRAGMENT_STORE_SLANG
#define LAYER_FRAGMENT_STORE_SLANG

__exported import resource_effects;
__exported import wave_aggregation;
__exported import layer_fragment_types;

struct LayerFragmentStoreParameters
{
    uint TileCount;
    uint BlockSize;
    uint BlockCapacity;
    uint Padding;
};

ConstantBuffer<LayerFragmentStoreParameters> LayerFragmentStore;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.None)]
RWByteAddressBuffer LayerFragmentBlocks;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWStructuredBuffer<uint> LayerTileHeads;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.None)]
RWStructuredBuffer<LayerColor> LayerFragmentPayloads;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWStructuredBuffer<uint> LayerFrameCounters;

LayerBlockHeader LoadLayerBlock(uint block)
{
    uint3 data = LayerFragmentBlocks.Load3(GetLayerBlockByteOffset(block, LayerFragmentStore.BlockSize));
    LayerBlockHeader header;
    header.Tile = data.x;
    header.Next = data.y;
    header.Count = data.z;
    return header;
}

LayerFragment LoadLayerFragment(uint block, uint record)
{
    uint3 data = LayerFragmentBlocks.Load3(GetLayerFragmentByteOffset(block, record, LayerFragmentStore.BlockSize));
    LayerFragment fragment;
    fragment.Depth = asfloat(data.x);
    fragment.Identity = data.y;
    fragment.Packed = data.z;
    return fragment;
}

LayerFragment LoadLayerFragment(uint fragmentIndex)
{
    return LoadLayerFragment(fragmentIndex / LayerFragmentStore.BlockSize,
        fragmentIndex % LayerFragmentStore.BlockSize);
}

LayerColor LoadLayerColor(uint fragmentIndex) { return LayerFragmentPayloads[fragmentIndex]; }
void StoreLayerColor(uint fragmentIndex, float3 premultipliedColor, float3 transmittance)
{ LayerFragmentPayloads[fragmentIndex] = PackLayerColor(premultipliedColor, transmittance); }

void StoreLayerFragment(uint block, uint record, LayerFragment fragment)
{
    LayerFragmentBlocks.Store3(GetLayerFragmentByteOffset(block, record, LayerFragmentStore.BlockSize),
        uint3(asuint(fragment.Depth), fragment.Identity, fragment.Packed));
}

// One reservation covers the blocks needed by a wave's lanes belonging to one tile.
uint AllocateLayerFragmentBlocks(uint tile, uint blockCount, uint fragmentCount)
{
    uint first;
    InterlockedAdd(LayerFrameCounters[LAYER_FRAME_BLOCK_COUNTER], blockCount, first);
    for (uint offset = 0u; offset < blockCount && first + offset < LayerFragmentStore.BlockCapacity; offset++)
    {
        uint block = first + offset;
        uint address = GetLayerBlockByteOffset(block, LayerFragmentStore.BlockSize);
        uint count = min(LayerFragmentStore.BlockSize, fragmentCount);
        LayerFragmentBlocks.Store3(address, uint3(tile, LAYER_BLOCK_EMPTY, count));
        uint next;
        InterlockedExchange(LayerTileHeads[tile], block, next);
        LayerFragmentBlocks.Store(address + 4u, next);
        fragmentCount -= count;
    }
    return first;
}

interface ILayerFragmentStore
{
    static void Begin(uint lane);
    static uint Append(uint tile, LayerFragment fragment, uint waveIndex);
    static void End(uint lane);
};

struct LayerSoftwareFragmentStore : ILayerFragmentStore
{
    // Each wave uses the row named by its first lane's group index.
    static groupshared uint Blocks[64];
    static groupshared uint RecordCounts[64];
    static groupshared uint StoredFragments[64];
    static groupshared uint DroppedFragments[64];

    static void Begin(uint lane)
    {
        Blocks[lane] = LAYER_BLOCK_EMPTY;
        RecordCounts[lane] = LayerFragmentStore.BlockSize;
        StoredFragments[lane] = DroppedFragments[lane] = 0u;
        GroupMemoryBarrierWithGroupSync();
    }

    // Only the first active lane updates its wave's row.
    static uint3 TakeRange(uint tile, uint requested, uint waveIndex)
    {
        uint block = Blocks[waveIndex];
        uint count = RecordCounts[waveIndex];
        if (count == LayerFragmentStore.BlockSize)
        {
            if (block != LAYER_BLOCK_EMPTY)
                LayerFragmentBlocks.Store(GetLayerBlockByteOffset(block, LayerFragmentStore.BlockSize) + 8u,
                    LayerFragmentStore.BlockSize);
            uint allocated = AllocateLayerFragmentBlocks(tile, 1u, 0u);
            block = allocated < LayerFragmentStore.BlockCapacity ? allocated : LAYER_BLOCK_EMPTY;
            count = 0u;
        }
        uint accepted = block == LAYER_BLOCK_EMPTY
            ? requested : min(requested, LayerFragmentStore.BlockSize - count);
        Blocks[waveIndex] = block;
        RecordCounts[waveIndex] = block == LAYER_BLOCK_EMPTY ? 0u : count + accepted;
        if (block == LAYER_BLOCK_EMPTY) DroppedFragments[waveIndex] += accepted;
        else StoredFragments[waveIndex] += accepted;
        GroupMemoryBarrier();
        return uint3(block, count, accepted);
    }

    static uint Append(uint tile, LayerFragment fragment, uint waveIndex)
    {
        uint waveCount = WaveActiveCountBits(true);
        uint rank = WavePrefixCountBits(true);
        uint processed = 0u;
        uint fragmentIndex = LAYER_BLOCK_EMPTY;
        while (processed < waveCount)
        {
            uint3 range = uint3(0u);
            if (WaveIsFirstLane())
                range = TakeRange(tile, waveCount - processed, waveIndex);
            range = WaveReadLaneFirst(range);
            if (rank >= processed && rank < processed + range.z && range.x != LAYER_BLOCK_EMPTY)
            {
                uint record = range.y + rank - processed;
                StoreLayerFragment(range.x, record, fragment);
                fragmentIndex = GetLayerFragmentIndex(range.x, record, LayerFragmentStore.BlockSize);
            }
            processed += range.z;
        }
        return fragmentIndex;
    }

    static void End(uint lane)
    {
        AllMemoryBarrierWithGroupSync();
        uint waveIndex = WaveReadLaneFirst(lane);
        if (WaveIsFirstLane())
        {
            uint block = Blocks[waveIndex];
            if (block != LAYER_BLOCK_EMPTY)
                LayerFragmentBlocks.Store(GetLayerBlockByteOffset(block, LayerFragmentStore.BlockSize) + 8u,
                    RecordCounts[waveIndex]);
            if (StoredFragments[waveIndex] != 0u)
                InterlockedAdd(LayerFrameCounters[LAYER_FRAME_FRAGMENTS], StoredFragments[waveIndex]);
            if (DroppedFragments[waveIndex] != 0u)
                InterlockedAdd(LayerFrameCounters[LAYER_FRAME_OVERFLOW], DroppedFragments[waveIndex]);
        }
    }
};

struct LayerHardwareFragmentStore : ILayerFragmentStore
{
    static void Begin(uint lane) { }
    static void End(uint lane) { }

    static uint Append(uint tile, LayerFragment fragment, uint waveIndex)
    {
        uint4 matching = WaveMatchingLanes(tile);
        uint leader = WaveMaskFirstLane(matching);
        uint rank = WaveMaskPrefix(matching, WaveGetLaneIndex());
        uint count = WaveMaskBitCount(matching);
        uint first = 0u;
        if (WaveGetLaneIndex() == leader)
        {
            uint blockCount = (count + LayerFragmentStore.BlockSize - 1u) / LayerFragmentStore.BlockSize;
            first = AllocateLayerFragmentBlocks(tile, blockCount, count);
            uint available = first < LayerFragmentStore.BlockCapacity
                ? min(blockCount, LayerFragmentStore.BlockCapacity - first) : 0u;
            uint stored = min(count, available * LayerFragmentStore.BlockSize);
            if (stored != 0u) InterlockedAdd(LayerFrameCounters[LAYER_FRAME_FRAGMENTS], stored);
            if (stored != count) InterlockedAdd(LayerFrameCounters[LAYER_FRAME_OVERFLOW], count - stored);
        }
        first = WaveReadLaneAt(first, leader);
        uint block = first + rank / LayerFragmentStore.BlockSize;
        if (block >= LayerFragmentStore.BlockCapacity) return LAYER_BLOCK_EMPTY;
        uint record = rank % LayerFragmentStore.BlockSize;
        StoreLayerFragment(block, record, fragment);
        return GetLayerFragmentIndex(block, record, LayerFragmentStore.BlockSize);
    }
};

[shader("compute")]
[numthreads(256, 1, 1)]
void LayerClearFragmentStore(uint3 id : SV_DispatchThreadID)
{
    uint index = id.x + id.y * 65535u * 256u;
    if (index < LayerFragmentStore.TileCount) LayerTileHeads[index] = LAYER_BLOCK_EMPTY;
    if (index < LAYER_FRAME_COUNTER_COUNT)
        LayerFrameCounters[index] = index == LAYER_FRAME_SOURCE_BLOCK_CAPACITY ? LayerFragmentStore.BlockCapacity : 0u;
}

#endif
