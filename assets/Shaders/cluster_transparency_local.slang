#ifndef CLUSTER_TRANSPARENCY_LOCAL_SLANG
#define CLUSTER_TRANSPARENCY_LOCAL_SLANG

groupshared TransparencyTriangle TransparencyLocalTriangles[16];
groupshared uint TransparencyLocalIds[16];
groupshared uint TransparencyLocalMasks[128];
groupshared float TransparencyHitDepths[32 * 64];
groupshared uint TransparencyHitIds[32 * 64];
static const uint TransparencyLocalHitCapacity = 8u;
groupshared float TransparencyLocalHitDepths[TransparencyLocalHitCapacity * 128];
groupshared uint TransparencyLocalHitIds[TransparencyLocalHitCapacity * 128];
groupshared uint TransparencyLocalMore;

uint GetTransparencyLocalHitAddress(uint item, uint thread)
{
    return item * 128u + (thread ^ item);
}

void CompositeTransparencyWaveBatch(float4 surface, uint pixelLane, uint firstHit,
    uint queued, uint batchFirst, inout float3 color, inout float transmission)
{
    uint lane = WaveGetLaneIndex();
    uint sampleFirst = max(firstHit,batchFirst) - batchFirst;
    uint samples = firstHit < batchFirst + 32u && firstHit + queued > batchFirst
        ? min(firstHit + queued,batchFirst + 32u) - max(firstHit,batchFirst) : 0u;
    uint sampleCount = WaveActiveMax(samples);
    uint pixelEnd = min(WaveReadLaneAt(firstHit + queued,pixelLane),batchFirst + 32u) - batchFirst;
    float alpha = saturate(surface.a);
    float3 batchColor = alpha * surface.rgb;
    float batchTransmission = 1.0 - alpha;
    for (uint stride = 1u; stride < sampleCount; stride <<= 1u)
    {
        uint peer = min(lane + stride,31u);
        float3 peerColor = WaveReadLaneAt(batchColor,peer);
        float peerTransmission = WaveReadLaneAt(batchTransmission,peer);
        if (lane + stride < pixelEnd)
        {
            batchColor += batchTransmission * peerColor;
            batchTransmission *= peerTransmission;
        }
    }
    float3 sampleColor = WaveReadLaneAt(batchColor,min(sampleFirst,31u));
    float sampleTransmission = WaveReadLaneAt(batchTransmission,min(sampleFirst,31u));
    if (samples != 0u)
    {
        color += transmission * sampleColor;
        transmission *= sampleTransmission;
    }
}

bool TransparencyQueuedHitBefore(float depth, TransparencyTriangle triangle, float otherDepth, uint otherId, bool cached)
{
    if (depth != otherDepth) return depth < otherDepth;
    if (otherId == 0xFFFFFFFFu) return true;
    TransparencyTriangle other = cached ? TransparencyLocalTriangles[otherId] : TransparencyRasterTriangles[otherId];
    return TransparencyBefore(depth,triangle.Source,triangle.Primitive,otherDepth,other.Source,other.Primitive);
}

uint TransparencyLocalCoverage(uint item, uint2 origin, uint view)
{
    TransparencyTriangle triangle = TransparencyLocalTriangles[item];
    if (TransparencyUniforms.UseIndex == 3u)
    {
        uint mask = 0u;
        uint2 local = origin & 15u;
        [unroll] for (uint row = 0u; row < 4u; row++)
        {
            uint bit = (local.y + row) * 16u + local.x;
            mask |= ((TransparencyLocalMasks[item * 8u + bit / 32u] >> (bit & 31u)) & 255u) << (row * 8u);
        }
        return mask;
    }
    float2 vertices[3] = { triangle.First, triangle.Second, triangle.Third };
    return ClusterRasterTileCoverage(vertices, triangle.InverseArea < 0.0 ? 1 : -1, int2(origin),
        int2(min(uint2(8u,4u),uint2(TransparencyUniforms.Width,TransparencyUniforms.Height) - origin)));
}

