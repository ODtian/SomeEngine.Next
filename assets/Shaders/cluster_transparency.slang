#include "cluster_transparency_source.slang"
#include "cluster_instance_properties.slang"
#include "cluster_view.slang"
#include "resource_effects.slang"
#include "cluster_raster_shared.slang"
#include "render_lighting.slang"
#include "wave_aggregation.slang"
#include "cluster_ordered_state.slang"
#include "vsm_receiver_requests.slang"

interface ITransparencyMaterials
{
    associatedtype Projection;
    associatedtype Shading;
    Shading readProjectionShading(uint bin, uint source, Projection value);
    Shading evaluateShading(uint index);
    void cacheTileShading(uint packet, Shading value);
    Shading loadTileShading(uint packet);
    Shading readShadingLane(Shading value, uint lane);
    bool coverShading(uint bin, uint source, Shading value, float3x2 domain, uint3 pixel, float depth, out float resultDepth);
    float4 shadeShading(uint bin, uint source, Shading value, uint3 pixel, float depth);
    Projection beginProjection(uint bin, uint source, uint view, float4 screenBounds);
    bool nextProjection(uint bin, uint source, uint view, inout Projection value,
        out uint primitive, out float3x2 domain, out float4 first, out float4 second, out float4 third);
    bool cover(uint bin, uint source, float3x2 domain, uint3 pixel, float depth, out float resultDepth);
    float4 shade(uint bin, uint source, uint primitive, uint3 pixel, float depth);
    bool coverTriangle(uint bin, uint index, float3x2 domain, uint3 pixel, float depth, out float resultDepth);
    float4 shadeTriangle(uint bin, uint index, uint3 pixel, float depth);
};

struct TransparencyParameters
{
    uint Width, Height, ViewCount, MaterialCapacity;
    uint TriangleCapacity, VisibleCapacity;
    float ColorLimit, MaximumError;
    uint ReferenceCapacity, UseIndex, CellCount, Effects;
};

struct TransparencyTriangle
{
    float2 First, Second, Third;
    float InverseArea;
    float3 Depths;
    uint Source, Bin, View, Primitive;
    // Each row retains the original triangle's barycentric y/z coordinates.
    float3x2 Domain;
    float3 DepthPlane;
};

struct TransparencySourceBounds
{
    uint Source;
    uint3 Minimum, Maximum;
};

ConstantBuffer<TransparencyParameters> TransparencyUniforms;
StructuredBuffer<ClusterViewUniforms> TransparencyViews;
StructuredBuffer<ClusteredLightGridParameters> TransparencyLightingViews;
ByteAddressBuffer TransparencyInstances;
ConstantBuffer<RenderInstancePropertyMetadata> TransparencyProperties;
StructuredBuffer<VisibleCluster> TransparencyVisibleClusters;
ByteAddressBuffer TransparencyPageHeap;
ByteAddressBuffer TransparencyFirstDraw;
ByteAddressBuffer TransparencySecondDraw;
// x: facing in bits 0..1, evaluator + 1 in bits 2..29,
// user depth offset in bit 30, opaque X-ray source in bit 31.
// y: descriptor-array item for that evaluator's direct material resources.
StructuredBuffer<uint2> TransparencyMaterialStates;
Texture2DArray<float> TransparencyOpaqueDepth;
RWTexture2DArray<float4> TransparencyColor;
RWStructuredBuffer<TransparencySource> TransparencySourceOutput;
RWByteAddressBuffer TransparencySourceCounts;
RWStructuredBuffer<uint> TransparencyActiveSources;
RWStructuredBuffer<TransparencySourceBounds> TransparencyProjectedSources;
RWStructuredBuffer<TransparencyTriangle> TransparencyTriangles;
StructuredBuffer<TransparencyTriangle> TransparencyRasterTriangles;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWStructuredBuffer<uint> TransparencyTileCounts;
RWStructuredBuffer<uint> TransparencyCellOffsets;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWStructuredBuffer<uint> TransparencyCellWrites;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWStructuredBuffer<uint> TransparencyCellStreams;
RWStructuredBuffer<uint> TransparencyReferences;
RWStructuredBuffer<uint> TransparencySortScratch;
RWByteAddressBuffer TransparencyCellWork;
RWByteAddressBuffer TransparencySortJobs;
RWStructuredBuffer<uint2> TransparencyCellTrees;
struct TransparencyDepthNode
{
    float MinimumDepth, MaximumDepth;
    uint MinimumPixel, MaximumPixel;
};
RWStructuredBuffer<TransparencyDepthNode> TransparencyDepthNodes;
RWStructuredBuffer<uint4> TransparencyCoverageRanges;
RWStructuredBuffer<uint> TransparencyHardwareMasks;
RWStructuredBuffer<uint2> TransparencyHardwareAssociations;
RWStructuredBuffer<uint4> TransparencyTilePartitions;
RWStructuredBuffer<uint4> TransparencyDepthCells;
RWStructuredBuffer<uint> TransparencyDepthReferences;
RWStructuredBuffer<float2> TransparencyReferenceDepths;
RWByteAddressBuffer TransparencyTileWork;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWByteAddressBuffer TransparencyArguments;
// Actual primitives, reserved references, resident references, active source clusters, projected sources.
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWByteAddressBuffer TransparencyTriangleCount;

uint TransparencyVisibleCount(ByteAddressBuffer arguments)
{
    return arguments.Load(4u) + arguments.Load(8u);
}

void WriteTransparencyDispatch(uint offset, uint3 groups)
{
    TransparencyArguments.Store(offset, groups.x);
    TransparencyArguments.Store(offset + 4u, groups.y);
    TransparencyArguments.Store(offset + 8u, groups.z);
}

void TransparencyDispatch(uint offset, uint work)
{
    uint groups = max(1u, (work + 63u) / 64u);
    WriteTransparencyDispatch(offset, uint3(min(groups, 65535u), (groups + 65534u) / 65535u, 1u));
}

void ExtendTransparencyDispatch(uint offset, uint groups)
{
    TransparencyArguments.InterlockedMax(offset, min(groups, 65535u));
    TransparencyArguments.InterlockedMax(offset + 4u, (groups + 65534u) / 65535u);
}

