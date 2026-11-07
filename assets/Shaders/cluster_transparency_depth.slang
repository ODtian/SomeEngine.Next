#ifndef CLUSTER_TRANSPARENCY_DEPTH_SLANG
#define CLUSTER_TRANSPARENCY_DEPTH_SLANG

groupshared uint TransparencyDepthMinimum, TransparencyDepthMaximum;
groupshared uint TransparencyDepthStarts[16], TransparencyDepthEnds[16];
groupshared uint TransparencyDepthHistogram[64];
groupshared uint TransparencyDepthOffsets[16], TransparencyDepthWrites[16];
groupshared uint TransparencyDepthSlices, TransparencyDepthFirst;
groupshared uint TransparencyCoverageSamples;

bool HasTransparencyDepthPartitions()
{ return (TransparencyUniforms.Effects & 4u) != 0u; }

float2 TransparencyTileDepthRange(TransparencyTriangle triangle, float2 origin, float2 extent)
{
    float2 low = max(origin,ceil(min(triangle.First,min(triangle.Second,triangle.Third)) - .5) + .5);
    float2 high = min(origin + extent,floor(max(triangle.First,max(triangle.Second,triangle.Third)) - .5) + .5);
    return saturate(TransparencyDepthBounds(triangle,low,high,1e-7));
}

float TransparencySortCost(uint count)
{
    if (count == 0u) return 0.0;
    float levels = max(0.0,ceil(log2(float(count))) - 7.0);
    float local = min(7.0,ceil(log2(float(count))));
    return float(count) * (local * (local + 1.0) * .5 + 6.0 * levels * (levels + 1.0));
}

float TransparencyTraversalCost(uint count, float layers)
{ return float(count) * 20.0 * max(1.0,ceil(layers / 16.0)); }

bool TransparencyDepthPartitionCanPay(uint count, float layers)
{
    float ideal = float(count) * 12.0 + 16.0 * 256.0 +
        16.0 * TransparencySortCost((count + 15u) / 16u) + TransparencyTraversalCost(count,layers / 16.0);
    return count > 128u && ideal < (TransparencySortCost(count) + TransparencyTraversalCost(count,layers)) * .85;
}

uint TransparencyPartitionBin(float depth, float minimum, float scale, uint slices)
{ return min(uint(max(0.0,(depth - minimum) * scale)),slices - 1u); }

