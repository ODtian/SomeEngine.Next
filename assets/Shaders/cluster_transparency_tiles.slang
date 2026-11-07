#ifndef CLUSTER_TRANSPARENCY_TILES_SLANG
#define CLUSTER_TRANSPARENCY_TILES_SLANG

groupshared uint TransparencySortIds[128];
groupshared float TransparencySortDepths[128];
groupshared uint TransparencyCellOrdered;

float TransparencySortDepth(uint index, float2 center)
{
    TransparencyTriangle triangle = TransparencyRasterTriangles[index];
    float depth = triangle.Depths.x + dot(center - triangle.First,triangle.DepthPlane.yz);
    return clamp(depth,min(triangle.Depths.x,min(triangle.Depths.y,triangle.Depths.z)),
        max(triangle.Depths.x,max(triangle.Depths.y,triangle.Depths.z)));
}

float2 TransparencyDepthBounds(TransparencyTriangle triangle, float2 low, float2 high, float margin)
{
    float depth = triangle.Depths.x + dot(low - triangle.First,triangle.DepthPlane.yz);
    float2 delta = (high - low) * triangle.DepthPlane.yz;
    float2 range = float2(depth + dot(min(delta,0.0.xx),1.0.xx) - margin,
        depth + dot(max(delta,0.0.xx),1.0.xx) + margin);
    return clamp(range,min(triangle.Depths.x,min(triangle.Depths.y,triangle.Depths.z)) - margin,
        max(triangle.Depths.x,max(triangle.Depths.y,triangle.Depths.z)) + margin);
}

bool TransparencyReferenceTieBefore(uint first, uint second)
{
    if (first == second || first == 0xFFFFFFFFu) return false;
    if (second == 0xFFFFFFFFu) return true;
    TransparencyTriangle a = TransparencyRasterTriangles[first], b = TransparencyRasterTriangles[second];
    return a.Source == b.Source && a.Primitive == b.Primitive ? first < second :
        TransparencyBefore(0.0, a.Source, a.Primitive, 0.0, b.Source, b.Primitive);
}

bool TransparencyReferenceBefore(uint first, uint second, float2 center)
{
    float a = TransparencySortDepth(first, center), b = TransparencySortDepth(second, center);
    return a < b || (a == b && TransparencyReferenceTieBefore(first, second));
}

uint ReadTransparencyReference(uint index, bool adaptive)
{ return adaptive ? TransparencyDepthReferences[index] : TransparencyReferences[index]; }

void StoreTransparencyReference(uint index, uint value, bool adaptive)
{ if (adaptive) TransparencyDepthReferences[index] = value; else TransparencyReferences[index] = value; }

uint ReadTransparencySort(uint index, bool scratch, bool adaptive)
{ return scratch ? TransparencySortScratch[index + (adaptive ? TransparencyUniforms.ReferenceCapacity : 0u)] : ReadTransparencyReference(index,adaptive); }

float ReadTransparencySortDepth(uint index, bool scratch, bool adaptive)
{
    float2 depths = TransparencyReferenceDepths[index + (adaptive ? TransparencyUniforms.ReferenceCapacity : 0u)];
    return scratch ? depths.y : depths.x;
}

void StoreTransparencySortDepth(uint index, float depth, bool scratch, bool adaptive)
{
    uint output = index + (adaptive ? TransparencyUniforms.ReferenceCapacity : 0u);
    if (scratch) TransparencyReferenceDepths[output].y = depth;
    else TransparencyReferenceDepths[output].x = depth;
}

void StoreTransparencyTree(uint index, bool adaptive, uint2 tree)
{ if (adaptive) TransparencyDepthCells[index].zw = tree; else TransparencyCellTrees[index] = tree; }