void ClearTransparency(uint3 thread)
{
    if (thread.x < TransparencyUniforms.ViewCount)
    {
        float4x4 view = TransparencyLightingViews[thread.x].View;
        float4x4 inverse = TransparencyViews[thread.x].ClipToWorld;
        uint offset = 16u + TransparencyUniforms.CellCount * 8u + thread.x * 32u;
        TransparencyCellWork.Store4(offset, asuint(mul(inverse, float4(view[0][2],view[1][2],view[2][2],view[3][2]))));
        TransparencyCellWork.Store4(offset + 16u, asuint(float4(inverse[0][3],inverse[1][3],inverse[2][3],inverse[3][3])));
    }
    if (thread.x < TransparencyUniforms.CellCount)
    {
        TransparencyTileCounts[thread.x] = 0u;
        TransparencyTileCounts[TransparencyUniforms.CellCount + thread.x] = 0u;
        TransparencyCellOffsets[thread.x] = 0u;
        TransparencyCellWrites[thread.x] = 0u;
        TransparencyCellStreams[thread.x] = 0u;
        TransparencyTilePartitions[thread.x] = uint4(0u,0u,1u,0u);
    }
    if (thread.x == 0u)
    {
        uint4 counts = uint4(TransparencyFirstDraw.Load(4u), TransparencyFirstDraw.Load(8u),
            TransparencySecondDraw.Load(4u), TransparencySecondDraw.Load(8u));
        uint count = counts.x + counts.y + counts.z + counts.w;
        TransparencySourceCounts.Store4(0u, counts);
        TransparencyTriangleCount.Store4(0u, 0u.xxxx);
        TransparencyTriangleCount.Store(16u, 0u);
        TransparencyCellWork.Store4(0u, 0u.xxxx);
        TransparencySortJobs.Store(0u,0u);
        TransparencyTileWork.Store4(0u,0u.xxxx);
        TransparencyTileWork.Store4(16u,0u.xxxx);
        TransparencyDispatch(0u, 0u);
        TransparencyDispatch(12u, count);
        TransparencyDispatch(36u, 0u);
        WriteTransparencyDispatch(160u, uint3(0u, 1u, 1u));
    }
}

uint TransparencySourceCount()
{
    return TransparencyTriangleCount.Load(12u);
}

uint TransparencyMatchingLane(uint index)
{
    return WaveMaskFirstLane(WaveMatchingLanes(index));
}

uint TransparencyPrimarySourceCount()
{
    uint4 counts = TransparencySourceCounts.Load4(0u);
    return counts.x + counts.y + counts.z + counts.w;
}

uint TransparencyVisibleIndex(uint source)
{
    uint4 counts = TransparencySourceCounts.Load4(0u);
    if (source < counts.x) return source;
    source -= counts.x;
    if (source < counts.y) return TransparencyUniforms.VisibleCapacity - source - 1u;
    source -= counts.y;
    if (source < counts.z) return counts.x + source;
    source -= counts.z;
    return TransparencyUniforms.VisibleCapacity - counts.y - source - 1u;
}

uint TransparencyWork(uint3 thread)
{
    return thread.x + thread.y * 65535u * 64u;
}

bool TransparencyRequestMaterial(DrawRequest request, uint triangle, out uint bin, out uint material, out uint view)
{
    view = DrawRequestViewIndex(request);
    bin = material = 0u;
    if (view >= TransparencyUniforms.ViewCount) return false;
    uint cluster = request.PageOffset + TransparencyPageHeap.Load(request.PageOffset + 16u) +
        DrawRequestClusterID(request) * PACKED_CLUSTER_STRIDE_BYTES;
    uint count = (TransparencyPageHeap.Load(cluster + 60u) >> 8u) & 255u;
    if (triangle >= count) return false;
    material = LoadClusterMaterialSlotOffset(TransparencyInstances,
        LoadRenderInstanceMetadata(TransparencyProperties, CLUSTER_MATERIAL_SLOT_OFFSET_METADATA_WORD), request.InstanceID) +
        LocalMaterial(TransparencyPageHeap, cluster, request.PageOffset, triangle);
    uint state = TransparencyMaterialStates[material].x;
    if ((state & 3u) == 0u || ((state & 0x80000000u) != 0u &&
        LoadInstanceXray(TransparencyInstances, TransparencyProperties, request.InstanceID).Enabled == 0u)) return false;
    bin = ((state & 0x3FFFFFFFu) >> 2u) - 1u;
    return true;
}

void CopyTransparencySources(uint3 thread)
{
    uint work = TransparencyWork(thread);
    if (work >= TransparencyPrimarySourceCount()) return;
    uint visible = TransparencyVisibleIndex(work);
    DrawRequest request = VisibleClusterRequest(TransparencyVisibleClusters[visible]);
    uint cluster = request.PageOffset + TransparencyPageHeap.Load(request.PageOffset + 16u) +
        DrawRequestClusterID(request) * PACKED_CLUSTER_STRIDE_BYTES;
    uint count = (TransparencyPageHeap.Load(cluster + 60u) >> 8u) & 255u;
    ClusterMaterialRanges ranges = LoadClusterMaterialRanges(TransparencyPageHeap, cluster, request.PageOffset);
    uint rangeCount = ClusterMaterialRangeCount(ranges, count);
    for (uint rangeIndex = 0u; rangeIndex < rangeCount; rangeIndex++)
    {
        uint triangle = ClusterMaterialRange(TransparencyPageHeap, ranges, count, rangeIndex).x & 255u;
        uint bin, material, view;
        if (!TransparencyRequestMaterial(request, triangle, bin, material, view)) continue;
        TransparencySource value = { request.PageOffset, request.InstanceID, request.ClusterAndView };
        TransparencySourceOutput[visible] = value;
        uint index;
        TransparencyTriangleCount.InterlockedAdd(12u, 1u, index);
        TransparencyActiveSources[index] = visible;
        ExtendTransparencyDispatch(0u, (index + 1u) * 2u);
        break;
    }
}

bool TransparencySourceMaterial(uint source, out uint bin, out uint material, out uint view)
{
    return TransparencyRequestMaterial(TransparencySourceRequest(source), TransparencySourceTriangle(source), bin, material, view);
}

uint TransparencyResourceItem(uint source)
{
    uint evaluator, material, view;
    TransparencySourceMaterial(source, evaluator, material, view);
    return TransparencyMaterialStates[material].y;
}

