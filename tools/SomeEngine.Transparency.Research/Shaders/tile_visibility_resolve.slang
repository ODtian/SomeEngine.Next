#include "tile_visibility_common.slang"

RWTexture2DArray<float4> SceneColor;
#ifdef TILE_SOFTWARE_RASTER
#define TILE_CACHE_COUNT 256
groupshared float CachedEdges[TILE_CACHE_COUNT * 9];
#else
#define TILE_CACHE_COUNT 256
groupshared uint CachedMasks[TILE_CACHE_COUNT * TILE_MASK_WORDS];
#endif
groupshared float4 CachedDepth[TILE_CACHE_COUNT];
groupshared uint2 CachedColors[TILE_CACHE_COUNT];
groupshared uint SortedTriangles[TILE_CACHE_COUNT];
// Loading packs the visible count and wave thread ranges into one atomic word.
// Sorting then reuses both words as triangle references.
#define CachedCounts SortedTriangles[0]
#define PartialWaves SortedTriangles[1]

void InsertHit(float depth, uint triangleId, uint colorIndex, uint entryIndex,
    inout float4 depths, inout uint4 triangleIds, inout uint4 colorIndices, inout uint4 entryIndices)
{
    if (depth < depths.x || (depth == depths.x && triangleId < triangleIds.x))
    {
        depths = float4(depth, depths.xyz);
        triangleIds = uint4(triangleId, triangleIds.xyz);
        colorIndices = uint4(colorIndex, colorIndices.xyz);
        entryIndices = uint4(entryIndex, entryIndices.xyz);
    }
    else if (depth < depths.y || (depth == depths.y && triangleId < triangleIds.y))
    {
        depths = float4(depths.x, depth, depths.yz);
        triangleIds = uint4(triangleIds.x, triangleId, triangleIds.yz);
        colorIndices = uint4(colorIndices.x, colorIndex, colorIndices.yz);
        entryIndices = uint4(entryIndices.x, entryIndex, entryIndices.yz);
    }
    else if (depth < depths.z || (depth == depths.z && triangleId < triangleIds.z))
    {
        depths = float4(depths.xy, depth, depths.z);
        triangleIds = uint4(triangleIds.xy, triangleId, triangleIds.z);
        colorIndices = uint4(colorIndices.xy, colorIndex, colorIndices.z);
        entryIndices = uint4(entryIndices.xy, entryIndex, entryIndices.z);
    }
    else if (depth < depths.w || (depth == depths.w && triangleId < triangleIds.w))
    {
        depths.w = depth;
        triangleIds.w = triangleId;
        colorIndices.w = colorIndex;
        entryIndices.w = entryIndex;
    }
}