uint BuildTransparencyDepthPartition(uint cell, uint first, uint count, float2 origin, float2 extent, uint thread)
{
    if (thread == 0u)
    {
        TransparencyDepthMinimum = asuint(1.0);
        TransparencyDepthMaximum = 0u;
        TransparencyDepthSlices = 1u;
    }
    if (thread < 16u) { TransparencyDepthStarts[thread] = 0u; TransparencyDepthEnds[thread] = 0u; }
    GroupMemoryBarrierWithGroupSync();
    float low = 1.0, high = 0.0;
    for (uint item = thread; item < count; item += 128u)
    {
        float2 range = TransparencyTileDepthRange(TransparencyRasterTriangles[TransparencyReferences[first + item]],origin,extent);
        TransparencyReferenceDepths[first + item] = range;
        low = min(low,range.x); high = max(high,range.y);
    }
    low = WaveActiveMin(low); high = WaveActiveMax(high);
    if (WaveIsFirstLane())
    {
        InterlockedMin(TransparencyDepthMinimum,asuint(low));
        InterlockedMax(TransparencyDepthMaximum,asuint(high));
    }
    DeviceMemoryBarrierWithGroupSync();
    float minimum = asfloat(TransparencyDepthMinimum);
    float inverse = 1.0 / max(asfloat(TransparencyDepthMaximum) - minimum,1e-7);
    for (uint item = thread; item < count; item += 128u)
    {
        float2 range = TransparencyReferenceDepths[first + item];
        uint start = TransparencyPartitionBin(range.x,minimum,inverse * 16u,16u);
        uint end = TransparencyPartitionBin(range.y,minimum,inverse * 16u,16u);
        InterlockedAdd(TransparencyDepthStarts[start],1u);
        InterlockedAdd(TransparencyDepthEnds[end],1u);
    }
    GroupMemoryBarrierWithGroupSync();
    if (thread == 0u)
    {
        for (uint bin = 1u; bin < 16u; bin++)
        {
            TransparencyDepthStarts[bin] += TransparencyDepthStarts[bin - 1u];
            TransparencyDepthEnds[bin] += TransparencyDepthEnds[bin - 1u];
        }
        float layers = float(TransparencyCoverageSamples) / ((extent.x + 1.0) * (extent.y + 1.0));
        float baseline = TransparencySortCost(count) + TransparencyTraversalCost(count,layers), best = baseline * .85;
        uint selected = 0u, references = 0u;
        for (uint level = 0u; level < 4u; level++)
        {
            uint slices = 2u << level, total = 0u;
            float cost = float(count) * 12.0 + float(slices) * 256.0;
            for (uint bin = 0u; bin < slices; bin++)
            {
                uint width = 16u / slices;
                uint size = TransparencyDepthStarts[(bin + 1u) * width - 1u] -
                    (bin == 0u ? 0u : TransparencyDepthEnds[bin * width - 1u]);
                TransparencyDepthHistogram[level * 16u + bin] = size;
                total += size; cost += TransparencySortCost(size) + TransparencyTraversalCost(size,layers * float(size) / float(count));
            }
            if (total <= count * 2u && cost < best)
            { best = cost; selected = level; references = total; TransparencyDepthSlices = slices; }
        }
        if (TransparencyDepthSlices > 1u)
        {
            TransparencyTileWork.InterlockedAdd(0u,references,TransparencyDepthFirst);
            uint offset = TransparencyDepthFirst;
            for (uint bin = 0u; bin < TransparencyDepthSlices; bin++)
            {
                uint size = TransparencyDepthHistogram[selected * 16u + bin];
                uint leaves = size == 0u ? 0u : 1u << uint(ceil(log2(float((size + 31u) / 32u))));
                uint node = 0u;
                TransparencyCellWork.InterlockedAdd(4u,leaves * 2u,node);
                TransparencyDepthCells[cell * 16u + bin] = uint4(offset,size,node,leaves);
                TransparencyDepthOffsets[bin] = offset;
                TransparencyDepthWrites[bin] = 0u;
                offset += size;
            }
            TransparencyTilePartitions[cell] = uint4(asuint(minimum),asuint(inverse * TransparencyDepthSlices),
                TransparencyDepthSlices,0u);
        }
    }
    DeviceMemoryBarrierWithGroupSync();
    if (TransparencyDepthSlices == 1u) return 1u;
    for (uint item = thread; item < count; item += 128u)
    {
        float2 range = TransparencyReferenceDepths[first + item];
        uint start = TransparencyPartitionBin(range.x,minimum,inverse * TransparencyDepthSlices,TransparencyDepthSlices);
        uint end = TransparencyPartitionBin(range.y,minimum,inverse * TransparencyDepthSlices,TransparencyDepthSlices);
        uint id = TransparencyReferences[first + item];
        for (uint bin = start; bin <= end; bin++)
        {
            uint output;
            InterlockedAdd(TransparencyDepthWrites[bin],1u,output);
            TransparencyDepthReferences[TransparencyDepthOffsets[bin] + output] = id;
        }
    }
    DeviceMemoryBarrierWithGroupSync();
    return TransparencyDepthSlices;
}

struct TransparencyCellRange
{
    uint Cell, First, Count, Slices;
    float2 Origin, Extent;
    bool Local;
};