bool ProjectTransparency(uint source, uint primitive, uint bin, uint material, uint view,
    float3x2 domain, float4 first, float4 second, float4 third, out TransparencyTriangle value)
{
    value = {};
    if (first.w <= 0.0 || second.w <= 0.0 || third.w <= 0.0) return false;
    float2 size = float2(TransparencyUniforms.Width, TransparencyUniforms.Height);
    // Cache and replay quantize the same projection before integer coverage.
    float2 a = mad(first.xy / first.w, float2(.5, -.5), .5.xx) * size;
    float2 b = mad(second.xy / second.w, float2(.5, -.5), .5.xx) * size;
    float2 c = mad(third.xy / third.w, float2(.5, -.5), .5.xx) * size;
    a = floor(mad(a, CLUSTER_RASTER_SUBPIXEL_SCALE_F.xx, .5.xx)) / CLUSTER_RASTER_SUBPIXEL_SCALE_F;
    b = floor(mad(b, CLUSTER_RASTER_SUBPIXEL_SCALE_F.xx, .5.xx)) / CLUSTER_RASTER_SUBPIXEL_SCALE_F;
    c = floor(mad(c, CLUSTER_RASTER_SUBPIXEL_SCALE_F.xx, .5.xx)) / CLUSTER_RASTER_SUBPIXEL_SCALE_F;
    float2 edge1 = b - a, edge2 = c - a;
    float area = edge1.x * edge2.y - edge1.y * edge2.x;
    if (area == 0.0 || ((TransparencyMaterialStates[material].x & 3u) == 1u && area >= 0.0)) return false;
    int2 minimum = max(int2(0), int2(ceil(min(a, min(b, c)) - .5)));
    int2 maximum = min(int2(size) - 1, int2(floor(max(a, max(b, c)) - .5)));
    if (any(minimum > maximum)) return false;
    value.First = a; value.Second = b; value.Third = c;
    value.InverseArea = 1.0 / area;
    value.Depths = float3(first.z / first.w, second.z / second.w, third.z / third.w);
    value.Source = source; value.Primitive = primitive; value.Bin = bin; value.View = view;
    value.Domain = domain;
    float2 plane = float2((value.Depths.y - value.Depths.x) * edge2.y - (value.Depths.z - value.Depths.x) * edge1.y,
        (value.Depths.z - value.Depths.x) * edge1.x - (value.Depths.y - value.Depths.x) * edge2.x) * value.InverseArea;
    value.DepthPlane = float3(value.Depths.x - dot(a, plane), plane);
    return true;
}

float TransparencyViewDepth(uint view, float2 pixel, float depth)
{
    float2 size = float2(TransparencyUniforms.Width, TransparencyUniforms.Height);
    float2 ndc = pixel / size * float2(2.0, -2.0) + float2(-1.0, 1.0);
    uint offset = 16u + TransparencyUniforms.CellCount * 8u + view * 32u;
    float4 clip = float4(ndc,depth,1.0);
    return max(-dot(clip,asfloat(TransparencyCellWork.Load4(offset))) / dot(clip,asfloat(TransparencyCellWork.Load4(offset + 16u))),
        TransparencyLightingViews[view].ZParams.x);
}

void TransparencyCellBounds(TransparencyTriangle triangle, out uint3 minimum, out uint3 maximum)
{
    ClusteredLightGridParameters grid = TransparencyLightingViews[triangle.View];
    float2 low = max(0.0.xx, ceil(min(triangle.First, min(triangle.Second, triangle.Third)) - .5));
    float2 high = min(float2(TransparencyUniforms.Width, TransparencyUniforms.Height) - 1,
        floor(max(triangle.First, max(triangle.Second, triangle.Third)) - .5));
    if (grid.DepthSliceCount == 1u)
    {
        minimum = uint3(uint2(low) / uint2(grid.TileSizeX,grid.TileSizeY),0u);
        maximum = uint3(uint2(high) / uint2(grid.TileSizeX,grid.TileSizeY),0u);
        return;
    }
    float3 depths = float3(TransparencyViewDepth(triangle.View, triangle.First, triangle.Depths.x),
        TransparencyViewDepth(triangle.View, triangle.Second, triangle.Depths.y),
        TransparencyViewDepth(triangle.View, triangle.Third, triangle.Depths.z));
    minimum = uint3(uint2(low) / uint2(grid.TileSizeX, grid.TileSizeY), ClusteredLightDepthSlice(grid, min(depths.x, min(depths.y, depths.z))));
    maximum = uint3(uint2(high) / uint2(grid.TileSizeX, grid.TileSizeY), ClusteredLightDepthSlice(grid, max(depths.x, max(depths.y, depths.z))));
    uint bin, material, view;
    TransparencySourceMaterial(triangle.Source, bin, material, view);
    if ((TransparencyMaterialStates[material].x & 0x40000000u) != 0u)
    {
        minimum.z = 0u;
        maximum.z = grid.DepthSliceCount - 1u;
    }
}

uint TransparencyCell(uint view, uint3 coordinate)
{
    ClusteredLightGridParameters grid = TransparencyLightingViews[view];
    return view * grid.CellCountPerView + (coordinate.z * grid.TileCountY + coordinate.y) * grid.TileCountX + coordinate.x;
}

void BuildTransparency<TMaterials : ITransparencyMaterials>(uniform TMaterials materials, uint3 thread)
{
    uint work = TransparencyWork(thread);
    if (work / 128u >= TransparencySourceCount()) return;
    uint source = TransparencyActiveSources[work / 128u] * 128u + work % 128u;
    uint bin, material, view;
    if (!TransparencySourceMaterial(source, bin, material, view)) return;
    TMaterials.Projection projection = materials.beginProjection(bin, source, view,
        float4(.5, .5, float(TransparencyUniforms.Width) - .5, float(TransparencyUniforms.Height) - .5));
    uint primitive;
    float3x2 domain;
    float4 first, second, third;
    uint3 low = 0xFFFFFFFFu.xxx, high = 0u.xxx;
    bool projected = false;
    while (materials.nextProjection(bin, source, view, projection, primitive, domain, first, second, third))
    {
        TransparencyTriangle triangle;
        if (!ProjectTransparency(source, primitive, bin, material, view, domain, first, second, third, triangle)) continue;
        uint index = 0u;
        uint emitted = WaveActiveCountBits(true);
        if (WaveIsFirstLane())
        {
            TransparencyTriangleCount.InterlockedAdd(0u,emitted,index);
            ExtendTransparencyDispatch(36u, (min(index + emitted, TransparencyUniforms.TriangleCapacity) + 63u) / 64u);
        }
        index = WaveReadLaneFirst(index) + WavePrefixCountBits(true);
        if (index < TransparencyUniforms.TriangleCapacity)
        {
            TransparencyTriangles[index] = triangle;
        }
        uint3 minimum, maximum;
        TransparencyCellBounds(triangle, minimum, maximum);
        low = min(low, minimum); high = max(high, maximum);
        projected = true;
    }
    if (projected)
    {
        uint index = 0u;
        uint projectedCount = WaveActiveCountBits(true);
        if (WaveIsFirstLane())
        {
            TransparencyTriangleCount.InterlockedAdd(16u,projectedCount,index);
            uint receivers = index + projectedCount;
            if (receivers > TransparencyUniforms.TriangleCapacity) receivers = TransparencySourceCount() * 128u;
            ExtendTransparencyDispatch(160u, receivers);
        }
        index = WaveReadLaneFirst(index) + WavePrefixCountBits(true);
        if (index < TransparencyUniforms.TriangleCapacity)
        {
            TransparencySourceBounds value = { source, low, high };
            TransparencyProjectedSources[index] = value;
        }
    }
}