void SortTransparencyRange(uint first, uint count, float2 origin, float2 extent,
    uint2 tree, uint treeIndex, bool adaptive, uint thread)
{
    float2 center = origin + extent * .5;
    if (thread == 0u) TransparencyCellOrdered = 1u;
    for (uint block = 0u; block < count; block += 128u)
    {
        uint index = block + thread;
        uint id = index < count ? ReadTransparencyReference(first + index,adaptive) : 0xFFFFFFFFu;
        float depth = index < count ? TransparencySortDepth(id, center) : asfloat(0x7F800000u);
        uint sortWidth = 1u << uint(ceil(log2(float(min(count - block, 128u)))));
        for (uint size = 2u; size <= sortWidth; size <<= 1u)
        for (uint stride = size >> 1u; stride > 0u; stride >>= 1u)
        {
            uint other = thread ^ stride;
            uint otherId;
            float otherDepth;
            if (stride < 32u)
            {
                otherId = WaveReadLaneAt(id,WaveGetLaneIndex() ^ stride);
                otherDepth = WaveReadLaneAt(depth,WaveGetLaneIndex() ^ stride);
            }
            else
            {
                TransparencySortIds[thread] = id;
                TransparencySortDepths[thread] = depth;
                GroupMemoryBarrierWithGroupSync();
                otherId = TransparencySortIds[other];
                otherDepth = TransparencySortDepths[other];
                GroupMemoryBarrierWithGroupSync();
            }
            bool before = depth < otherDepth || (depth == otherDepth && TransparencyReferenceTieBefore(id,otherId));
            bool ascending = (thread & size) == 0u;
            bool swap = (other > thread) == (before != ascending);
            if (swap) { id = otherId; depth = otherDepth; }
        }
        if (index < count)
        {
            StoreTransparencyReference(first + index,id,adaptive);
            StoreTransparencySortDepth(first + index,depth,false,adaptive);
        }
    }
    AllMemoryBarrierWithGroupSync();
    bool scratch = false;
    for (uint width = 128u; width < count; width <<= 1u)
    {
        for (uint item = thread; item < count; item += 128u)
        {
            uint id = ReadTransparencySort(first + item,scratch,adaptive);
            float depth = ReadTransparencySortDepth(first + item,scratch,adaptive);
            uint run = item / width, start = (run ^ 1u) * width;
            uint low = min(start, count), high = min(start + width, count);
            while (low < high)
            {
                uint mid = (low + high) >> 1u;
                float otherDepth = ReadTransparencySortDepth(first + mid,scratch,adaptive);
                bool before = otherDepth < depth;
                if (otherDepth == depth) before = TransparencyReferenceTieBefore(ReadTransparencySort(first + mid,scratch,adaptive),id);
                if (before) low = mid + 1u;
                else high = mid;
            }
            uint output = first + (run & ~1u) * width + item % width + low - min(start, count);
            if (scratch) StoreTransparencyReference(output,id,adaptive);
            else TransparencySortScratch[output + (adaptive ? TransparencyUniforms.ReferenceCapacity : 0u)] = id;
            StoreTransparencySortDepth(output,depth,!scratch,adaptive);
        }
        DeviceMemoryBarrierWithGroupSync();
        scratch = !scratch;
    }
    if (scratch)
    {
        for (uint item = thread; item < count; item += 128u)
            StoreTransparencyReference(first + item,TransparencySortScratch[first + item + (adaptive ? TransparencyUniforms.ReferenceCapacity : 0u)],adaptive);
        DeviceMemoryBarrierWithGroupSync();
    }
    for (uint item = thread + 1u; item < count; item += 128u)
    {
        TransparencyTriangle a = TransparencyRasterTriangles[ReadTransparencyReference(first + item - 1u,adaptive)];
        TransparencyTriangle b = TransparencyRasterTriangles[ReadTransparencyReference(first + item,adaptive)];
        float2 delta = b.DepthPlane.yz - a.DepthPlane.yz;
        float difference = b.Depths.x + dot(origin - b.First,b.DepthPlane.yz) -
            (a.Depths.x + dot(origin - a.First,a.DepthPlane.yz));
        float minimum = difference + dot(min(delta * extent, 0.0.xx), 1.0.xx);
        if (!(minimum > 1e-7 || (difference == 0.0 && all(delta == 0.0))))
            InterlockedAnd(TransparencyCellOrdered, 0u);
    }
    GroupMemoryBarrierWithGroupSync();
    if (TransparencyCellOrdered != 0u)
    {
        if (thread == 0u) StoreTransparencyTree(treeIndex,adaptive,uint2(tree.x,tree.y | 0x80000000u));
        return;
    }
    uint lane = WaveGetLaneIndex();
    for (uint leaf = thread / 32u; leaf < tree.y; leaf += 4u)
    {
        TransparencyDepthNode node;
        node.MinimumDepth = 2.0; node.MaximumDepth = -1.0;
        uint2 low = 65535u.xx, high = 0u.xx;
        uint item = leaf * 32u + lane;
        if (item < count)
        {
            TransparencyTriangle triangle = TransparencyRasterTriangles[ReadTransparencyReference(first + item,adaptive)];
            float2 firstPixel = max(origin, ceil(min(triangle.First, min(triangle.Second, triangle.Third)) - .5) + .5);
            float2 lastPixel = min(origin + extent, floor(max(triangle.First, max(triangle.Second, triangle.Third)) - .5) + .5);
            float2 depths = TransparencyDepthBounds(triangle,firstPixel,lastPixel,1e-6);
            node.MinimumDepth = depths.x;
            node.MaximumDepth = depths.y;
            low = uint2(firstPixel - .5);
            high = uint2(lastPixel - .5);
        }
        node.MinimumDepth = WaveActiveMin(node.MinimumDepth);
        node.MaximumDepth = WaveActiveMax(node.MaximumDepth);
        low = WaveActiveMin(low); high = WaveActiveMax(high);
        node.MinimumPixel = low.x | (low.y << 16u); node.MaximumPixel = high.x | (high.y << 16u);
        if (lane == 0u) TransparencyDepthNodes[tree.x + tree.y + leaf] = node;
    }
    DeviceMemoryBarrierWithGroupSync();
    for (uint width = tree.y >> 1u; width > 0u; width >>= 1u)
    {
        for (uint item = width + thread; item < width * 2u; item += 128u)
        {
            TransparencyDepthNode a = TransparencyDepthNodes[tree.x + item * 2u], b = TransparencyDepthNodes[tree.x + item * 2u + 1u];
            TransparencyDepthNode node;
            node.MinimumDepth = min(a.MinimumDepth, b.MinimumDepth); node.MaximumDepth = max(a.MaximumDepth, b.MaximumDepth);
            uint2 low = min(uint2(a.MinimumPixel & 65535u, a.MinimumPixel >> 16u), uint2(b.MinimumPixel & 65535u, b.MinimumPixel >> 16u));
            uint2 high = max(uint2(a.MaximumPixel & 65535u, a.MaximumPixel >> 16u), uint2(b.MaximumPixel & 65535u, b.MaximumPixel >> 16u));
            node.MinimumPixel = low.x | (low.y << 16u); node.MaximumPixel = high.x | (high.y << 16u);
            TransparencyDepthNodes[tree.x + item] = node;
        }
        DeviceMemoryBarrierWithGroupSync();
    }
}

