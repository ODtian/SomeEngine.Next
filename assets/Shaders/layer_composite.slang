__exported import resource_effects;
__exported import layer_fragment_types;
__exported import layer_operations;
__exported import layer_fragment_sort;
__exported import layer_composition_parameters;
__exported import fragment_material;
#include "generated/render_instance_layout.slang"
ByteAddressBuffer LayerFragmentBlocks;
StructuredBuffer<LayerColor> LayerFragmentPayloads;
StructuredBuffer<uint> LayerTileHeads;
ByteAddressBuffer PageHeap;
StructuredBuffer<VisibleCluster> VisibleClusters;
ByteAddressBuffer InstanceData;
ConstantBuffer<RenderInstancePropertyMetadata> InstanceProperties;
StructuredBuffer<uint2> MaterialLayers;
StructuredBuffer<LayerOperation> LayerOperations;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWStructuredBuffer<uint> LayerFrameCounters;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.None)]
RWStructuredBuffer<uint> LayerSortScratch;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.None)]
RWStructuredBuffer<uint> LayerMergeScratch;
Texture2DArray<float4> LayerOpaqueColor;
Texture2DArray<float> LayerOpaqueDepth;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.None)]
RWTexture2DArray<float4> LayerAccumulatorColor;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.None)]
RWTexture2DArray<float> LayerAccumulatorDepth;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.None)]
RWTexture2DArray<float> LayerWorkingOpaqueDepth;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.None)]
RWTexture2DArray<uint> LayerStencil;

static const uint LAYER_COMPOSITE_THREADS = 256u;
static const uint LAYER_SHARED_RECORD_CAPACITY = 1024u;
groupshared LayerSortRecord LayerSharedRecords[1024];
groupshared uint LayerPixelCounts[1024];
groupshared uint LayerPixelOffsets[1024];
groupshared uint LayerPixelWrites[1024];
groupshared uint LayerTileRecordCount;
groupshared uint LayerTileMaxPixelCount;
groupshared uint LayerNextPixel;
groupshared uint LayerSegmentBegin;
groupshared uint LayerSegmentEnd;
groupshared uint LayerSegmentCount;
groupshared uint LayerSlowPixel;
groupshared uint LayerSlowBase;
groupshared uint LayerScratchWriteCount;

LayerBlockHeader ReadCompositeLayerBlock(uint block)
{
    uint3 words = LayerFragmentBlocks.Load3(GetLayerBlockByteOffset(block, LayerCompositionUniforms.BlockSize));
    LayerBlockHeader result = { words.x, words.y, words.z };
    return result;
}

LayerSortRecord ReadCompositeLayerRecord(uint block, uint record)
{
    uint3 words = LayerFragmentBlocks.Load3(GetLayerFragmentByteOffset(block, record, LayerCompositionUniforms.BlockSize));
    LayerSortRecord result;
    result.Fragment.Depth = asfloat(words.x);
    result.Fragment.Identity = words.y;
    result.Fragment.Packed = words.z;
    result.PayloadIndex = GetLayerFragmentIndex(block, record, LayerCompositionUniforms.BlockSize);
    return result;
}

LayerSortRecord ReadCompositeLayerRecord(uint fragmentIndex)
{
    return ReadCompositeLayerRecord(fragmentIndex / LayerCompositionUniforms.BlockSize,
        fragmentIndex % LayerCompositionUniforms.BlockSize);
}

uint3 LayerCompositePixel(uint tile, uint localPixel)
{
    uint tilesPerView = LayerCompositionUniforms.TileCountX * LayerCompositionUniforms.TileCountY;
    uint tileInView = tile % tilesPerView;
    uint size = LayerCompositionUniforms.TileSize;
    return uint3((tileInView % LayerCompositionUniforms.TileCountX) * size + localPixel % size,
        (tileInView / LayerCompositionUniforms.TileCountX) * size + localPixel / size, tile / tilesPerView);
}

LayerColorAccumulator ReadLayerAccumulator(uint3 pixel)
{
    return BeginLayerColorAccumulator(LayerAccumulatorColor[pixel], LayerAccumulatorDepth[pixel],
        LayerWorkingOpaqueDepth[pixel], LayerStencil[pixel]);
}