void CountTransparencyCells(uint3 thread)
{
    uint index = TransparencyWork(thread);
    bool reproject = TransparencyTriangleCount.Load(0u) > TransparencyUniforms.TriangleCapacity;
    uint count = TransparencyTriangleCount.Load(reproject ? 16u : 0u);
    if (index >= count || (reproject && count > TransparencyUniforms.TriangleCapacity)) return;
    uint3 minimum, maximum;
    uint view;
    if (reproject)
    {
        TransparencySourceBounds source = TransparencyProjectedSources[index];
        minimum = source.Minimum; maximum = source.Maximum;
        view = DrawRequestViewIndex(TransparencySourceRequest(source.Source));
    }
    else
    {
        TransparencyTriangle triangle = TransparencyRasterTriangles[index];
        TransparencyCellBounds(triangle, minimum, maximum);
        view = triangle.View;
    }
    if (TransparencyUniforms.UseIndex == 3u && !reproject)
    {
        uint width = maximum.x - minimum.x + 1u, first;
        TransparencyCellWork.InterlockedAdd(8u, width * (maximum.y - minimum.y + 1u), first);
        TransparencyCoverageRanges[index] = uint4(first, minimum.xy, width);
        if ((TransparencyUniforms.Effects & 8u) != 0u)
        {
            ClusteredLightGridParameters grid = TransparencyLightingViews[view];
            for (uint y = minimum.y; y <= maximum.y; y++)
            for (uint x = minimum.x; x <= maximum.x; x++)
            {
                uint association = first + (y - minimum.y) * width + x - minimum.x;
                uint tile = view * grid.TileCountX * grid.TileCountY + y * grid.TileCountX + x;
                TransparencyHardwareAssociations[association] = uint2(index,tile);
            }
            return;
        }
    }
    if (TransparencyUniforms.UseIndex == 0u)
    {
        InterlockedAdd(TransparencyTileCounts[0], 1u);
        return;
    }
    for (uint z = minimum.z; z <= maximum.z; z++)
    for (uint y = minimum.y; y <= maximum.y; y++)
    for (uint x = minimum.x; x <= maximum.x; x++)
    {
        uint cell = TransparencyCell(view, uint3(x, y, z));
        uint previous;
        InterlockedAdd(TransparencyTileCounts[cell], 1u, previous);
        if (previous == 0u)
        {
            uint job;
            TransparencyCellWork.InterlockedAdd(0u, 1u, job);
            TransparencyCellWork.Store(16u + job * 4u, cell);
            ClusteredLightGridParameters grid = TransparencyLightingViews[view];
            uint tile = view * grid.TileCountX * grid.TileCountY + y * grid.TileCountX + x;
            uint occupied;
            InterlockedAdd(TransparencyTileCounts[TransparencyUniforms.CellCount + tile], 1u, occupied);
            if (occupied == 0u)
            {
                TransparencyCellWork.InterlockedAdd(12u, 1u, job);
                TransparencyCellWork.Store(16u + (TransparencyUniforms.CellCount + job) * 4u, tile);
            }
        }
    }
}

void ReserveTransparency(uint3 thread)
{
    if (thread.x == 0u)
    {
        bool reproject = TransparencyTriangleCount.Load(0u) > TransparencyUniforms.TriangleCapacity;
        uint count = TransparencyTriangleCount.Load(reproject ? 16u : 0u);
        TransparencyDispatch(36u, min(count, TransparencyUniforms.TriangleCapacity));
        uint3 groups = uint3((TransparencyUniforms.Width + 15u) / 16u,
            (TransparencyUniforms.Height + 15u) / 16u, TransparencyUniforms.ViewCount);
        WriteTransparencyDispatch(24u, reproject ? 0u.xxx : groups);
        WriteTransparencyDispatch(48u, reproject ? groups : 0u.xxx);
        uint jobs = TransparencyCellWork.Load(0u);
        WriteTransparencyDispatch(60u, uint3(min(jobs, 65535u), (jobs + 65534u) / 65535u, 1u));
        TransparencyArguments.Store4(72u, uint4(reproject ? 0u : count * 3u, 1u, 0u, 0u));
        TransparencyDispatch(88u, TransparencyCellWork.Load(8u) * 8u);
        TransparencyDispatch(124u, TransparencyCellWork.Load(8u));
        uint tileJobs = reproject ? 0u : TransparencyCellWork.Load(12u);
        WriteTransparencyDispatch(100u, uint3(min(tileJobs, 65535u), (tileJobs + 65534u) / 65535u, 1u));
    }
    if (thread.x < TransparencyUniforms.CellCount)
    {
        uint count = TransparencyTileCounts[thread.x];
        if (count == 0u) return;
        uint first;
        TransparencyTriangleCount.InterlockedAdd(4u, count, first);
        TransparencyCellOffsets[thread.x] = first;
        if (first + count > TransparencyUniforms.ReferenceCapacity || TransparencyUniforms.UseIndex == 0u)
            TransparencyCellStreams[thread.x] = 1u;
        if (TransparencyCellStreams[thread.x] == 0u)
        {
            TransparencyTriangleCount.InterlockedAdd(8u, count);
            if (count <= 128u)
            {
                TransparencyCellTrees[thread.x] = uint2(0u,0x40000000u);
                return;
            }
            uint leaves = (count + 31u) / 32u;
            uint leafBase = 1u << uint(ceil(log2(float(leaves))));
            uint node;
            TransparencyCellWork.InterlockedAdd(4u, leafBase * 2u, node);
            TransparencyCellTrees[thread.x] = uint2(node, leafBase);
        }
    }
}

void ScatterTransparency(uint3 thread)
{
    uint index = TransparencyWork(thread);
    if (TransparencyUniforms.UseIndex == 0u) return;
    bool reproject = TransparencyTriangleCount.Load(0u) > TransparencyUniforms.TriangleCapacity;
    uint count = TransparencyTriangleCount.Load(reproject ? 16u : 0u);
    if (index >= count || (reproject && count > TransparencyUniforms.TriangleCapacity)) return;
    uint3 minimum, maximum;
    uint view, value;
    if (reproject)
    {
        TransparencySourceBounds source = TransparencyProjectedSources[index];
        minimum = source.Minimum; maximum = source.Maximum;
        view = DrawRequestViewIndex(TransparencySourceRequest(source.Source));
        value = source.Source;
    }
    else
    {
        TransparencyTriangle triangle = TransparencyRasterTriangles[index];
        TransparencyCellBounds(triangle, minimum, maximum);
        view = triangle.View;
        value = index;
    }
    for (uint z = minimum.z; z <= maximum.z; z++)
    for (uint y = minimum.y; y <= maximum.y; y++)
    for (uint x = minimum.x; x <= maximum.x; x++)
    {
        uint cell = TransparencyCell(view, uint3(x, y, z));
        if (TransparencyCellStreams[cell] != 0u) continue;
        uint offset;
        InterlockedAdd(TransparencyCellWrites[cell], 1u, offset);
        TransparencyReferences[TransparencyCellOffsets[cell] + offset] = value;
    }
}