#include "cluster_transparency_depth.slang"

uint NextTransparencyNode(uint node)
{
    while ((node & 1u) != 0u) node >>= 1u;
    return node == 0u ? 0u : node + 1u;
}

uint TransposeTransparencyMask(uint mask, uint lane)
{
    [unroll]
    for (uint width = 16u; width != 0u; width >>= 1u)
    {
        uint peer = WaveReadLaneAt(mask, lane ^ width);
        uint bits = width == 16u ? 0x0000FFFFu : width == 8u ? 0x00FF00FFu : width == 4u ? 0x0F0F0F0Fu : width == 2u ? 0x33333333u : 0x55555555u;
        mask = (lane & width) == 0u ? (mask & bits) | ((peer & bits) << width)
            : (mask & ~bits) | ((peer & ~bits) >> width);
    }
    return mask;
}

struct TransparencyRasterPacket
{
    uint Id, Coverage;
    float3 Plane;
};

uint LoadTransparencyHardwareMask(uint id, uint2 origin, uint2 tile, ClusteredLightGridParameters grid)
{
    uint4 range = TransparencyCoverageRanges[id];
    uint association = range.x + (tile.y - range.z) * range.w + tile.x - range.y;
    uint2 local = origin % uint2(grid.TileSizeX,grid.TileSizeY);
    uint first = association * 8u + local.y / 2u;
    uint2 rowPairs = uint2(TransparencyHardwareMasks[first],TransparencyHardwareMasks[first + 1u]) >> local.x;
    rowPairs = (rowPairs & 255u) | ((rowPairs >> 8u) & 65280u);
    return rowPairs.x | (rowPairs.y << 16u);
}

void FinalizeTransparencyTiles()
{
    if (TransparencyRequestsUseTiles())
    {
        uint tiles = TransparencyCellWork.Load(12u);
        WriteTransparencyDispatch(160u, uint3(min(tiles, 65535u), (tiles + 65534u) / 65535u, 1u));
    }
    uint general = TransparencyTileWork.Load(16u) * 4u, depth = TransparencyTileWork.Load(20u) * 32u;
    WriteTransparencyDispatch(100u,uint3(min(general,65535u),(general + 65534u) / 65535u,1u));
    WriteTransparencyDispatch(136u,uint3(min(depth,65535u),(depth + 65534u) / 65535u,1u));
    uint local = TransparencyTileWork.Load(28u) * 2u;
    WriteTransparencyDispatch(112u,uint3(min(local,65535u),(local + 65534u) / 65535u,1u));
}

