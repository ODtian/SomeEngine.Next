#include "cluster_transparency_source.slang"
#include "cluster_instance_properties.slang"
#include "cluster_view.slang"
#include "resource_effects.slang"
#include "vsm_receiver_requests.slang"
#include "cluster_raster_shared.slang"
#include "render_lighting.slang"
#include "wave_aggregation.slang"

interface ITransparencyMaterials
{
    uint primitiveCount(uint bin, uint source, uint view);
    bool project(uint bin, uint source, uint view, uint primitive, out float4 first, out float4 second, out float4 third);
    bool cover(uint bin, uint source, uint primitive, uint3 pixel, float depth, out float resultDepth);
    float4 shade(uint bin, uint source, uint primitive, uint3 pixel, float depth);
    void receiver(uint bin, uint source, uint primitive, uint3 pixel, float depth,
        out float3 position, out float3 normal, out uint flags);
};

struct TransparencyParameters
{
    uint Width, Height, ViewCount, MaterialCapacity;
    uint TriangleCapacity, VisibleCapacity;
    float ColorLimit, MaximumError;
    uint ReferenceCapacity, UseIndex, CellCount, Padding;
};

struct TransparencyTriangle
{
    float2 First, Second, Third;
    float InverseArea;
    float3 Depths;
    uint Source, Bin, View, Primitive;
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
StructuredBuffer<uint> TransparencyMaterialBins;
StructuredBuffer<uint> TransparencyMaterialStates;
Texture2DArray<float> TransparencyOpaqueDepth;
RWTexture2DArray<float4> TransparencyColor;
RWStructuredBuffer<TransparencySource> TransparencySourceOutput;
RWByteAddressBuffer TransparencySourceCounts;
RWStructuredBuffer<TransparencyTriangle> TransparencyTriangles;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWStructuredBuffer<uint> TransparencyTileCounts;
RWStructuredBuffer<uint> TransparencyCellOffsets;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWStructuredBuffer<uint> TransparencyCellWrites;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWStructuredBuffer<uint> TransparencyCellStreams;
RWStructuredBuffer<uint> TransparencyReferences;
RWByteAddressBuffer TransparencyArguments;
// Actual primitives, reserved references, resident references, source clusters.
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWByteAddressBuffer TransparencyTriangleCount;

uint TransparencyVisibleCount(ByteAddressBuffer arguments)
{
    return arguments.Load(4u) + arguments.Load(8u);
}

void TransparencyDispatch(uint offset, uint work)
{
    uint groups = max(1u, (work + 63u) / 64u);
    TransparencyArguments.Store3(offset, uint3(min(groups, 65535u), (groups + 65534u) / 65535u, 1u));
}

void ClearTransparency(uint3 thread)
{
    if (thread.x < TransparencyUniforms.CellCount)
    {
        TransparencyTileCounts[thread.x] = 0u;
        TransparencyCellOffsets[thread.x] = 0u;
        TransparencyCellWrites[thread.x] = 0u;
        TransparencyCellStreams[thread.x] = 0u;
    }
    if (thread.x == 0u)
    {
        uint4 counts = uint4(TransparencyFirstDraw.Load(4u), TransparencyFirstDraw.Load(8u),
            TransparencySecondDraw.Load(4u), TransparencySecondDraw.Load(8u));
        uint count = counts.x + counts.y + counts.z + counts.w;
        TransparencySourceCounts.Store4(0u, counts);
        TransparencyTriangleCount.Store4(0u, uint4(0u, 0u, 0u, count));
        TransparencyDispatch(0u, count * 128u);
        TransparencyDispatch(12u, count);
    }
}

uint TransparencySourceCount()
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

void CopyTransparencySources(uint3 thread)
{
    uint work = TransparencyWork(thread);
    if (work >= TransparencySourceCount()) return;
    uint visible = TransparencyVisibleIndex(work);
    DrawRequest request = VisibleClusterRequest(TransparencyVisibleClusters[visible]);
    TransparencySource value = { request.PageOffset, request.InstanceID, request.ClusterAndView };
    TransparencySourceOutput[visible] = value;
}

bool TransparencySourceMaterial(uint source, out uint bin, out uint material, out uint view)
{
    DrawRequest request = TransparencySourceRequest(source);
    view = DrawRequestViewIndex(request);
    bin = material = 0u;
    if (view >= TransparencyUniforms.ViewCount) return false;
    uint triangle = TransparencySourceTriangle(source);
    uint cluster = request.PageOffset + TransparencyPageHeap.Load(request.PageOffset + 16u) +
        DrawRequestClusterID(request) * PACKED_CLUSTER_STRIDE_BYTES;
    uint count = (TransparencyPageHeap.Load(cluster + 60u) >> 8u) & 255u;
    if (triangle >= count) return false;
    material = LoadClusterMaterialSlotOffset(TransparencyInstances,
        LoadRenderInstanceMetadata(TransparencyProperties, CLUSTER_MATERIAL_SLOT_OFFSET_METADATA_WORD), request.InstanceID) +
        LocalMaterial(TransparencyPageHeap, cluster, request.PageOffset, triangle);
    if (TransparencyMaterialStates[material] == 0u) return false;
    bin = TransparencyMaterialBins[material];
    return true;
}

bool ProjectTransparency<TMaterials : ITransparencyMaterials>(uniform TMaterials materials,
    uint source, uint primitive, uint bin, uint material, uint view, out TransparencyTriangle value)
{
    value = {};
    float4 first, second, third;
    if (!materials.project(bin, source, view, primitive, first, second, third)) return false;
    if (first.w <= 0.0 || second.w <= 0.0 || third.w <= 0.0) return false;
    float2 size = float2(TransparencyUniforms.Width, TransparencyUniforms.Height);
    float2 a = (first.xy / first.w * float2(.5, -.5) + .5) * size;
    float2 b = (second.xy / second.w * float2(.5, -.5) + .5) * size;
    float2 c = (third.xy / third.w * float2(.5, -.5) + .5) * size;
    a = floor(a * CLUSTER_RASTER_SUBPIXEL_SCALE_F + .5) / CLUSTER_RASTER_SUBPIXEL_SCALE_F;
    b = floor(b * CLUSTER_RASTER_SUBPIXEL_SCALE_F + .5) / CLUSTER_RASTER_SUBPIXEL_SCALE_F;
    c = floor(c * CLUSTER_RASTER_SUBPIXEL_SCALE_F + .5) / CLUSTER_RASTER_SUBPIXEL_SCALE_F;
    float2 edge1 = b - a, edge2 = c - a;
    float area = edge1.x * edge2.y - edge1.y * edge2.x;
    if (area == 0.0 || (TransparencyMaterialStates[material] == 1u && area >= 0.0)) return false;
    int2 minimum = max(int2(0), int2(ceil(min(a, min(b, c)) - .5)));
    int2 maximum = min(int2(size) - 1, int2(floor(max(a, max(b, c)) - .5)));
    if (any(minimum > maximum)) return false;
    value.First = a; value.Second = b; value.Third = c;
    value.InverseArea = 1.0 / area;
    value.Depths = float3(first.z / first.w, second.z / second.w, third.z / third.w);
    value.Source = source; value.Primitive = primitive; value.Bin = bin; value.View = view;
    return true;
}

float TransparencyViewDepth(uint view, float2 pixel, float depth)
{
    float2 size = float2(TransparencyUniforms.Width, TransparencyUniforms.Height);
    float2 ndc = pixel / size * float2(2.0, -2.0) + float2(-1.0, 1.0);
    float4 world = mul(float4(ndc, depth, 1.0), TransparencyViews[view].ClipToWorld);
    float3 position = world.xyz / world.w;
    return max(-mul(float4(position, 1.0), TransparencyLightingViews[view].View).z,
        TransparencyLightingViews[view].ZParams.x);
}

void TransparencyCellBounds(TransparencyTriangle triangle, out uint3 minimum, out uint3 maximum)
{
    ClusteredLightGridParameters grid = TransparencyLightingViews[triangle.View];
    float2 low = max(0.0.xx, ceil(min(triangle.First, min(triangle.Second, triangle.Third)) - .5));
    float2 high = min(float2(TransparencyUniforms.Width, TransparencyUniforms.Height) - 1,
        floor(max(triangle.First, max(triangle.Second, triangle.Third)) - .5));
    float3 depths = float3(TransparencyViewDepth(triangle.View, triangle.First, triangle.Depths.x),
        TransparencyViewDepth(triangle.View, triangle.Second, triangle.Depths.y),
        TransparencyViewDepth(triangle.View, triangle.Third, triangle.Depths.z));
    minimum = uint3(uint2(low) / uint2(grid.TileSizeX, grid.TileSizeY), ClusteredLightDepthSlice(grid, min(depths.x, min(depths.y, depths.z))));
    maximum = uint3(uint2(high) / uint2(grid.TileSizeX, grid.TileSizeY), ClusteredLightDepthSlice(grid, max(depths.x, max(depths.y, depths.z))));
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
    uint source = TransparencyVisibleIndex(work / 128u) * 128u + work % 128u;
    uint bin, material, view;
    if (!TransparencySourceMaterial(source, bin, material, view)) return;
    uint primitiveCount = materials.primitiveCount(bin, source, view);
    for (uint primitive = 0u; primitive < primitiveCount; primitive++)
    {
        TransparencyTriangle triangle;
        if (!ProjectTransparency(materials, source, primitive, bin, material, view, triangle)) continue;
        uint index;
        TransparencyTriangleCount.InterlockedAdd(0u, 1u, index);
        if (index < TransparencyUniforms.TriangleCapacity) TransparencyTriangles[index] = triangle;
        if (TransparencyUniforms.UseIndex == 0u)
        {
            InterlockedAdd(TransparencyTileCounts[0], 1u);
            continue;
        }
        uint3 minimum, maximum;
        TransparencyCellBounds(triangle, minimum, maximum);
        for (uint z = minimum.z; z <= maximum.z; z++)
        for (uint y = minimum.y; y <= maximum.y; y++)
        for (uint x = minimum.x; x <= maximum.x; x++)
        {
            uint cell = TransparencyCell(view, uint3(x, y, z));
            InterlockedAdd(TransparencyTileCounts[cell], 1u);
            if (index >= TransparencyUniforms.TriangleCapacity) InterlockedOr(TransparencyCellStreams[cell], 1u);
        }
    }
}

void ReserveTransparency(uint3 thread)
{
    if (thread.x == 0u)
        TransparencyDispatch(36u, min(TransparencyTriangleCount.Load(0u), TransparencyUniforms.TriangleCapacity));
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
            TransparencyTriangleCount.InterlockedAdd(8u, count);
    }
}