bool TransparencyCovered(TransparencyTriangle triangle, float2 pixel, out float depth)
{
    float2 edge1 = triangle.Second - triangle.First, edge2 = triangle.Third - triangle.First;
    float2 delta = pixel - triangle.First;
    float b = (delta.x * edge2.y - delta.y * edge2.x) * triangle.InverseArea;
    float c = (edge1.x * delta.y - edge1.y * delta.x) * triangle.InverseArea;
    // Interpolate depth differences to preserve the sample depth's precision.
    depth = triangle.Depths.x + b * (triangle.Depths.y - triangle.Depths.x) +
        c * (triangle.Depths.z - triangle.Depths.x);
    int winding = triangle.InverseArea < 0.0 ? 1 : -1;
    int2 coordinate = int2(pixel - .5);
    return ClusterRasterIntegerEdgeAccepts(triangle.Second, triangle.Third, coordinate, winding) &&
        ClusterRasterIntegerEdgeAccepts(triangle.Third, triangle.First, coordinate, winding) &&
        ClusterRasterIntegerEdgeAccepts(triangle.First, triangle.Second, coordinate, winding);
}


// A pixel keeps its depth ordering while the candidate source advances through cells and batches.
struct TransparencyCandidates<TMaterials : ITransparencyMaterials>
{
    uint Slice, Reference, Work, Projected;
    uint Cell, LastSlice, Source;
    uint4 OccupiedSlices;
    float4 ScreenBounds;
    TMaterials.Projection Projection;
    bool SourceStarted;
};

TransparencyCandidates<TMaterials> BeginTransparencyCandidates<TMaterials : ITransparencyMaterials, let reproject : bool>(uint3 pixel, float opaque)
{
    TransparencyCandidates<TMaterials> input = {};
    if (reproject)
    {
        float2 center = float2(pixel.xy) + .5;
        input.ScreenBounds = float4(WaveActiveMin(center), WaveActiveMax(center));
        return input;
    }
    if (TransparencyUniforms.UseIndex == 0u) return input;
    ClusteredLightGridParameters grid = TransparencyLightingViews[pixel.z];
    uint2 tile = pixel.xy / uint2(grid.TileSizeX, grid.TileSizeY);
    uint tileIndex = (pixel.z * grid.TileCountY + tile.y) * grid.TileCountX + tile.x;
    uint leader = WaveMaskFirstLane(WaveMatchingLanes(tileIndex));
    uint4 occupied = 0u.xxxx;
    if (WaveGetLaneIndex() == leader)
        for (uint slice = 0u; slice < grid.DepthSliceCount; slice++)
            if (TransparencyTileCounts[TransparencyCell(pixel.z, uint3(tile, slice))] != 0u)
                occupied[slice >> 5u] |= 1u << (slice & 31u);
    input.OccupiedSlices = WaveReadLaneAt(occupied, leader);
    input.LastSlice = opaque < 1.0 && (TransparencyUniforms.Effects & 3u) == 0u
        ? ClusteredLightDepthSlice(grid, TransparencyViewDepth(pixel.z, float2(pixel.xy) + .5, opaque))
        : grid.DepthSliceCount - 1u;
    return input;
}

bool ReadCachedTransparencySource(uint3 pixel, inout uint projected, out TransparencyTriangle triangle)
{
    triangle = {};
    uint count = TransparencyTriangleCount.Load(0u);
    while (projected < count)
    {
        triangle = TransparencyRasterTriangles[projected++];
        if (triangle.View == pixel.z) return true;
    }
    return false;
}

bool ReadTransparencySource<TMaterials : ITransparencyMaterials>(uniform TMaterials materials,
    uint3 pixel, inout TransparencyCandidates<TMaterials> input, out TransparencyTriangle triangle)
{
    triangle = {};
    uint workCount = TransparencySourceCount() * 128u;
    while (input.Work < workCount)
    {
        uint source = TransparencyActiveSources[input.Work / 128u] * 128u + input.Work % 128u;
        uint bin, material, sourceView;
        if (TransparencySourceMaterial(source, bin, material, sourceView) && sourceView == pixel.z)
        {
            if (!input.SourceStarted)
            {
                input.Projection = materials.beginProjection(bin, source, pixel.z, input.ScreenBounds);
                input.SourceStarted = true;
            }
            uint primitive;
            float3x2 domain;
            float4 first, second, third;
            while (materials.nextProjection(bin, source, pixel.z, input.Projection, primitive, domain, first, second, third))
                if (ProjectTransparency(source, primitive, bin, material, pixel.z, domain, first, second, third, triangle)) return true;
        }
        input.Work++; input.SourceStarted = false;
    }
    return false;
}

bool NextProjectedTransparencySource<TMaterials : ITransparencyMaterials>(uint3 pixel,
    inout TransparencyCandidates<TMaterials> input, out uint source)
{
    source = 0u;
    uint count = TransparencyTriangleCount.Load(16u);
    if (TransparencyUniforms.UseIndex == 0u)
    {
        input.Cell = 0xFFFFFFFFu;
        while (input.Projected < count)
        {
            source = TransparencyProjectedSources[input.Projected++].Source;
            if (DrawRequestViewIndex(TransparencySourceRequest(source)) == pixel.z) return true;
        }
        return false;
    }
    ClusteredLightGridParameters grid = TransparencyLightingViews[pixel.z];
    uint2 first = uint2(input.ScreenBounds.xy) / uint2(grid.TileSizeX, grid.TileSizeY);
    uint2 last = uint2(input.ScreenBounds.zw) / uint2(grid.TileSizeX, grid.TileSizeY);
    uint2 size = last - first + 1u;
    while (input.Slice < size.x * size.y * grid.DepthSliceCount)
    {
        uint3 coordinate = uint3(first + uint2(input.Slice % size.x, input.Slice / size.x % size.y),
            input.Slice / (size.x * size.y));
        uint cell = TransparencyCell(pixel.z, coordinate);
        if (TransparencyCellStreams[cell] == 0u)
        {
            if (input.Reference < TransparencyTileCounts[cell])
            {
                source = TransparencyReferences[TransparencyCellOffsets[cell] + input.Reference++];
                input.Cell = cell;
                return true;
            }
        }
        else
        {
            while (input.Projected < count)
            {
                TransparencySourceBounds value = TransparencyProjectedSources[input.Projected++];
                if (DrawRequestViewIndex(TransparencySourceRequest(value.Source)) == pixel.z &&
                    all(coordinate >= value.Minimum) && all(coordinate <= value.Maximum))
                {
                    source = value.Source; input.Cell = cell;
                    return true;
                }
            }
        }
        input.Slice++; input.Reference = input.Projected = 0u;
    }
    return false;
}