void ResolveTransparencyDepthTiles<TMaterials : ITransparencyMaterials>(uniform TMaterials materials,
    uint job, uint block, uint thread)
{
    uint lane = WaveGetLaneIndex();
    uint tileIndex = job;
    ClusteredLightGridParameters grid = TransparencyLightingViews[0];
    uint view = tileIndex / (grid.TileCountX * grid.TileCountY);
    grid = TransparencyLightingViews[view];
    uint tileKey = tileIndex;
    uint4 partition = LoadTransparencyTilePartition(tileKey);
    bool adaptive = partition.z > 1u;
    tileIndex %= grid.TileCountX * grid.TileCountY;
    uint2 tile = uint2(tileIndex % grid.TileCountX,tileIndex / grid.TileCountX);
    uint local = block * 8u + thread / 32u;
    uint3 pixel = uint3(tile * uint2(grid.TileSizeX,grid.TileSizeY) + uint2(local & 15u,local >> 4u),view);
    if (pixel.x >= TransparencyUniforms.Width || pixel.y >= TransparencyUniforms.Height) return;
    float opaque = TransparencyUniforms.UseIndex == 3u ? 1.0 : TransparencyOpaqueDepth.Load(int4(pixel,0));
    uint lastSlice = LastTransparencyTileSlice(partition,adaptive,view,float2(pixel.xy) + .5,opaque);
    float4 background = TransparencyColor[pixel];
    float3 color = 0.0.xxx;
    float transmission = 1.0;
    bool running = true, contributed = false;
    for (uint slice = 0u; slice <= lastSlice && running; slice++)
    {
        uint cell = TransparencyCell(view,uint3(tile,slice));
        uint4 range = LoadTransparencyTileCell(tileKey,view,tile,slice,adaptive);
        uint first = range.x, count = range.y;
        for (uint start = 0u; start < count && running; start += 32u)
        {
            uint item = start + lane;
            uint id = item < count ? ReadTransparencyReference(first + item,adaptive) : 0u;
            TransparencyTriangle triangle = TransparencyRasterTriangles[id];
            float depth = triangle.Depths.x + dot(float2(pixel.xy) + .5 - triangle.First,triangle.DepthPlane.yz);
            bool covered = item < count;
            if (TransparencyUniforms.UseIndex == 3u)
            {
                if (covered)
                {
                    uint4 range = TransparencyCoverageRanges[id];
                    uint association = range.x + (tile.y - range.z) * range.w + tile.x - range.y;
                    uint bit = (pixel.y & 15u) * 16u + (pixel.x & 15u);
                    covered = (TransparencyHardwareMasks[association * 8u + bit / 32u] & (1u << (bit & 31u))) != 0u;
                }
            }
            else covered = covered && TransparencyCovered(triangle,float2(pixel.xy) + .5,depth);
            float4 surface = 0.0.xxxx;
            if (covered && TransparencySliceAccepts(partition,adaptive,slice,depth) && TransparencyDepthHit(pixel,triangle,cell,opaque,depth))
            {
                TMaterials.Shading shading = materials.readShadingLane(materials.evaluateShading(id),lane);
                float resultDepth;
                if (materials.coverShading(triangle.Bin,triangle.Source,shading,triangle.Domain,pixel,depth,resultDepth) &&
                    TransparencyDepthHit(pixel,triangle,cell,opaque,resultDepth) && TransparencySliceAccepts(partition,adaptive,slice,resultDepth))
                    surface = materials.shadeShading(triangle.Bin,triangle.Source,shading,pixel,resultDepth);
            }
            float alpha = saturate(surface.a);
            float3 blockColor = alpha * surface.rgb;
            float blockTransmission = 1.0 - alpha;
            [unroll]
            for (uint stride = 1u; stride < 32u; stride <<= 1u)
            {
                float3 otherColor = WaveReadLaneAt(blockColor,min(lane + stride,31u));
                float otherTransmission = WaveReadLaneAt(blockTransmission,min(lane + stride,31u));
                if ((lane & (stride * 2u - 1u)) == 0u && lane + stride < 32u)
                {
                    blockColor += blockTransmission * otherColor;
                    blockTransmission *= otherTransmission;
                }
            }
            float3 front = WaveReadLaneAt(blockColor,0u);
            float remainder = WaveReadLaneAt(blockTransmission,0u);
            color += transmission * front;
            transmission *= remainder;
            contributed = contributed || remainder < 1.0;
            float limit = max(TransparencyUniforms.ColorLimit,max(background.r,max(background.g,background.b)));
            if (TransparencyUniforms.MaximumError > 0.0 && transmission * limit * .5 <= TransparencyUniforms.MaximumError)
            { color += transmission * limit * .5; transmission = 0.0; running = false; }
        }
    }
    if (lane == 0u && contributed) TransparencyColor[pixel] = float4(color + transmission * background.rgb,background.a);
}