bool PrepareTransparencyCell(uint3 group, uint thread, out TransparencyCellRange value)
{
    value = {};
    if (TransparencyTriangleCount.Load(0u) > TransparencyUniforms.TriangleCapacity) return false;
    uint job = group.x + group.y * 65535u;
    if (job >= TransparencyCellWork.Load(0u)) return false;
    uint cell = TransparencyCellWork.Load(16u + job * 4u);
    if (TransparencyCellStreams[cell] != 0u) return false;
    uint first = TransparencyCellOffsets[cell], count = TransparencyTileCounts[cell];
    uint view = cell / TransparencyLightingViews[0].CellCountPerView;
    ClusteredLightGridParameters grid = TransparencyLightingViews[view];
    uint tile = cell % (grid.TileCountX * grid.TileCountY);
    float2 origin = float2(tile % grid.TileCountX,tile / grid.TileCountX) * float2(grid.TileSizeX,grid.TileSizeY) + .5;
    float2 extent = min(float2(grid.TileSizeX,grid.TileSizeY) - 1.0,
        float2(TransparencyUniforms.Width,TransparencyUniforms.Height) - .5 - origin);
    bool local;
    {
        if (thread == 0u) TransparencyCoverageSamples = 0u;
        GroupMemoryBarrierWithGroupSync();
        uint samples = 0u;
        for (uint item = thread; item < count; item += 128u)
        {
            uint id = TransparencyReferences[first + item];
            if ((TransparencyUniforms.Effects & 8u) != 0u)
            {
                uint4 range = TransparencyCoverageRanges[id];
                uint2 xy = uint2(origin - .5) / uint2(grid.TileSizeX,grid.TileSizeY);
                uint association = range.x + (xy.y - range.z) * range.w + xy.x - range.y;
                for (uint word = 0u; word < 8u; word++) samples += countbits(TransparencyHardwareMasks[association * 8u + word]);
            }
            else
            {
                TransparencyTriangle triangle = TransparencyRasterTriangles[id];
                float2 low = max(origin,min(triangle.First,min(triangle.Second,triangle.Third)));
                float2 high = min(origin + extent,max(triangle.First,max(triangle.Second,triangle.Third)));
                float2 size = max(0.0.xx,high - low + 1.0);
                samples += uint(ceil(min(.5 / abs(triangle.InverseArea),size.x * size.y)));
            }
        }
        samples = WaveActiveSum(samples);
        if (WaveIsFirstLane()) InterlockedAdd(TransparencyCoverageSamples,samples);
        GroupMemoryBarrierWithGroupSync();
        local = count <= 32u || TransparencyCoverageSamples <= 16u * uint((extent.x + 1.0) * (extent.y + 1.0));
        if (local && thread == 0u)
            TransparencyCellTrees[cell] = uint2(0u,0x40000000u);
        if (!local && count <= 128u)
        {
            if (thread == 0u)
            {
                uint leaves = 1u << uint(ceil(log2(float((count + 31u) / 32u))));
                uint node;
                TransparencyCellWork.InterlockedAdd(4u,leaves * 2u,node);
                TransparencyCellTrees[cell] = uint2(node,leaves);
            }
            DeviceMemoryBarrierWithGroupSync();
        }
    }
    float layers = float(TransparencyCoverageSamples) / ((extent.x + 1.0) * (extent.y + 1.0));
    uint slices = !local && HasTransparencyDepthPartitions() && TransparencyDepthPartitionCanPay(count,layers)
        ? BuildTransparencyDepthPartition(cell,first,count,origin,extent,thread) : 1u;
    if (thread == 0u && grid.DepthSliceCount == 1u)
    {
        TransparencyTileWork.InterlockedAdd(slices == 1u ? 4u : 8u,1u);
        if (slices > 1u) TransparencyTileWork.InterlockedAdd(24u,slices);
        if (local) TransparencyTileWork.InterlockedAdd(12u,1u);
    }
    value.Cell = cell; value.First = first; value.Count = count; value.Slices = slices;
    value.Origin = origin; value.Extent = extent; value.Local = local;
    return true;
}