bool ReadProjectedTransparencySource<TMaterials : ITransparencyMaterials>(uniform TMaterials materials,
    uint3 pixel, inout TransparencyCandidates<TMaterials> input, out TransparencyTriangle triangle)
{
    triangle = {};
    if (TransparencyTriangleCount.Load(16u) > TransparencyUniforms.TriangleCapacity)
    {
        input.Cell = 0xFFFFFFFFu;
        return ReadTransparencySource(materials, pixel, input, triangle);
    }
    while (true)
    {
        uint bin, material, view;
        if (!input.SourceStarted)
        {
            uint source;
            if (!NextProjectedTransparencySource(pixel, input, source)) return false;
            input.Source = source;
            TransparencySourceMaterial(source, bin, material, view);
            input.Projection = materials.beginProjection(bin, source, pixel.z, input.ScreenBounds);
            input.SourceStarted = true;
        }
        else TransparencySourceMaterial(input.Source, bin, material, view);
        uint primitive;
        float3x2 domain;
        float4 first, second, third;
        while (materials.nextProjection(bin, input.Source, pixel.z, input.Projection, primitive, domain, first, second, third))
            if (ProjectTransparency(input.Source, primitive, bin, material, pixel.z, domain, first, second, third, triangle)) return true;
        input.SourceStarted = false;
    }
}

bool ReadWaveTransparencySource<TMaterials : ITransparencyMaterials>(uniform TMaterials materials,
    uint3 pixel, inout TransparencyCandidates<TMaterials> input, out TransparencyTriangle triangle)
{
    uint leader = WaveMaskFirstLane(WaveActiveBallot(true));
    uint found = 0u;
    triangle = {};
    if (WaveGetLaneIndex() == leader)
        found = ReadProjectedTransparencySource(materials, pixel, input, triangle) ? 1u : 0u;
    if (WaveReadLaneAt(found, leader) == 0u) return false;
    input.Cell = WaveReadLaneAt(input.Cell, leader);
    triangle.First = WaveReadLaneAt(triangle.First, leader);
    triangle.Second = WaveReadLaneAt(triangle.Second, leader);
    triangle.Third = WaveReadLaneAt(triangle.Third, leader);
    triangle.InverseArea = WaveReadLaneAt(triangle.InverseArea, leader);
    triangle.Depths = WaveReadLaneAt(triangle.Depths, leader);
    triangle.Source = WaveReadLaneAt(triangle.Source, leader);
    triangle.Bin = WaveReadLaneAt(triangle.Bin, leader);
    triangle.View = WaveReadLaneAt(triangle.View, leader);
    triangle.Primitive = WaveReadLaneAt(triangle.Primitive, leader);
    triangle.Domain[0] = WaveReadLaneAt(triangle.Domain[0], leader);
    triangle.Domain[1] = WaveReadLaneAt(triangle.Domain[1], leader);
    triangle.Domain[2] = WaveReadLaneAt(triangle.Domain[2], leader);
    return true;
}

bool NextTransparencyCandidate<TMaterials : ITransparencyMaterials, let reproject : bool>(uniform TMaterials materials,
    uint3 pixel, inout TransparencyCandidates<TMaterials> input, out TransparencyTriangle triangle)
{
    if (reproject)
    {
        return ReadWaveTransparencySource(materials, pixel, input, triangle);
    }
    if (TransparencyUniforms.UseIndex == 0u)
    {
        input.Cell = 0xFFFFFFFFu;
        return ReadCachedTransparencySource(pixel, input.Projected, triangle);
    }
    ClusteredLightGridParameters grid = TransparencyLightingViews[pixel.z];
    uint2 tile = pixel.xy / uint2(grid.TileSizeX, grid.TileSizeY);
    while (input.Slice <= input.LastSlice)
    {
        uint word = input.Slice >> 5u;
        uint occupied = input.OccupiedSlices[word] & (0xFFFFFFFFu << (input.Slice & 31u));
        if (occupied == 0u) { input.Slice = (word + 1u) * 32u; continue; }
        input.Slice = word * 32u + uint(firstbitlow(occupied));
        if (input.Slice > input.LastSlice) break;
        uint cell = TransparencyCell(pixel.z, uint3(tile, input.Slice));
        if (TransparencyCellStreams[cell] == 0u)
        {
            if (input.Reference < TransparencyTileCounts[cell])
            {
                triangle = TransparencyRasterTriangles[TransparencyReferences[TransparencyCellOffsets[cell] + input.Reference++]];
                input.Cell = cell;
                return true;
            }
        }
        else if (TransparencyTileCounts[cell] != 0u)
        {
            while (ReadCachedTransparencySource(pixel, input.Projected, triangle))
            {
                uint3 minimum, maximum;
                TransparencyCellBounds(triangle, minimum, maximum);
                uint3 position = uint3(tile, input.Slice);
                if (all(position >= minimum) && all(position <= maximum)) { input.Cell = cell; return true; }
            }
        }
        input.Slice++; input.Reference = input.Work = input.Projected = 0u; input.SourceStarted = false;
    }
    triangle = {};
    return false;
}

bool TransparencyBefore(float depth, uint source, uint primitive, float otherDepth, uint otherSource, uint otherPrimitive)
{
    if (depth != otherDepth) return depth < otherDepth;
    TransparencySource a = TransparencySources[source >> 7u], b = TransparencySources[otherSource >> 7u];
    if (a.InstanceAndTriangle != b.InstanceAndTriangle) return a.InstanceAndTriangle < b.InstanceAndTriangle;
    if (a.PageOffset != b.PageOffset) return a.PageOffset < b.PageOffset;
    if (a.ClusterAndView != b.ClusterAndView) return a.ClusterAndView < b.ClusterAndView;
    if ((source & 127u) != (otherSource & 127u)) return (source & 127u) < (otherSource & 127u);
    return primitive < otherPrimitive;
}

OrderedMaterialState TransparencyOrderedState(uint source)
{
    uint bin, material, view;
    TransparencySourceMaterial(source, bin, material, view);
    return OrderedMaterialStates[material];
}

bool TransparencyMaterialHit<TMaterials : ITransparencyMaterials>(uniform TMaterials materials, uint3 pixel,
    TransparencyTriangle triangle, uint cell, float opaque, inout float depth, uint cached = 0xFFFFFFFFu)
{
    float resultDepth;
    bool covered = cached == 0xFFFFFFFFu ? materials.cover(triangle.Bin, triangle.Source, triangle.Domain, pixel, depth, resultDepth)
        : materials.coverTriangle(triangle.Bin, cached, triangle.Domain, pixel, depth, resultDepth);
    if (!covered) return false;
    depth = resultDepth;
    return TransparencyDepthHit(pixel,triangle,cell,opaque,depth);
}