void ScatterTransparency(uint3 thread)
{
    uint index = TransparencyWork(thread);
    if (TransparencyUniforms.UseIndex == 0u) return;
    if (index >= min(TransparencyTriangleCount.Load(0u), TransparencyUniforms.TriangleCapacity)) return;
    TransparencyTriangle triangle = TransparencyTriangles[index];
    uint3 minimum, maximum;
    TransparencyCellBounds(triangle, minimum, maximum);
    for (uint z = minimum.z; z <= maximum.z; z++)
    for (uint y = minimum.y; y <= maximum.y; y++)
    for (uint x = minimum.x; x <= maximum.x; x++)
    {
        uint cell = TransparencyCell(triangle.View, uint3(x, y, z));
        if (TransparencyCellStreams[cell] != 0u) continue;
        uint offset;
        InterlockedAdd(TransparencyCellWrites[cell], 1u, offset);
        TransparencyReferences[TransparencyCellOffsets[cell] + offset] = index;
    }
}

bool TransparencyCovered(TransparencyTriangle triangle, float2 pixel, out float depth)
{
    float2 edge1 = triangle.Second - triangle.First, edge2 = triangle.Third - triangle.First;
    float2 delta = pixel - triangle.First;
    float b = (delta.x * edge2.y - delta.y * edge2.x) * triangle.InverseArea;
    float c = (edge1.x * delta.y - edge1.y * delta.x) * triangle.InverseArea;
    float a = 1.0 - b - c;
    depth = dot(float3(a, b, c), triangle.Depths);
    int winding = triangle.InverseArea < 0.0 ? 1 : -1;
    int2 coordinate = int2(pixel - .5);
    return ClusterRasterIntegerEdgeAccepts(triangle.Second, triangle.Third, coordinate, winding) &&
        ClusterRasterIntegerEdgeAccepts(triangle.Third, triangle.First, coordinate, winding) &&
        ClusterRasterIntegerEdgeAccepts(triangle.First, triangle.Second, coordinate, winding);
}