TransparencyRasterPacket LoadTransparencyRasterPacket(uint first, uint count, uint item, uint2 origin, uint2 tile, uint view, bool adaptive)
{
    TransparencyRasterPacket packet;
    packet.Id = item < count ? ReadTransparencyReference(first + item,adaptive) : 0u;
    TransparencyTriangle triangle = TransparencyRasterTriangles[packet.Id];
    uint mask = 0u;
    if (item < count)
    {
        if (TransparencyUniforms.UseIndex == 3u)
        {
            ClusteredLightGridParameters grid = TransparencyLightingViews[view];
            mask = LoadTransparencyHardwareMask(packet.Id,origin,tile,grid);
        }
        else
        {
            float2 vertices[3] = { triangle.First, triangle.Second, triangle.Third };
            if (all(max(triangle.First, max(triangle.Second, triangle.Third)) >= float2(origin) + .5) &&
                all(min(triangle.First, min(triangle.Second, triangle.Third)) <= float2(origin) + float2(7.5,3.5)))
                mask = ClusterRasterTileCoverage(vertices, triangle.InverseArea < 0.0 ? 1 : -1,
                    int2(origin), int2(min(uint2(8u,4u), uint2(TransparencyUniforms.Width, TransparencyUniforms.Height) - origin)));
        }
    }
    packet.Coverage = TransposeTransparencyMask(mask, WaveGetLaneIndex());
    packet.Plane = float3(triangle.Depths.x + dot(float2(origin) + .5 - triangle.First, triangle.DepthPlane.yz), triangle.DepthPlane.yz);
    return packet;
}

#include "cluster_transparency_local.slang"



uint FindTransparencyCoveredBit(uint mask, uint rank)
{
    if (rank == 0u) return uint(firstbitlow(mask));
    uint bit = 0u;
    [unroll] for (uint width = 16u; width != 0u; width >>= 1u)
    {
        uint lowerCount = countbits(mask & ((1u << width) - 1u));
        if (rank >= lowerCount)
        {
            rank -= lowerCount;
            bit += width;
            mask >>= width;
        }
    }
    return bit;
}

uint GetTransparencyHitAddress(uint item, uint thread)
{
    // Spread a pixel's consecutive hits across the wave's shared-memory banks.
    return item * 64u + (thread ^ item);
}

bool TransparencyQueuedReferenceBefore(float depth, TransparencyTriangle triangle, float otherDepth,
    uint otherReference, uint first, bool adaptive)
{
    if (depth != otherDepth) return depth < otherDepth;
    if (otherReference == 0xFFFFFFFFu) return true;
    TransparencyTriangle other = TransparencyRasterTriangles[ReadTransparencyReference(first + otherReference,adaptive)];
    return TransparencyBefore(depth,triangle.Source,triangle.Primitive,otherDepth,other.Source,other.Primitive);
}