void CombineTransparencyDepthCells(uint cell, uint count, uint slices)
{
    uint references = 0u;
    bool ordered = true;
    for (uint slice = 0u; slice < slices; slice++)
    {
        uint4 range = TransparencyDepthCells[cell * 16u + slice];
        references += range.y;
        ordered = ordered && (range.y == 0u || (range.w & 0x80000000u) != 0u);
    }
    if (ordered && references == count)
    {
        TransparencyDepthCells[cell * 16u].y = count;
        TransparencyDepthCells[cell * 16u].w = 0x80000000u;
        TransparencyTilePartitions[cell].w = 1u;
    }
}

void SortTransparencyCells(uint3 group, uint thread)
{
    TransparencyCellRange value;
    if (!PrepareTransparencyCell(group,thread,value)) return;
    uint cell = value.Cell, first = value.First, count = value.Count, slices = value.Slices;
    float2 origin = value.Origin, extent = value.Extent;
    if (slices > 1u)
    {
        for (uint slice = 0u; slice < slices; slice++)
        {
            uint index = cell * 16u + slice;
            uint4 range = TransparencyDepthCells[index];
            if (range.y != 0u) SortTransparencyRange(range.x,range.y,origin,extent,range.zw,index,true,thread);
            GroupMemoryBarrierWithGroupSync();
        }
        if (thread == 0u) CombineTransparencyDepthCells(cell,count,slices);
    }
    else if (!value.Local)
        SortTransparencyRange(first,count,origin,extent,TransparencyCellTrees[cell],cell,false,thread);
}

void PrepareTransparencyCells(uint3 group, uint thread)
{
    TransparencyCellRange value;
    if (!PrepareTransparencyCell(group,thread,value) || value.Local || thread != 0u) return;
    uint count = value.Slices > 1u ? value.Slices : 1u, first;
    TransparencySortJobs.InterlockedAdd(0u,count,first);
    for (uint slice = 0u; slice < count; slice++)
        TransparencySortJobs.Store(16u + (first + slice) * 4u,value.Slices > 1u
            ? value.Cell * 16u + slice : value.Cell | 0x80000000u);
}

void FinalizeTransparencySort()
{
    uint count = TransparencySortJobs.Load(0u);
    WriteTransparencyDispatch(148u,uint3(min(count,65535u),(count + 65534u) / 65535u,1u));
}

void SortTransparencyRanges(uint3 group, uint thread)
{
    uint job = group.x + group.y * 65535u;
    if (job >= TransparencySortJobs.Load(0u)) return;
    uint key = TransparencySortJobs.Load(16u + job * 4u);
    bool adaptive = (key & 0x80000000u) == 0u;
    uint cell = adaptive ? key / 16u : key & 0x7FFFFFFFu;
    uint4 range = adaptive ? TransparencyDepthCells[key]
        : uint4(TransparencyCellOffsets[cell],TransparencyTileCounts[cell],TransparencyCellTrees[cell]);
    if (range.y == 0u) return;
    uint view = cell / TransparencyLightingViews[0].CellCountPerView;
    ClusteredLightGridParameters grid = TransparencyLightingViews[view];
    uint tile = cell % (grid.TileCountX * grid.TileCountY);
    float2 origin = float2(tile % grid.TileCountX,tile / grid.TileCountX) * float2(grid.TileSizeX,grid.TileSizeY) + .5;
    float2 extent = min(float2(grid.TileSizeX,grid.TileSizeY) - 1.0,
        float2(TransparencyUniforms.Width,TransparencyUniforms.Height) - .5 - origin);
    SortTransparencyRange(range.x,range.y,origin,extent,range.zw,adaptive ? key : cell,adaptive,thread);
}

uint TransparencyTileSlices(uint tile, ClusteredLightGridParameters grid)
{
    if (!HasTransparencyDepthPartitions()) return grid.DepthSliceCount;
    uint4 partition = TransparencyTilePartitions[tile];
    return partition.w == 1u ? 1u : partition.z;
}