// A pixel keeps its depth ordering while the candidate source advances through cells and batches.
struct TransparencyCandidates
{
    uint Slice, Reference, Work, Primitive, Projected;
    uint Cell, LastSlice;
    uint4 OccupiedSlices;
};

TransparencyCandidates BeginTransparencyCandidates(uint3 pixel, float opaque)
{
    TransparencyCandidates input = {};
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
    input.LastSlice = opaque < 1.0
        ? ClusteredLightDepthSlice(grid, TransparencyViewDepth(pixel.z, float2(pixel.xy) + .5, opaque))
        : grid.DepthSliceCount - 1u;
    return input;
}

bool ReadTransparencySource<TMaterials : ITransparencyMaterials>(uniform TMaterials materials,
    uint view, inout TransparencyCandidates input, out TransparencyTriangle triangle)
{
    triangle = {};
    uint count = TransparencyTriangleCount.Load(0u);
    if (count <= TransparencyUniforms.TriangleCapacity)
    {
        while (input.Projected < count)
        {
            triangle = TransparencyTriangles[input.Projected++];
            if (triangle.View == view) return true;
        }
        return false;
    }
    uint workCount = TransparencySourceCount() * 128u;
    while (input.Work < workCount)
    {
        uint source = TransparencyVisibleIndex(input.Work / 128u) * 128u + input.Work % 128u;
        uint bin, material, sourceView;
        if (TransparencySourceMaterial(source, bin, material, sourceView) && sourceView == view)
        {
            uint primitives = materials.primitiveCount(bin, source, view);
            while (input.Primitive < primitives)
                if (ProjectTransparency(materials, source, input.Primitive++, bin, material, view, triangle)) return true;
        }
        input.Work++; input.Primitive = 0u;
    }
    return false;
}