void ComposeLayerRecord(LayerSortRecord record, inout LayerColorAccumulator accumulator)
{
    LayerBeginDrawOrder(LayerCompositionUniforms.SortMode, LayerFragmentDrawOrder(record.Fragment), accumulator);
    if (accumulator.Finished && LayerCompositionUniforms.Accumulator == 0u) return;
    if (LayerCompositionUniforms.ClipByOpaqueDepth != 0u && record.Fragment.Depth > accumulator.OpaqueDepth) return;
    uint2 material = MaterialLayers[ReadFragmentMaterialAddress(PageHeap, VisibleClusters,
        InstanceData, LoadRenderInstanceMetadata(InstanceProperties, CLUSTER_MATERIAL_SLOT_OFFSET_METADATA_WORD),
        record.Fragment.Identity)];
    LayerOperation operation = LayerOperations[material.x];
    if (LayerCompositionUniforms.Accumulator != 0u &&
        !LayerApplyDepthStencil(operation, record.Fragment, material.y, accumulator)) return;
    if (accumulator.Finished) return;
    LayerApplyColor(operation, LayerFragmentPayloads[record.PayloadIndex], record.Fragment.Depth,
        LayerCompositionUniforms.SortMode, accumulator);
}

void WriteLayerAccumulator(uint3 pixel, LayerColorAccumulator accumulator)
{
    LayerAccumulatorColor[pixel] = ResolveLayerColor(accumulator, LayerCompositionUniforms.SortMode);
    LayerAccumulatorDepth[pixel] = accumulator.Depth;
    LayerWorkingOpaqueDepth[pixel] = accumulator.OpaqueDepth;
    LayerStencil[pixel] = accumulator.Stencil;
}

void CountLayerTilePixels(uint tile, uint area, uint lane)
{
    for (uint pixel = lane; pixel < area; pixel += LAYER_COMPOSITE_THREADS)
        LayerPixelCounts[pixel] = LayerPixelOffsets[pixel] = LayerPixelWrites[pixel] = 0u;
    if (lane == 0u) LayerTileRecordCount = LayerTileMaxPixelCount = 0u;
    GroupMemoryBarrierWithGroupSync();
    uint block = LayerTileHeads[tile];
    while (block != LAYER_BLOCK_EMPTY)
    {
        LayerBlockHeader header = ReadCompositeLayerBlock(block);
        for (uint record = lane; record < header.Count; record += LAYER_COMPOSITE_THREADS)
        {
            LayerFragment fragment = ReadCompositeLayerRecord(block, record).Fragment;
            uint pixel = LayerFragmentPixel(fragment);
            InterlockedAdd(LayerPixelOffsets[pixel], 1u);
            if (LayerFragmentPass(fragment) == LayerCompositionUniforms.PassIndex)
                InterlockedAdd(LayerPixelCounts[pixel], 1u);
        }
        block = header.Next;
    }
    GroupMemoryBarrierWithGroupSync();
    uint total = 0u, maximum = 0u;
    for (uint pixel = lane; pixel < area; pixel += LAYER_COMPOSITE_THREADS)
    {
        total += LayerPixelCounts[pixel];
        maximum = max(maximum, LayerPixelOffsets[pixel]);
        LayerPixelOffsets[pixel] = LayerPixelCounts[pixel];
    }
    InterlockedAdd(LayerTileRecordCount, total);
    InterlockedMax(LayerTileMaxPixelCount, maximum);
    GroupMemoryBarrierWithGroupSync();
    if (lane == 0u)
    {
        InterlockedMax(LayerFrameCounters[LAYER_FRAME_MAX_PIXEL_FRAGMENTS], LayerTileMaxPixelCount);
        if (LayerTileRecordCount > LAYER_SHARED_RECORD_CAPACITY)
            InterlockedAdd(LayerFrameCounters[LAYER_FRAME_SPLIT_TILES], 1u);
    }
    // The tile has 256 or 1024 pixels, so this scan operates on a power-of-two array.
    for (uint step = 1u; step < area; step <<= 1u)
    {
        for (uint pair = lane; pair < area / (step * 2u); pair += LAYER_COMPOSITE_THREADS)
        {
            uint right = (pair + 1u) * step * 2u - 1u;
            LayerPixelOffsets[right] += LayerPixelOffsets[right - step];
        }
        GroupMemoryBarrierWithGroupSync();
    }
    if (lane == 0u) LayerPixelOffsets[area - 1u] = 0u;
    GroupMemoryBarrierWithGroupSync();
    for (uint step = area >> 1u; step != 0u; step >>= 1u)
    {
        for (uint pair = lane; pair < area / (step * 2u); pair += LAYER_COMPOSITE_THREADS)
        {
            uint right = (pair + 1u) * step * 2u - 1u;
            uint left = right - step;
            uint value = LayerPixelOffsets[left];
            LayerPixelOffsets[left] = LayerPixelOffsets[right];
            LayerPixelOffsets[right] += value;
        }
        GroupMemoryBarrierWithGroupSync();
    }
}