bool TransparencyTileUsesDepthReferences(uint tile)
{ return HasTransparencyDepthPartitions() && TransparencyTilePartitions[tile].z > 1u; }

uint4 LoadTransparencyTilePartition(uint tile)
{ return HasTransparencyDepthPartitions() ? TransparencyTilePartitions[tile] : uint4(0u,0u,1u,0u); }

uint4 LoadTransparencyTileCell(uint tileIndex, uint view, uint2 tile, uint slice, bool adaptive)
{
    if (adaptive) return TransparencyDepthCells[tileIndex * 16u + slice];
    uint cell = TransparencyCell(view,uint3(tile,slice));
    return uint4(TransparencyCellOffsets[cell],TransparencyTileCounts[cell],TransparencyCellTrees[cell]);
}

bool TransparencySliceAccepts(uint4 partition, bool adaptive, uint slice, float depth)
{
    if (!adaptive || partition.w == 1u) return true;
    return slice == TransparencyPartitionBin(depth,asfloat(partition.x),asfloat(partition.y),partition.z);
}

uint LastTransparencyTileSlice(uint4 partition, bool adaptive, uint view, float2 pixel, float opaque)
{
    ClusteredLightGridParameters grid = TransparencyLightingViews[view];
    if (adaptive)
    {
        if (partition.w == 1u) return 0u;
        return TransparencyPartitionBin(opaque,asfloat(partition.x),asfloat(partition.y),partition.z);
    }
    return grid.DepthSliceCount == 1u ? 0u : opaque < 1.0
        ? ClusteredLightDepthSlice(grid,TransparencyViewDepth(view,pixel,opaque)) : grid.DepthSliceCount - 1u;
}

void ClassifyTransparencyTiles(uint3 thread)
{
    if (TransparencyTriangleCount.Load(0u) > TransparencyUniforms.TriangleCapacity || thread.x >= TransparencyCellWork.Load(12u)) return;
    uint index = TransparencyCellWork.Load(16u + (TransparencyUniforms.CellCount + thread.x) * 4u);
    ClusteredLightGridParameters grid = TransparencyLightingViews[0];
    uint view = index / (grid.TileCountX * grid.TileCountY);
    grid = TransparencyLightingViews[view];
    uint2 tile = uint2(index % grid.TileCountX,(index / grid.TileCountX) % grid.TileCountY);
    bool ordered = true;
    bool adaptive = TransparencyTileUsesDepthReferences(index);
    uint slices = TransparencyTileSlices(index,grid);
    for (uint slice = 0u; slice < slices; slice++)
    {
        uint4 range = LoadTransparencyTileCell(index,view,tile,slice,adaptive);
        if (range.y != 0u && (range.w & 0x80000000u) == 0u) ordered = false;
    }
    bool local = !adaptive && grid.DepthSliceCount == 1u &&
        (TransparencyCellTrees[TransparencyCell(view,uint3(tile,0u))].y & 0x40000000u) != 0u;
    uint job;
    TransparencyTileWork.InterlockedAdd(local ? 28u : ordered ? 20u : 16u,1u,job);
    TransparencyTileWork.Store(32u + ((local ? TransparencyUniforms.CellCount * 2u : ordered ? TransparencyUniforms.CellCount : 0u) + job) * 4u,index);
}

void ClassifyTransparencySortedTiles(uint3 thread)
{
    if (TransparencyTriangleCount.Load(0u) > TransparencyUniforms.TriangleCapacity || thread.x >= TransparencyCellWork.Load(12u)) return;
    uint cell = TransparencyCellWork.Load(16u + (TransparencyUniforms.CellCount + thread.x) * 4u);
    if (HasTransparencyDepthPartitions())
    {
        uint4 partition = TransparencyTilePartitions[cell];
        if (partition.z > 1u && partition.w != 1u)
            CombineTransparencyDepthCells(cell,TransparencyTileCounts[cell],partition.z);
    }
    ClassifyTransparencyTiles(thread);
}

#endif