// Four pixel waves share each geometry packet. The ordered hit queues remain in
// shared memory while material evaluation uses the registers for surface data.
void ResolveTransparencyLocalTile<TMaterials : ITransparencyMaterials>(uniform TMaterials materials,
    uint view, uint2 tile, uint first, uint count, uint halfTile, uint thread)
{
    uint lane = WaveGetLaneIndex();
    bool cached = count <= 16u;
    uint2 origin = tile * 16u + uint2((thread / 32u) % 2u * 8u,(thread / 64u) * 4u + halfTile * 8u);
    uint3 pixel = uint3(origin + uint2(lane & 7u,lane >> 3u),view);
    bool valid = pixel.x < TransparencyUniforms.Width && pixel.y < TransparencyUniforms.Height;
    // Hardware masks already contain the depth-tested samples. Their depth
    // certificate also covers the small interpolation difference in the plane.
    float opaque = valid ? (TransparencyUniforms.UseIndex == 3u ? 1.0 : TransparencyOpaqueDepth.Load(int4(pixel,0))) : -1.0;
    float4 background = valid ? TransparencyColor[pixel] : 0.0.xxxx;
    float3 color = 0.0.xxx;
    float transmission = 1.0, previousDepth = -1.0;
    uint previousSource = 0u, previousPrimitive = 0u;
    bool more = valid, contributed = false;
    while (true)
    {
        uint hits = 0u;
        uint lastHitAddress = GetTransparencyLocalHitAddress(TransparencyLocalHitCapacity - 1u,thread);
        TransparencyLocalHitDepths[lastHitAddress] = 2.0;
        TransparencyLocalHitIds[lastHitAddress] = 0xFFFFFFFFu;
        for (uint start = 0u; start < count; start += 16u)
        {
            uint packetCount = min(16u,count - start);
            if (thread < packetCount)
            {
                uint id = TransparencyReferences[first + start + thread];
                TransparencyTriangle triangle = TransparencyRasterTriangles[id];
                TransparencyLocalTriangles[thread] = triangle;
                TransparencyLocalIds[thread] = id;
                bool needed;
                if (TransparencyUniforms.UseIndex == 3u)
                {
                    uint4 range = TransparencyCoverageRanges[id];
                    uint association = range.x + (tile.y - range.z) * range.w + tile.x - range.y;
                    uint covered = 0u;
                    [unroll] for (uint word = 0u; word < 8u; word++)
                    {
                        uint mask = TransparencyHardwareMasks[association * 8u + word];
                        TransparencyLocalMasks[thread * 8u + word] = mask;
                        if (word / 4u == halfTile) covered |= mask;
                    }
                    needed = covered != 0u;
                }
                else
                    needed = all(max(triangle.First,max(triangle.Second,triangle.Third)) >= float2(tile * 16u + uint2(0u,halfTile * 8u)) + .5) &&
                        all(min(triangle.First,min(triangle.Second,triangle.Third)) <= float2(tile * 16u + uint2(16u,halfTile * 8u + 8u)) - .5);
                if (cached && needed) materials.cacheTileShading(thread,materials.evaluateShading(id));
            }
            GroupMemoryBarrierWithGroupSync();
            uint mask = lane < packetCount && origin.x < TransparencyUniforms.Width && origin.y < TransparencyUniforms.Height
                ? TransparencyLocalCoverage(lane,origin,view) : 0u;
            uint candidates = TransposeTransparencyMask(mask,lane);
            if (!more) candidates = 0u;
            while (WaveActiveAnyTrue(candidates != 0u))
            {
                uint selected = candidates != 0u ? uint(firstbitlow(candidates)) : 0u;
                if (candidates != 0u)
                {
                    TransparencyTriangle triangle = TransparencyLocalTriangles[selected];
                    float depth = triangle.Depths.x + dot(float2(pixel.xy) + .5 - triangle.First,triangle.DepthPlane.yz);
                    if (depth >= previousDepth && depth <= opaque && depth >= 0.0 && depth <= 1.0 &&
                        TransparencyBefore(previousDepth,previousSource,previousPrimitive,depth,triangle.Source,triangle.Primitive))
                    {
                        float coveredDepth;
                        uint id = TransparencyLocalIds[selected];
                        bool covered = cached
                            ? materials.coverShading(triangle.Bin,triangle.Source,materials.loadTileShading(selected),triangle.Domain,pixel,depth,coveredDepth)
                            : materials.coverTriangle(triangle.Bin,id,triangle.Domain,pixel,depth,coveredDepth);
                        if (covered &&
                            coveredDepth >= 0.0 && coveredDepth <= min(opaque,1.0))
                        {
                            depth = coveredDepth;
                            uint index = cached ? selected : id;
                            uint position = min(hits,TransparencyLocalHitCapacity - 1u);
                            hits++;
                            uint address = GetTransparencyLocalHitAddress(position,thread);
                            if (hits <= TransparencyLocalHitCapacity || TransparencyQueuedHitBefore(depth,triangle,TransparencyLocalHitDepths[address],TransparencyLocalHitIds[address],cached))
                            {
                                while (position > 0u)
                                {
                                    uint previous = GetTransparencyLocalHitAddress(position - 1u,thread);
                                    float otherDepth = TransparencyLocalHitDepths[previous];
                                    uint otherId = TransparencyLocalHitIds[previous];
                                    if (!TransparencyQueuedHitBefore(depth,triangle,otherDepth,otherId,cached)) break;
                                    TransparencyLocalHitDepths[address] = otherDepth;
                                    TransparencyLocalHitIds[address] = otherId;
                                    position--; address = previous;
                                }
                                TransparencyLocalHitDepths[address] = depth;
                                TransparencyLocalHitIds[address] = index;
                            }
                        }
                    }
                }
                candidates &= candidates - 1u;
            }
            GroupMemoryBarrierWithGroupSync();
        }
        more = hits > TransparencyLocalHitCapacity;
        uint queued = min(hits,TransparencyLocalHitCapacity);
        uint totalHits = WaveActiveSum(queued), maximumHits = WaveActiveMax(queued);
        uint compactBatches = (totalHits + 31u) / 32u;
        bool compact = compactBatches < maximumHits;
        uint firstHit = 0u;
        if (compact)
        {
            GroupMemoryBarrier();
            firstHit = WavePrefixSum(queued);
        }
        uint batches = compact ? compactBatches : maximumHits;
        for (uint batch = 0u; batch < batches; batch++)
        {
            uint pixelLane = lane, item = batch;
            bool covered = item < queued;
            uint batchFirst = batch * 32u;
            if (compact)
            {
                uint hit = batchFirst + lane;
                uint low = 0u, high = 32u;
                while (low + 1u < high)
                {
                    uint mid = (low + high) >> 1u;
                    if (WaveReadLaneAt(firstHit,mid) <= hit) low = mid; else high = mid;
                }
                pixelLane = low;
                item = hit - WaveReadLaneAt(firstHit,low);
                covered = hit < totalHits;
            }
            float4 surface = 0.0.xxxx;
            if (covered)
            {
                uint address = GetTransparencyLocalHitAddress(item,thread - lane + pixelLane);
                uint index = TransparencyLocalHitIds[address];
                float depth = TransparencyLocalHitDepths[address];
                TransparencyTriangle triangle;
                TMaterials.Shading shading;
                [branch]
                if (cached)
                {
                    triangle = TransparencyLocalTriangles[index];
                    shading = materials.loadTileShading(index);
                }
                else
                {
                    triangle = TransparencyRasterTriangles[index];
                    shading = materials.evaluateShading(index);
                }
                uint3 samplePixel = uint3(origin + uint2(pixelLane & 7u,pixelLane >> 3u),view);
                surface = materials.shadeShading(triangle.Bin,triangle.Source,shading,samplePixel,depth);
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
            uint address = GetTransparencyLocalHitAddress(queued - 1u,thread);
            uint index = TransparencyLocalHitIds[address];
            float depth = TransparencyLocalHitDepths[address];
            TransparencyTriangle triangle = cached ? TransparencyLocalTriangles[index] : TransparencyRasterTriangles[index];
            previousDepth = depth; previousSource = triangle.Source; previousPrimitive = triangle.Primitive;
            contributed = true;
        }
        float limit = max(TransparencyUniforms.ColorLimit,max(background.r,max(background.g,background.b)));
        if (TransparencyUniforms.MaximumError > 0.0 && transmission * limit * .5 <= TransparencyUniforms.MaximumError)
        { color += transmission * limit * .5; transmission = 0.0; more = false; }
        if (thread == 0u) TransparencyLocalMore = 0u;
        GroupMemoryBarrierWithGroupSync();
        bool waveMore = WaveActiveAnyTrue(more);
        if (WaveIsFirstLane() && waveMore) InterlockedOr(TransparencyLocalMore,1u);
        GroupMemoryBarrierWithGroupSync();
        if (TransparencyLocalMore == 0u) break;
    }
    if (valid && contributed) TransparencyColor[pixel] = float4(color + transmission * background.rgb,background.a);
}

void ResolveTransparencyLocalTileJobs<TMaterials : ITransparencyMaterials>(uniform TMaterials materials,
    uint3 group, uint thread)
{
    uint job = group.x + group.y * 65535u;
    if (job >= TransparencyTileWork.Load(28u) * 2u) return;
    uint index = TransparencyTileWork.Load(32u + (TransparencyUniforms.CellCount * 2u + job / 2u) * 4u);
    ClusteredLightGridParameters grid = TransparencyLightingViews[0];
    uint view = index / (grid.TileCountX * grid.TileCountY);
    grid = TransparencyLightingViews[view];
    uint2 tile = uint2(index % grid.TileCountX,(index / grid.TileCountX) % grid.TileCountY);
    uint cell = TransparencyCell(view,uint3(tile,0u));
    ResolveTransparencyLocalTile(materials,view,tile,TransparencyCellOffsets[cell],TransparencyTileCounts[cell],job & 1u,thread);
}

#endif