bool TransparencyDepthHit(uint3 pixel, TransparencyTriangle triangle, uint cell, float opaque, float depth)
{
    if (depth < 0.0 || depth > 1.0) return false;
    if ((TransparencyUniforms.Effects & 3u) == 0u)
    {
        ClusteredLightGridParameters grid = TransparencyLightingViews[pixel.z];
        return depth <= opaque && (cell == 0xFFFFFFFFu || grid.DepthSliceCount == 1u ||
            cell == ClusteredLightCellIndex(grid,pixel.z,pixel.xy,TransparencyViewDepth(pixel.z,float2(pixel.xy) + .5,depth)));
    }
    InstanceXray effect = LoadInstanceXray(TransparencyInstances, TransparencyProperties,
        TransparencySourceRequest(triangle.Source).InstanceID);
    if (TransparencyOrderedState(triangle.Source).Enabled != 0u)
    {
        // Ordered coverage reaches the state chain even when its depth test fails.
    }
    else if (effect.Enabled == 0u)
    {
        if (depth > opaque) return false;
    }
    else if (depth <= opaque)
    {
        if (effect.Visible.w == 0.0) return false;
    }
    else
    {
        float distance = TransparencyViewDepth(pixel.z, float2(pixel.xy) + .5, depth) -
            TransparencyViewDepth(pixel.z, float2(pixel.xy) + .5, opaque);
        if (distance < effect.MinimumOcclusion || distance > effect.MaximumOcclusion || effect.Occluded.w == 0.0) return false;
    }
    return cell == 0xFFFFFFFFu || cell == ClusteredLightCellIndex(TransparencyLightingViews[pixel.z], pixel.z, pixel.xy,
        TransparencyViewDepth(pixel.z, float2(pixel.xy) + .5, depth));
}

bool TransparencyHit<TMaterials : ITransparencyMaterials>(uniform TMaterials materials, uint3 pixel,
    TransparencyTriangle triangle, uint cell, float opaque, out float depth)
{
    return TransparencyCovered(triangle, float2(pixel.xy) + .5, depth) &&
        TransparencyMaterialHit(materials, pixel, triangle, cell, opaque, depth);
}

groupshared TransparencyTriangle TransparencyRequestTriangle;
groupshared uint TransparencyRequestMore;

void RequestTransparencyPages<TMaterials : ITransparencyMaterials>(uniform TMaterials materials, uint3 group, uint thread)
{
    uint work = group.x + group.y * 65535u;
    uint projectedSources = TransparencyTriangleCount.Load(16u);
    bool compact = projectedSources <= TransparencyUniforms.TriangleCapacity;
    uint count = compact ? projectedSources : TransparencySourceCount() * 128u;
    if (work >= count) return;
    uint source = compact ? TransparencyProjectedSources[work].Source
        : TransparencyActiveSources[work / 128u] * 128u + work % 128u;
    uint bin, material, view;
    if (!TransparencySourceMaterial(source, bin, material, view)) return;
    TMaterials.Projection projection = {};
    if (thread == 0u)
        projection = materials.beginProjection(bin, source, view,
            float4(.5, .5, float(TransparencyUniforms.Width) - .5, float(TransparencyUniforms.Height) - .5));
    uint instance = TransparencySourceRequest(source).InstanceID;
    bool firstPerson = (LoadInstanceFirstPerson(TransparencyInstances, TransparencyProperties, instance).x & 3u) == 1u;
    float4x4 clipToWorld = TransparencyViews[view].ClipToWorld;
    while (true)
    {
        if (thread == 0u)
        {
            TransparencyRequestMore = 0u;
            uint primitive;
            float3x2 domain;
            float4 first, second, third;
            while (materials.nextProjection(bin, source, view, projection, primitive, domain, first, second, third))
            {
                TransparencyTriangle triangle;
                if (!ProjectTransparency(source, primitive, bin, material, view, domain, first, second, third, triangle)) continue;
                TransparencyRequestTriangle = triangle;
                materials.cacheTileShading(0u, materials.readProjectionShading(bin, source, projection));
                TransparencyRequestMore = 1u;
                break;
            }
        }
        GroupMemoryBarrierWithGroupSync();
        if (TransparencyRequestMore == 0u) break;
        TransparencyTriangle triangle = TransparencyRequestTriangle;
        TMaterials.Shading shading = materials.loadTileShading(0u);
        int2 minimum = max(0, int2(ceil(min(triangle.First, min(triangle.Second, triangle.Third)) - .5)));
        int2 maximum = min(int2(TransparencyUniforms.Width, TransparencyUniforms.Height) - 1,
            int2(floor(max(triangle.First, max(triangle.Second, triangle.Third)) - .5)));
        for (int y = minimum.y + int(thread >> 4u); y <= maximum.y; y += 16)
        for (int x = minimum.x + int(thread & 15u); x <= maximum.x; x += 16)
        {
            uint3 pixel = uint3(x, y, view);
            float opaque = TransparencyOpaqueDepth.Load(int4(pixel, 0));
            float depth, resultDepth;
            if (!TransparencyCovered(triangle, float2(pixel.xy) + .5, depth) ||
                !materials.coverShading(bin, source, shading, triangle.Domain, pixel, depth, resultDepth) ||
                !TransparencyDepthHit(pixel, triangle, 0xFFFFFFFFu, opaque, resultDepth)) continue;
            float2 uv = (float2(pixel.xy) + .5) / float2(TransparencyUniforms.Width, TransparencyUniforms.Height);
            float4 world = mul(float4(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0, resultDepth, 1.0),
                clipToWorld);
            float3 position = abs(world.w) > 1e-8 ? world.xyz / world.w : world.xyz;
            MarkVirtualShadowPages(position, view, firstPerson);
        }
        GroupMemoryBarrierWithGroupSync();
    }
}

bool OrderedBefore(int order, uint source, uint primitive, int otherOrder, uint otherSource, uint otherPrimitive)
{
    return order < otherOrder || (order == otherOrder &&
        TransparencyBefore(0.0, source, primitive, 0.0, otherSource, otherPrimitive));
}