void LayerSharedCompareExchange(uint left, uint right)
{
    LayerSortRecord a = LayerSharedRecords[left];
    LayerSortRecord b = LayerSharedRecords[right];
    if (LayerRecordBefore(b, a, LayerCompositionUniforms.SortMode))
    {
        LayerSharedRecords[left] = b;
        LayerSharedRecords[right] = a;
    }
}

void LayerSharedBitonicSort(uint first, uint count, uint lane)
{
    uint width = 1u;
    while (width < count) width <<= 1u;
    // All comparators keep the earlier record at the lower address. Omitted tail
    // positions therefore remain virtual +infinity without consuming shared storage.
    for (uint merge = 2u; merge <= width; merge <<= 1u)
    {
        uint half = merge >> 1u;
        for (uint pair = lane; pair < width / 2u; pair += LAYER_COMPOSITE_THREADS)
        {
            uint chunk = pair / half;
            uint local = pair % half;
            uint left = chunk * merge + local;
            uint right = chunk * merge + merge - 1u - local;
            if (right < count) LayerSharedCompareExchange(first + left, first + right);
        }
        GroupMemoryBarrierWithGroupSync();
        for (uint disperse = half; disperse >= 2u; disperse >>= 1u)
        {
            uint distance = disperse >> 1u;
            for (uint pair = lane; pair < width / 2u; pair += LAYER_COMPOSITE_THREADS)
            {
                uint left = (pair / distance) * disperse + pair % distance;
                uint right = left + distance;
                if (right < count) LayerSharedCompareExchange(first + left, first + right);
            }
            GroupMemoryBarrierWithGroupSync();
        }
    }
}

void LoadLayerSegment(uint tile, uint lane)
{
    uint block = LayerTileHeads[tile];
    uint firstOffset = LayerPixelOffsets[LayerSegmentBegin];
    while (block != LAYER_BLOCK_EMPTY)
    {
        LayerBlockHeader header = ReadCompositeLayerBlock(block);
        for (uint record = lane; record < header.Count; record += LAYER_COMPOSITE_THREADS)
        {
            LayerSortRecord value = ReadCompositeLayerRecord(block, record);
            uint pixel = LayerFragmentPixel(value.Fragment);
            if (LayerFragmentPass(value.Fragment) != LayerCompositionUniforms.PassIndex ||
                pixel < LayerSegmentBegin || pixel >= LayerSegmentEnd) continue;
            uint ordinal;
            InterlockedAdd(LayerPixelWrites[pixel], 1u, ordinal);
            LayerSharedRecords[LayerPixelOffsets[pixel] - firstOffset + ordinal] = value;
        }
        block = header.Next;
    }
    GroupMemoryBarrierWithGroupSync();
}

void CompositeRegisterPixel(uint tile, uint pixel, uint first, uint count)
{
    uint indices[16];
    [unroll]
    for (uint index = 0u; index < 16u; index++) indices[index] = index;
    [loop]
    for (uint index = 1u; index < count; index++)
    {
        uint inserted = indices[index];
        uint position = index;
        [loop]
        while (position != 0u)
        {
            if (!LayerRecordBefore(LayerSharedRecords[first + inserted],
                LayerSharedRecords[first + indices[position - 1u]], LayerCompositionUniforms.SortMode)) break;
            indices[position] = indices[position - 1u];
            position--;
        }
        indices[position] = inserted;
    }
    uint3 coordinate = LayerCompositePixel(tile, pixel);
    LayerColorAccumulator accumulator = ReadLayerAccumulator(coordinate);
    [loop]
    for (uint index = 0u; index < count; index++)
        ComposeLayerRecord(LayerSharedRecords[first + indices[index]], accumulator);
    WriteLayerAccumulator(coordinate, accumulator);
}

uint ReadLayerScratch(uint offset, bool merged)
{
    return merged ? LayerMergeScratch[offset] : LayerSortScratch[offset];
}