void ResolveTransparencyTiles<TMaterials : ITransparencyMaterials>(uniform TMaterials materials, uint3 group, uint thread)
{
    uint lane = WaveGetLaneIndex();
    uint job = group.x + group.y * 65535u;
    if (job >= TransparencyTileWork.Load(16u) * 4u) return;
    uint tileIndex = TransparencyTileWork.Load(32u + (job / 4u) * 4u);
    ClusteredLightGridParameters grid = TransparencyLightingViews[0];
    uint view = tileIndex / (grid.TileCountX * grid.TileCountY);
    grid = TransparencyLightingViews[view];
    uint tileKey = tileIndex;
    uint4 partition = LoadTransparencyTilePartition(tileKey);
    bool adaptive = partition.z > 1u;
    tileIndex %= grid.TileCountX * grid.TileCountY;
    uint2 tile = uint2(tileIndex % grid.TileCountX, tileIndex / grid.TileCountX);
    uint2 origin = tile * uint2(grid.TileSizeX, grid.TileSizeY) + uint2((thread / 32u) * 8u, (job & 3u) * 4u);
    uint3 pixel = uint3(origin + uint2(lane & 7u, lane >> 3u), view);
    if (origin.x >= TransparencyUniforms.Width || origin.y >= TransparencyUniforms.Height) return;
    bool valid = pixel.x < TransparencyUniforms.Width && pixel.y < TransparencyUniforms.Height;
    float opaque = valid ? (TransparencyUniforms.UseIndex == 3u ? 1.0 : TransparencyOpaqueDepth.Load(int4(pixel,0))) : -1.0;
    uint lastSlice = LastTransparencyTileSlice(partition,adaptive,pixel.z,float2(pixel.xy) + .5,opaque);
    lastSlice = WaveActiveMax(lastSlice);
    float4 background = valid ? TransparencyColor[pixel] : 0.0.xxxx;
    float3 color = 0.0.xxx;
    float transmission = 1.0, previousDepth = -1.0;
    uint previousSource = 0u, previousPrimitive = 0u;
    bool running = valid, contributed = false;
    for (uint slice = 0u; slice <= lastSlice && WaveActiveAnyTrue(running); slice++)
    {
        uint cell = TransparencyCell(pixel.z, uint3(tile, slice));
        uint4 range = LoadTransparencyTileCell(tileKey,pixel.z,tile,slice,adaptive);
        uint first = range.x, count = range.y;
        if (count == 0u) continue;
        uint2 tree = range.zw;
        if ((tree.y & 0x80000000u) != 0u)
        {
            for (uint firstItem = 0u; firstItem < count && WaveActiveAnyTrue(running); firstItem += 32u)
            {
                TransparencyRasterPacket packet = LoadTransparencyRasterPacket(first, count, firstItem + lane, origin, tile, pixel.z, adaptive);
                TMaterials.Shading shading = materials.evaluateShading(packet.Id);
                uint candidates = running ? packet.Coverage : 0u;
                while (WaveActiveAnyTrue(candidates != 0u))
                {
                    uint selected = candidates != 0u ? uint(firstbitlow(candidates)) : 0u;
                    uint id = WaveReadLaneAt(packet.Id, selected);
                    float3 plane = WaveReadLaneAt(packet.Plane, selected);
                    float depth = plane.x + dot(float2(lane & 7u, lane >> 3u), plane.yz);
                    TMaterials.Shading selectedShading = materials.readShadingLane(shading,selected);
                    if (candidates != 0u)
                    {
                        TransparencyTriangle triangle = TransparencyRasterTriangles[id];
                        float coveredDepth;
                        if (materials.coverShading(triangle.Bin,triangle.Source,selectedShading,triangle.Domain,pixel,depth,coveredDepth) &&
                            TransparencyDepthHit(pixel,triangle,cell,opaque,coveredDepth) && TransparencySliceAccepts(partition,adaptive,slice,coveredDepth))
                        {
                            float4 surface = materials.shadeShading(triangle.Bin,triangle.Source,selectedShading,pixel,coveredDepth);
                            float alpha = saturate(surface.a);
                            color += transmission * alpha * surface.rgb;
                            transmission *= 1.0 - alpha;
                            contributed = true;
                            float limit = max(TransparencyUniforms.ColorLimit, max(background.r, max(background.g, background.b)));
                            if (TransparencyUniforms.MaximumError > 0.0 && transmission * limit * .5 <= TransparencyUniforms.MaximumError)
                            { color += transmission * limit * .5; transmission = 0.0; running = false; }
                        }
                    }
                    candidates = running ? candidates & (candidates - 1u) : 0u;
                }
            }
            continue;
        }
        bool more = true;
        uint firstRemainingReference = 0u;
        while (WaveActiveAnyTrue(more && running))
        {
            uint lastHitAddress = GetTransparencyHitAddress(31u,thread);
            TransparencyHitDepths[lastHitAddress] = 2.0;
            TransparencyHitIds[lastHitAddress] = 0xFFFFFFFFu;
            uint hits = 0u;
            bool linear = (tree.y & 0x40000000u) != 0u;
            uint nodeIndex = firstRemainingReference == 0u ? 1u : tree.y + firstRemainingReference / 32u;
            uint linearFirst = firstRemainingReference;
            uint nextReference = count;
            while (linear ? linearFirst < count : nodeIndex != 0u)
            {
                uint item;
                if (linear) item = linearFirst + lane;
                else
                {
                    TransparencyDepthNode node = TransparencyDepthNodes[tree.x + nodeIndex];
                    uint2 low = uint2(node.MinimumPixel & 65535u, node.MinimumPixel >> 16u);
                    uint2 high = uint2(node.MaximumPixel & 65535u, node.MaximumPixel >> 16u);
                    bool remaining = running && more && all(pixel.xy >= low) && all(pixel.xy <= high) &&
                        node.MaximumDepth >= previousDepth && node.MinimumDepth <= opaque;
                    bool needed = remaining && node.MinimumDepth <= TransparencyHitDepths[lastHitAddress];
                    if (!WaveActiveAnyTrue(needed))
                    {
                        if (remaining)
                        {
                            uint leaf = nodeIndex << uint(firstbithigh(tree.y) - firstbithigh(nodeIndex));
                            nextReference = min(nextReference,(leaf - tree.y) * 32u);
                        }
                        nodeIndex = NextTransparencyNode(nodeIndex);
                        continue;
                    }
                    if (nodeIndex < tree.y) { nodeIndex *= 2u; continue; }
                    item = (nodeIndex - tree.y) * 32u + lane;
                }
                uint id = item < count ? ReadTransparencyReference(first + item,adaptive) : 0u;
                TransparencyTriangle triangle = TransparencyRasterTriangles[id];
                uint mask = 0u;
                if (item < count)
                {
                    if (TransparencyUniforms.UseIndex == 3u)
                    {
                        mask = LoadTransparencyHardwareMask(id,origin,tile,grid);
                    }
                    else
                    {
                        float2 vertices[3] = { triangle.First, triangle.Second, triangle.Third };
                        if (all(max(triangle.First, max(triangle.Second, triangle.Third)) >= float2(origin) + .5) &&
                            all(min(triangle.First, min(triangle.Second, triangle.Third)) <= float2(origin) + float2(7.5,3.5)))
                            mask = ClusterRasterTileCoverage(vertices, triangle.InverseArea < 0.0 ? 1 : -1,
                                int2(origin), int2(min(uint2(8u,4u), uint2(TransparencyUniforms.Width, TransparencyUniforms.Height) - origin)));
                    }
                }
                uint candidates = TransposeTransparencyMask(mask, lane);
                float2 plane = triangle.DepthPlane.yz;
                float baseDepth = triangle.Depths.x + dot(float2(origin) + .5 - triangle.First,plane);
                if (!running || !more) candidates = 0u;
                while (WaveActiveAnyTrue(candidates != 0u))
                {
                    uint selected = candidates != 0u ? uint(firstbitlow(candidates)) : 0u;
                    float depth = WaveReadLaneAt(baseDepth, selected) + dot(float2(lane & 7u, lane >> 3u), WaveReadLaneAt(plane, selected));
                    uint candidate = WaveReadLaneAt(id, selected);
                    uint reference = item - lane + selected;
                    if (candidates != 0u && depth > TransparencyHitDepths[lastHitAddress] &&
                        depth >= previousDepth && depth <= opaque + 1e-6)
                        nextReference = min(nextReference,reference);
                    if (candidates != 0u && depth <= TransparencyHitDepths[lastHitAddress] && depth >= previousDepth && depth <= opaque + 1e-6)
                    {
                        TransparencyTriangle hit = TransparencyRasterTriangles[candidate];
                        if (TransparencyBefore(previousDepth, previousSource, previousPrimitive, depth, hit.Source, hit.Primitive) &&
                            TransparencyMaterialHit(materials, pixel, hit, cell, opaque, depth, candidate) && TransparencySliceAccepts(partition,adaptive,slice,depth))
                        {
                            uint position = min(hits,31u);
                            hits++;
                            uint address = GetTransparencyHitAddress(position,thread);
                            bool insert = hits <= 32u || TransparencyQueuedReferenceBefore(depth,hit,
                                TransparencyHitDepths[address],TransparencyHitIds[address],first,adaptive);
                            if (hits > 32u)
                                nextReference = min(nextReference,insert ? TransparencyHitIds[address] : reference);
                            if (insert)
                            {
                                // Cell references already follow depth order. Append ordered
                                // hits and move only the suffix crossed by an intersection.
                                while (position > 0u)
                                {
                                    uint previous = GetTransparencyHitAddress(position - 1u,thread);
                                    float otherDepth = TransparencyHitDepths[previous];
                                    uint otherReference = TransparencyHitIds[previous];
                                    if (!TransparencyQueuedReferenceBefore(depth,hit,otherDepth,otherReference,first,adaptive)) break;
                                    TransparencyHitDepths[address] = otherDepth;
                                    TransparencyHitIds[address] = otherReference;
                                    position--; address = previous;
                                }
                                TransparencyHitDepths[address] = depth;
                                TransparencyHitIds[address] = reference;
                            }
                        }
                    }
                    candidates &= candidates - 1u;
                }
                if (linear) linearFirst += 32u; else nodeIndex = NextTransparencyNode(nodeIndex);
            }
            // Continue at the first reference still needed by this wave. Queue
            // evictions and skipped depth nodes both retain their first input.
            firstRemainingReference = WaveActiveMin(nextReference) & ~31u;
            more = nextReference < count;
            uint queued = min(hits,32u);
            uint totalHits = WaveActiveSum(queued), maximumHits = WaveActiveMax(queued);
            uint compactBatches = (totalHits + 31u) / 32u;
            bool compact = compactBatches < maximumHits;
            uint firstHit = 0u, occupiedPixels = 0u;
            if (compact)
            {
                GroupMemoryBarrier();
                firstHit = WavePrefixSum(queued);
                occupiedPixels = WaveActiveBallot(queued != 0u).x;
            }
            uint batches = compact ? compactBatches : maximumHits;
            // Full waves retain their pixel lanes. Uneven queues use fewer
            // shading batches by assigning consecutive hits to all lanes.
            for (uint batch = 0u; batch < batches; batch++)
            {
                uint pixelLane = lane, item = batch;
                bool covered = item < queued;
                uint batchFirst = batch * 32u;
                if (compact)
                {
                    uint hit = batchFirst + lane;
                    bool firstRange = all(bool2(queued != 0u,firstHit <= batchFirst));
                    uint firstPixel = WaveActiveMax(select(firstRange,lane,0u));
                    // Pixel starts identify consecutive ranges in this batch.
                    bool newRange = all(bool3(queued != 0u,firstHit > batchFirst,firstHit < batchFirst + 32u));
                    uint pixelStarts = WaveActiveBitOr(select(newRange,1u << ((firstHit - batchFirst) & 31u),0u));
                    uint rank = countbits(pixelStarts & (0xFFFFFFFFu >> (31u - lane)));
                    pixelLane = FindTransparencyCoveredBit(occupiedPixels & (0xFFFFFFFFu << firstPixel),rank);
                    item = hit - WaveReadLaneAt(firstHit,pixelLane);
                    covered = hit < totalHits;
                }
                float4 surface = 0.0.xxxx;
                if (covered)
                {
                    uint address = GetTransparencyHitAddress(item,thread - lane + pixelLane);
                    uint index = ReadTransparencyReference(first + TransparencyHitIds[address],adaptive);
                    float depth = TransparencyHitDepths[address];
                    TransparencyTriangle triangle = TransparencyRasterTriangles[index];
                    uint3 samplePixel = uint3(origin + uint2(pixelLane & 7u,pixelLane >> 3u),view);
                    surface = materials.shadeTriangle(triangle.Bin,index,samplePixel,depth);
                }
                if (compact)
                    CompositeTransparencyWaveBatch(surface,pixelLane,firstHit,queued,batchFirst,color,transmission);
                else if (covered)
                {
                    float alpha = saturate(surface.a);
                    color += transmission * alpha * surface.rgb;
                    transmission *= 1.0 - alpha;
                }
            }
            if (queued != 0u)
            {
                uint address = GetTransparencyHitAddress(queued - 1u,thread);
                uint index = ReadTransparencyReference(first + TransparencyHitIds[address],adaptive);
                float depth = TransparencyHitDepths[address];
                TransparencyTriangle triangle = TransparencyRasterTriangles[index];
                previousDepth = depth; previousSource = triangle.Source; previousPrimitive = triangle.Primitive;
                contributed = true;
            }
            float limit = max(TransparencyUniforms.ColorLimit, max(background.r, max(background.g, background.b)));
            if (TransparencyUniforms.MaximumError > 0.0 && transmission * limit * .5 <= TransparencyUniforms.MaximumError)
            { color += transmission * limit * .5; transmission = 0.0; running = false; }
        }
    }
    if (valid && contributed) TransparencyColor[pixel] = float4(color + transmission * background.rgb, background.a);
}

void ResolveTransparencyDepthTileJobs<TMaterials : ITransparencyMaterials>(uniform TMaterials materials, uint3 group, uint thread)
{
    uint job = group.x + group.y * 65535u;
    if (job >= TransparencyTileWork.Load(20u) * 32u) return;
    uint tile = TransparencyTileWork.Load(32u + (TransparencyUniforms.CellCount + job / 32u) * 4u);
    ResolveTransparencyDepthTiles(materials,tile,job % 32u,thread);
}

#endif