[shader("compute")]
[numthreads(16, 16, 1)]
void ResolveTileVisibility(uint3 groupId : SV_GroupID, uint3 groupThread : SV_GroupThreadID)
{
    uint lane = groupThread.y * TILE_PIXEL_SIZE + groupThread.x;
    uint2 range = TileRanges[groupId.y * Settings.TileColumns + groupId.x];
    if (range.y == 0) return;
    bool cached = range.y <= TILE_CACHE_COUNT;
    if (cached)
    {
        if (lane == 0)
        {
            CachedCounts = 0;
            PartialWaves = 0;
        }
        GroupMemoryBarrierWithGroupSync();
        uint sortThread = 0;
        uint waveSize = WaveGetLaneCount();
        uint waveThreads = WaveActiveCountBits(true);
        float2 center = PixelPoint(groupId.xy * TILE_PIXEL_SIZE) + float2(15.0 / Settings.Width, -15.0 / Settings.Height);
        {
            uint index = lane;
            uint sourceTriangle = 0;
            bool visible = false;
#ifdef TILE_SOFTWARE_RASTER
            ProjectedTriangle coverage;
#else
            uint4 masks0 = 0, masks1 = 0;
#endif
            if (index < range.y)
            {
                sourceTriangle = TileAssociations[range.x + index];
#ifdef TILE_SOFTWARE_RASTER
                coverage = Coverage[sourceTriangle];
                float2 extent = float2(15.0 / Settings.Width, 15.0 / Settings.Height);
                float3 edgeMaximum = float3(dot(coverage.EdgeA.xyz, float3(center, 1)) + dot(abs(coverage.EdgeA.xy), extent),
                    dot(coverage.EdgeB.xyz, float3(center, 1)) + dot(abs(coverage.EdgeB.xy), extent),
                    dot(coverage.EdgeC.xyz, float3(center, 1)) + dot(abs(coverage.EdgeC.xy), extent));
                visible = all(edgeMaximum >= -1e-5);
#else
                uint first = CoverageAssociation(TileTriangles[sourceTriangle], groupId.xy) * TILE_MASK_WORDS;
                masks0 = uint4(CoverageMasks[first], CoverageMasks[first + 1], CoverageMasks[first + 2], CoverageMasks[first + 3]);
                masks1 = uint4(CoverageMasks[first + 4], CoverageMasks[first + 5], CoverageMasks[first + 6], CoverageMasks[first + 7]);
                visible = any(masks0 != 0) || any(masks1 != 0);
#endif
            }
            uint count = WaveActiveCountBits(visible), previousCounts = 0;
            if (WaveIsFirstLane())
            {
                InterlockedAdd(CachedCounts, count | (waveSize << 16), previousCounts);
                if (waveThreads != waveSize) InterlockedOr(PartialWaves, 1);
            }
            previousCounts = WaveReadLaneFirst(previousCounts);
            sortThread = (previousCounts >> 16) + WaveGetLaneIndex();
            uint destination = (previousCounts & 65535) + WavePrefixCountBits(visible);
            if (visible)
            {
                TileTriangleData triangle = TileTriangles[sourceTriangle];
                float minimum = dot(triangle.DepthPlane, float3(center, 1)) -
                    abs(triangle.DepthPlane.x) * (15.0 / Settings.Width) -
                    abs(triangle.DepthPlane.y) * (15.0 / Settings.Height) - 1e-5;
                CachedDepth[destination] = float4(triangle.DepthPlane, minimum);
#ifdef TILE_SOFTWARE_RASTER
                [unroll] for (uint axis = 0; axis < 3; axis++)
                {
                    CachedEdges[destination * 9 + axis] = coverage.EdgeA[axis];
                    CachedEdges[destination * 9 + axis + 3] = coverage.EdgeB[axis];
                    CachedEdges[destination * 9 + axis + 6] = coverage.EdgeC[axis];
                }
#else
                [unroll] for (uint word = 0; word < 4; word++)
                {
                    CachedMasks[destination * TILE_MASK_WORDS + word] = masks0[word];
                    CachedMasks[destination * TILE_MASK_WORDS + word + 4] = masks1[word];
                }
#endif
                CachedColors[destination] = uint2(triangle.TriangleId, triangle.ColorIndex);
            }
        }
        GroupMemoryBarrierWithGroupSync();
        range.y = CachedCounts & 65535;
        if (range.y == 0) return;
        bool fullWaves = PartialWaves == 0;
        if (!fullWaves) sortThread = lane;
        uint sortCount = 1u << (firstbithigh(max(range.y - 1, 1u)) + 1);
        uint sortTriangle = sortThread;
        float sortDepth = sortThread < range.y ? CachedDepth[sortThread].w : 3.402823e38;
        [unroll] for (uint width = 2; width <= TILE_CACHE_COUNT; width *= 2)
        {
            if (width > sortCount) break;
            [unroll] for (uint distance = width / 2; distance > 0; distance /= 2)
            {
                uint otherTriangle;
                float otherDepth;
                if (fullWaves && distance < waveSize)
                {
                    uint otherLane = WaveGetLaneIndex() ^ distance;
                    otherTriangle = WaveReadLaneAt(sortTriangle, otherLane);
                    otherDepth = WaveReadLaneAt(sortDepth, otherLane);
                }
                else
                {
                    GroupMemoryBarrierWithGroupSync();
                    SortedTriangles[sortThread] = sortTriangle;
                    GroupMemoryBarrierWithGroupSync();
                    otherTriangle = SortedTriangles[sortThread ^ distance];
                    otherDepth = otherTriangle < range.y ? CachedDepth[otherTriangle].w : 3.402823e38;
                }
                bool ascending = ((sortThread & width) == 0) == ((sortThread & distance) == 0);
                bool swap = ascending ? otherDepth < sortDepth : otherDepth > sortDepth;
                if (swap)
                {
                    sortTriangle = otherTriangle;
                    sortDepth = otherDepth;
                }
            }
        }
        GroupMemoryBarrierWithGroupSync();
        SortedTriangles[sortThread] = sortTriangle;
        GroupMemoryBarrierWithGroupSync();
    }

    uint2 pixel = groupId.xy * TILE_PIXEL_SIZE + groupThread.xy;
    if (any(pixel >= uint2(Settings.Width, Settings.Height))) return;
    float2 point = PixelPoint(pixel);
#ifdef TILE_SOFTWARE_RASTER
    float opaque = 1 - OpaqueDepth.Load(int4(pixel, 0, 0));
#endif
    float4 background = SceneColor[uint3(pixel, 0)];
    float3 upper = max(1.0, background.rgb);
    float remainingError = max(upper.x, max(upper.y, upper.z)) * .5;
    float3 color = 0;
    float transmittance = 1;
    float previousDepth = -1;
    uint previousId = 0;
    uint firstTriangle = 0;
    while (firstTriangle < range.y)
    {
        float4 depths = 3.402823e38;
        uint4 triangleIds = 0xffffffff;
        uint4 colorIndices = 0;
        uint4 entryIndices = 0xffffffff;
        uint firstDeferred = range.y;
        for (uint index = firstTriangle; index < range.y; index++)
        {
            float3 plane;
            uint2 attributes;
#ifdef TILE_SOFTWARE_RASTER
            ProjectedTriangle coverage;
#else
            uint mask;
#endif
            if (cached)
            {
                uint triangle = SortedTriangles[index];
                float4 values = CachedDepth[triangle];
#ifdef TILE_SOFTWARE_RASTER
                if (values.w > opaque) break;
#endif
                if (values.w > depths.w) { firstDeferred = min(firstDeferred, index); break; }
                plane = values.xyz;
#ifdef TILE_SOFTWARE_RASTER
                uint firstEdge = triangle * 9;
                coverage.EdgeA = float4(CachedEdges[firstEdge], CachedEdges[firstEdge + 1], CachedEdges[firstEdge + 2], 0);
                coverage.EdgeB = float4(CachedEdges[firstEdge + 3], CachedEdges[firstEdge + 4], CachedEdges[firstEdge + 5], 0);
                coverage.EdgeC = float4(CachedEdges[firstEdge + 6], CachedEdges[firstEdge + 7], CachedEdges[firstEdge + 8], 0);
#else
                mask = CachedMasks[triangle * TILE_MASK_WORDS + lane / 32];
#endif
                attributes = CachedColors[triangle];
            }
            else
            {
                uint sourceTriangle = TileAssociations[range.x + index];
                TileTriangleData triangle = TileTriangles[sourceTriangle];
                plane = triangle.DepthPlane;
#ifdef TILE_SOFTWARE_RASTER
                coverage = Coverage[sourceTriangle];
#else
                mask = CoverageMasks[CoverageAssociation(triangle, groupId.xy) * TILE_MASK_WORDS + lane / 32];
#endif
                attributes = uint2(triangle.TriangleId, triangle.ColorIndex);
            }
#ifdef TILE_SOFTWARE_RASTER
            float coverageDepth;
            bool covered = Covers(coverage, point, coverageDepth);
            float depth = dot(plane, float3(point, 1));
            if (!covered || depth < 0 || depth > opaque) continue;
#else
            if ((mask & (1u << (lane & 31))) == 0) continue;
            float depth = dot(plane, float3(point, 1));
#endif
            if (depth < previousDepth || (depth == previousDepth && attributes.x <= previousId)) continue;
            if (depths.w < 3.402823e38)
            {
                bool nearer = depth < depths.w || (depth == depths.w && attributes.x < triangleIds.w);
                firstDeferred = min(firstDeferred, nearer ? entryIndices.w : index);
            }
            InsertHit(depth, attributes.x, attributes.y, index, depths, triangleIds, colorIndices, entryIndices);
        }
        if (depths.x == 3.402823e38) break;
        [unroll] for (uint hit = 0; hit < 4; hit++)
        {
            if (depths[hit] == 3.402823e38) break;
            float4 surface = Colors[colorIndices[hit]];
            color += transmittance * surface.a * surface.rgb;
            transmittance *= 1 - surface.a;
            previousDepth = depths[hit];
            previousId = triangleIds[hit];
        }
        if (firstDeferred >= range.y) break;
        if (transmittance * remainingError <= Settings.ColorError)
        {
            SceneColor[uint3(pixel, 0)] = float4(color + transmittance * upper * .5, background.a);
            return;
        }
        firstTriangle = firstDeferred;
    }
    SceneColor[uint3(pixel, 0)] = float4(color + transmittance * background.rgb, background.a);
}