void CompositeSlowPixel(uint tile, uint pixel, uint count, uint lane)
{
    if (lane == 0u)
    {
        InterlockedAdd(LayerFrameCounters[LAYER_FRAME_SLOW_SCRATCH], count, LayerSlowBase);
        InterlockedAdd(LayerFrameCounters[LAYER_FRAME_SLOW_PIXELS], 1u);
        LayerScratchWriteCount = 0u;
    }
    GroupMemoryBarrierWithGroupSync();
    uint block = LayerTileHeads[tile];
    while (block != LAYER_BLOCK_EMPTY)
    {
        LayerBlockHeader header = ReadCompositeLayerBlock(block);
        for (uint record = lane; record < header.Count; record += LAYER_COMPOSITE_THREADS)
        {
            LayerSortRecord value = ReadCompositeLayerRecord(block, record);
            if (LayerFragmentPass(value.Fragment) != LayerCompositionUniforms.PassIndex || LayerFragmentPixel(value.Fragment) != pixel) continue;
            uint offset;
            InterlockedAdd(LayerScratchWriteCount, 1u, offset);
            LayerSortScratch[LayerSlowBase + offset] = value.PayloadIndex;
        }
        block = header.Next;
    }
    DeviceMemoryBarrierWithGroupSync();
    for (uint run = 0u; run < count; run += LAYER_SHARED_RECORD_CAPACITY)
    {
        uint length = min(LAYER_SHARED_RECORD_CAPACITY, count - run);
        for (uint index = lane; index < length; index += LAYER_COMPOSITE_THREADS)
            LayerSharedRecords[index] = ReadCompositeLayerRecord(LayerSortScratch[LayerSlowBase + run + index]);
        GroupMemoryBarrierWithGroupSync();
        LayerSharedBitonicSort(0u, length, lane);
        for (uint index = lane; index < length; index += LAYER_COMPOSITE_THREADS)
            LayerSortScratch[LayerSlowBase + run + index] = LayerSharedRecords[index].PayloadIndex;
        DeviceMemoryBarrierWithGroupSync();
    }
    bool merged = false;
    for (uint run = LAYER_SHARED_RECORD_CAPACITY; run < count; run <<= 1u)
    {
        for (uint index = lane; index < count; index += LAYER_COMPOSITE_THREADS)
        {
            uint pair = (index / (run * 2u)) * run * 2u;
            uint leftCount = min(run, count - pair);
            uint rightFirst = pair + leftCount;
            uint rightCount = min(run, count - rightFirst);
            uint rank = index - pair;
            uint low = rank > rightCount ? rank - rightCount : 0u;
            uint high = min(rank, leftCount);
            while (low < high)
            {
                uint left = low + (high - low) / 2u;
                uint right = rank - left;
                bool advance = false;
                if (right != 0u && left < leftCount)
                {
                    LayerSortRecord a = ReadCompositeLayerRecord(ReadLayerScratch(LayerSlowBase + pair + left, merged));
                    LayerSortRecord b = ReadCompositeLayerRecord(ReadLayerScratch(LayerSlowBase + rightFirst + right - 1u, merged));
                    advance = !LayerRecordBefore(b, a, LayerCompositionUniforms.SortMode);
                }
                if (advance) low = left + 1u;
                else high = left;
            }
            uint left = low, right = rank - low;
            bool takeLeft = right >= rightCount;
            if (!takeLeft && left < leftCount)
            {
                LayerSortRecord a = ReadCompositeLayerRecord(ReadLayerScratch(LayerSlowBase + pair + left, merged));
                LayerSortRecord b = ReadCompositeLayerRecord(ReadLayerScratch(LayerSlowBase + rightFirst + right, merged));
                takeLeft = !LayerRecordBefore(b, a, LayerCompositionUniforms.SortMode);
            }
            uint value = takeLeft
                ? ReadLayerScratch(LayerSlowBase + pair + left, merged)
                : ReadLayerScratch(LayerSlowBase + rightFirst + right, merged);
            if (merged) LayerSortScratch[LayerSlowBase + index] = value;
            else LayerMergeScratch[LayerSlowBase + index] = value;
        }
        DeviceMemoryBarrierWithGroupSync();
        merged = !merged;
    }
    if (lane == 0u)
    {
        uint3 coordinate = LayerCompositePixel(tile, pixel);
        LayerColorAccumulator accumulator = ReadLayerAccumulator(coordinate);
        for (uint index = 0u; index < count; index++)
            ComposeLayerRecord(ReadCompositeLayerRecord(ReadLayerScratch(LayerSlowBase + index, merged)), accumulator);
        WriteLayerAccumulator(coordinate, accumulator);
    }
    DeviceMemoryBarrierWithGroupSync();
}