bool NextTransparencyCandidate<TMaterials : ITransparencyMaterials>(uniform TMaterials materials,
    uint3 pixel, inout TransparencyCandidates input, out TransparencyTriangle triangle)
{
    if (TransparencyUniforms.UseIndex == 0u)
    {
        input.Cell = 0xFFFFFFFFu;
        return ReadTransparencySource(materials, pixel.z, input, triangle);
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
                triangle = TransparencyTriangles[TransparencyReferences[TransparencyCellOffsets[cell] + input.Reference++]];
                input.Cell = cell;
                return true;
            }
        }
        else if (TransparencyTileCounts[cell] != 0u)
        {
            while (ReadTransparencySource(materials, pixel.z, input, triangle))
            {
                uint3 minimum, maximum;
                TransparencyCellBounds(triangle, minimum, maximum);
                uint3 position = uint3(tile, input.Slice);
                if (all(position >= minimum) && all(position <= maximum)) { input.Cell = cell; return true; }
            }
        }
        input.Slice++; input.Reference = input.Work = input.Primitive = input.Projected = 0u;
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

bool TransparencyHit<TMaterials : ITransparencyMaterials>(uniform TMaterials materials, uint3 pixel,
    TransparencyTriangle triangle, uint cell, float opaque, out float depth)
{
    if (!TransparencyCovered(triangle, float2(pixel.xy) + .5, depth)) return false;
    float resultDepth;
    if (!materials.cover(triangle.Bin, triangle.Source, triangle.Primitive, pixel, depth, resultDepth)) return false;
    depth = resultDepth;
    if (depth < 0.0 || depth > opaque) return false;
    return cell == 0xFFFFFFFFu || cell == ClusteredLightCellIndex(TransparencyLightingViews[pixel.z], pixel.z, pixel.xy,
        TransparencyViewDepth(pixel.z, float2(pixel.xy) + .5, depth));
}

void RequestTransparencyShadows<TMaterials : ITransparencyMaterials>(uniform TMaterials materials, uint3 pixel)
{
    if (pixel.x >= TransparencyUniforms.Width || pixel.y >= TransparencyUniforms.Height || pixel.z >= TransparencyUniforms.ViewCount) return;
    float opaque = TransparencyOpaqueDepth.Load(int4(pixel, 0));
    TransparencyCandidates input = BeginTransparencyCandidates(pixel, opaque);
    TransparencyTriangle triangle;
    while (NextTransparencyCandidate(materials, pixel, input, triangle))
    {
        float depth;
        if (!TransparencyHit(materials, pixel, triangle, input.Cell, opaque, depth)) continue;
        SurfaceReceiver receiver = (SurfaceReceiver)0;
        materials.receiver(triangle.Bin, triangle.Source, triangle.Primitive, pixel, depth,
            receiver.Position, receiver.GeometricNormal, receiver.Flags);
        receiver.Depth = depth;
        MarkVirtualShadowReceiver(pixel.xy, receiver);
    }
}

void ResolveTransparency<TMaterials : ITransparencyMaterials>(uniform TMaterials materials, uint3 pixel)
{
    if (pixel.x >= TransparencyUniforms.Width || pixel.y >= TransparencyUniforms.Height || pixel.z >= TransparencyUniforms.ViewCount) return;
    if (TransparencyTriangleCount.Load(0u) == 0u) return;
    float opaque = TransparencyOpaqueDepth.Load(int4(pixel, 0));
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
        TransparencyCandidates input = BeginTransparencyCandidates(pixel, opaque);
        TransparencyTriangle triangle;
        while (NextTransparencyCandidate(materials, pixel, input, triangle))
        {
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
        [unroll]
        for (uint item = 0u; item < 4u; item++)
        {
            if (bins[item] == 0xFFFFFFFFu) break;
            float4 surface = materials.shade(bins[item], sources[item], primitives[item], pixel, depths[item]);
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
    if (!contributed) return;
    TransparencyColor[pixel] = float4(color + transmission * background.rgb, background.a);
}