void ResolveOrderedTransparency<TMaterials : ITransparencyMaterials, let reproject : bool>(uniform TMaterials materials, uint3 pixel, float opaque)
{
    uint stencil = 0u;
    float depthValue = opaque;
    float4 color = 0.0.xxxx;
    bool colorRead = false, previous = false;
    int previousOrder = 0;
    uint previousSource = 0u, previousPrimitive = 0u;
    [loop]
    while (true)
    {
        bool found = false, front = false;
        float selectedDepth = 0.0;
        uint selectedSource = 0u, selectedPrimitive = 0u, selectedBin = 0u;
        OrderedMaterialState selectedState = {};
        TransparencyCandidates<TMaterials> input = BeginTransparencyCandidates<TMaterials, reproject>(pixel, 1.0);
        TransparencyTriangle triangle;
        while (NextTransparencyCandidate<TMaterials, reproject>(materials, pixel, input, triangle))
        {
            OrderedMaterialState state = TransparencyOrderedState(triangle.Source);
            if (state.Enabled == 0u) continue;
            if (previous && !OrderedBefore(previousOrder, previousSource, previousPrimitive,
                state.DrawOrder, triangle.Source, triangle.Primitive)) continue;
            if (found && !OrderedBefore(state.DrawOrder, triangle.Source, triangle.Primitive,
                selectedState.DrawOrder, selectedSource, selectedPrimitive)) continue;
            float depth;
            if (!TransparencyHit(materials, pixel, triangle, input.Cell, 1.0, depth)) continue;
            found = true;
            selectedDepth = depth; selectedSource = triangle.Source;
            selectedPrimitive = triangle.Primitive; selectedBin = triangle.Bin;
            selectedState = state; front = triangle.InverseArea < 0.0;
        }
        if (!found) break;
        previous = true; previousOrder = selectedState.DrawOrder;
        previousSource = selectedSource; previousPrimitive = selectedPrimitive;
        if (!AdvanceOrderedStencil(selectedState, front, selectedDepth, depthValue, stencil)) continue;
        if (selectedState.ColorMask == 0u) continue;
        float4 surface = materials.shade(selectedBin, selectedSource, selectedPrimitive, pixel, selectedDepth);
        if (!colorRead) { color = TransparencyColor[pixel]; colorRead = true; }
        color = OrderedBlend(selectedState, surface, color);
    }
    OrderedStencil[pixel] = stencil;
    if (colorRead) TransparencyColor[pixel] = color;
}

void CompositeUnorderedTransparency<TMaterials : ITransparencyMaterials, let reproject : bool>(uniform TMaterials materials, uint3 pixel, float opaque)
{
    float4 background = 0.0.xxxx;
    bool backgroundRead = false, contributed = false;
    float3 color = 0.0.xxx;
    float transmission = 1.0, previousDepth = -1.0;
    uint previousSource = 0u, previousPrimitive = 0u;
    bool remaining = true;
    while (remaining)
    {
        float4 depths = 2.0.xxxx;
        uint4 sources = 0u.xxxx, primitives = 0u.xxxx, bins = 0xFFFFFFFFu.xxxx;
        TransparencyCandidates<TMaterials> input = BeginTransparencyCandidates<TMaterials, reproject>(pixel, opaque);
        TransparencyTriangle triangle;
        while (NextTransparencyCandidate<TMaterials, reproject>(materials, pixel, input, triangle))
        {
            if (TransparencyOrderedState(triangle.Source).Enabled != 0u) continue;
            float depth;
            if (!TransparencyHit(materials, pixel, triangle, input.Cell, opaque, depth)) continue;
            uint source = triangle.Source, primitive = triangle.Primitive, bin = triangle.Bin;
            if (!TransparencyBefore(previousDepth, previousSource, previousPrimitive, depth, source, primitive)) continue;
            [unroll]
            for (uint item = 0u; item < 4u; item++)
            {
                if (TransparencyBefore(depth, source, primitive, depths[item], sources[item], primitives[item]))
                {
                    float displacedDepth = depths[item]; depths[item] = depth; depth = displacedDepth;
                    uint displacedSource = sources[item]; sources[item] = source; source = displacedSource;
                    uint displacedPrimitive = primitives[item]; primitives[item] = primitive; primitive = displacedPrimitive;
                    uint displacedBin = bins[item]; bins[item] = bin; bin = displacedBin;
                }
            }
        }
        if (bins.x == 0xFFFFFFFFu) break;
        remaining = bins.w != 0xFFFFFFFFu;
        [loop]
        for (uint item = 0u; item < 4u; item++)
        {
            if (bins[item] == 0xFFFFFFFFu) break;
            float4 surface = materials.shade(bins[item], sources[item], primitives[item], pixel, depths[item]);
            InstanceXray effect = LoadInstanceXray(TransparencyInstances, TransparencyProperties,
                TransparencySourceRequest(sources[item]).InstanceID);
            if (effect.Enabled != 0u) surface *= depths[item] <= opaque ? effect.Visible : effect.Occluded;
            float alpha = saturate(surface.a);
            if (alpha > 0.0)
            {
                if (!backgroundRead) { background = TransparencyColor[pixel]; backgroundRead = true; }
                color += transmission * alpha * surface.rgb;
                transmission *= 1.0 - alpha;
                contributed = true;
            }
            previousDepth = depths[item]; previousSource = sources[item]; previousPrimitive = primitives[item];
        }
        if (remaining && TransparencyUniforms.MaximumError > 0.0)
        {
            if (!backgroundRead) { background = TransparencyColor[pixel]; backgroundRead = true; }
            float limit = max(TransparencyUniforms.ColorLimit, max(background.r, max(background.g, background.b)));
            if (transmission * limit * .5 <= TransparencyUniforms.MaximumError)
            {
                color += transmission * limit * .5;
                transmission = 0.0;
                contributed = true;
                break;
            }
        }
    }
    if (contributed) TransparencyColor[pixel] = float4(color + transmission * background.rgb, background.a);
}

void ResolveUnorderedTransparency<TMaterials : ITransparencyMaterials, let reproject : bool>(uniform TMaterials materials, uint3 pixel)
{
    if (pixel.x >= TransparencyUniforms.Width || pixel.y >= TransparencyUniforms.Height || pixel.z >= TransparencyUniforms.ViewCount) return;
    if (TransparencyTriangleCount.Load(0u) == 0u) return;
    float opaque = TransparencyOpaqueDepth.Load(int4(pixel, 0));
    CompositeUnorderedTransparency<TMaterials, reproject>(materials, pixel, opaque);
}

void ResolveOrderedTransparency<TMaterials : ITransparencyMaterials, let reproject : bool>(uniform TMaterials materials, uint3 pixel)
{
    if (pixel.x >= TransparencyUniforms.Width || pixel.y >= TransparencyUniforms.Height || pixel.z >= TransparencyUniforms.ViewCount) return;
    if (TransparencyTriangleCount.Load(0u) == 0u || (TransparencyUniforms.Effects & 2u) == 0u) return;
    float opaque = TransparencyOpaqueDepth.Load(int4(pixel, 0));
    ResolveOrderedTransparency<TMaterials, reproject>(materials, pixel, opaque);
}

void ResolveTransparency<TMaterials : ITransparencyMaterials, let reproject : bool>(uniform TMaterials materials, uint3 pixel)
{
    if (pixel.x >= TransparencyUniforms.Width || pixel.y >= TransparencyUniforms.Height || pixel.z >= TransparencyUniforms.ViewCount) return;
    if (TransparencyTriangleCount.Load(0u) == 0u) return;
    float opaque = TransparencyOpaqueDepth.Load(int4(pixel, 0));
    CompositeUnorderedTransparency<TMaterials, reproject>(materials, pixel, opaque);
    if ((TransparencyUniforms.Effects & 2u) != 0u)
        ResolveOrderedTransparency<TMaterials, reproject>(materials, pixel, opaque);
}

#include "cluster_transparency_tiles.slang"
#include "cluster_transparency_hardware.slang"