[shader("compute")]
[numthreads(8, 8, 1)]
void LayerInitializeAccumulators(uint3 pixel : SV_DispatchThreadID)
{
    if (pixel.x >= LayerCompositionUniforms.Width || pixel.y >= LayerCompositionUniforms.Height ||
        pixel.z >= LayerCompositionUniforms.ViewCount) return;
    LayerAccumulatorColor[pixel] = LayerOpaqueColor.Load(int4(pixel, 0));
    float depth = LayerOpaqueDepth.Load(int4(pixel, 0));
    LayerAccumulatorDepth[pixel] = LayerWorkingOpaqueDepth[pixel] = depth;
    LayerStencil[pixel] = 0u;
}

[shader("compute")]
[numthreads(256, 1, 1)]
void LayerComposite(uint3 group : SV_GroupID, uint lane : SV_GroupIndex)
{
    uint tile = group.x + group.y * 65535u;
    if (tile >= LayerCompositionUniforms.TileCount) return;
    if (LayerTileHeads[tile] == LAYER_BLOCK_EMPTY) return;
    uint area = LayerCompositionUniforms.TileSize * LayerCompositionUniforms.TileSize;
    CountLayerTilePixels(tile, area, lane);
    if (LayerTileRecordCount == 0u) return;
    if (lane == 0u) LayerNextPixel = 0u;
    GroupMemoryBarrierWithGroupSync();
    // Every wave enters the selection barrier before reading its shared exit field.
    while (true)
    {
        if (lane == 0u)
        {
            uint first = LayerNextPixel;
            while (first < area && LayerPixelCounts[first] == 0u) first++;
            LayerSegmentBegin = first;
            LayerSegmentEnd = first;
            LayerSegmentCount = 0u;
            LayerSlowPixel = LAYER_BLOCK_EMPTY;
            if (first < area && LayerPixelCounts[first] > LAYER_SHARED_RECORD_CAPACITY)
            {
                LayerSlowPixel = first;
                LayerSegmentEnd = first + 1u;
            }
            else
                while (LayerSegmentEnd < area && LayerSegmentCount + LayerPixelCounts[LayerSegmentEnd] <= LAYER_SHARED_RECORD_CAPACITY)
                {
                    LayerSegmentCount += LayerPixelCounts[LayerSegmentEnd];
                    LayerSegmentEnd++;
                }
            LayerNextPixel = LayerSegmentEnd;
        }
        GroupMemoryBarrierWithGroupSync();
        if (LayerSegmentBegin == area) break;
        if (LayerSlowPixel != LAYER_BLOCK_EMPTY)
            CompositeSlowPixel(tile, LayerSlowPixel, LayerPixelCounts[LayerSlowPixel], lane);
        else
        {
            LoadLayerSegment(tile, lane);
            uint firstOffset = LayerPixelOffsets[LayerSegmentBegin];
            for (uint pixel = LayerSegmentBegin + lane; pixel < LayerSegmentEnd; pixel += LAYER_COMPOSITE_THREADS)
                if (LayerPixelCounts[pixel] != 0u && LayerPixelCounts[pixel] <= 16u)
                    CompositeRegisterPixel(tile, pixel, LayerPixelOffsets[pixel] - firstOffset, LayerPixelCounts[pixel]);
            GroupMemoryBarrierWithGroupSync();
            for (uint pixel = LayerSegmentBegin; pixel < LayerSegmentEnd; pixel++)
                if (LayerPixelCounts[pixel] > 16u)
                {
                    uint first = LayerPixelOffsets[pixel] - firstOffset;
                    uint count = LayerPixelCounts[pixel];
                    LayerSharedBitonicSort(first, count, lane);
                    if (lane == 0u)
                    {
                        uint3 coordinate = LayerCompositePixel(tile, pixel);
                        LayerColorAccumulator accumulator = ReadLayerAccumulator(coordinate);
                        for (uint index = 0u; index < count; index++)
                            ComposeLayerRecord(LayerSharedRecords[first + index], accumulator);
                        WriteLayerAccumulator(coordinate, accumulator);
                    }
                    GroupMemoryBarrierWithGroupSync();
                }
        }
        GroupMemoryBarrierWithGroupSync();
    }
}
