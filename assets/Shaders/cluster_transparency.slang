#include "cluster_structures.slang"
#include "cluster_instance_properties.slang"
#include "visbuffer_encoding.slang"
#include "resource_effects.slang"
#include "vsm_receiver_requests.slang"
#include "cluster_raster_shared.slang"

// This unit is shared by all user material libraries in the static composition.
// Its interface carries geometry and colors, while each library keeps its vertex,
// domain, visibility, surface data and resource types.
interface ITransparencyMaterials
{
    bool project(uint bin, uint visibility, uint view, out float4 first, out float4 second, out float4 third);
    bool cover(uint bin, uint visibility, uint3 pixel, float depth, out float resultDepth);
    float4 shade(uint bin, uint visibility, uint3 pixel, float depth);
    void receiver(uint bin, uint visibility, uint3 pixel, float depth,
        out float3 position, out float3 normal, out uint flags);
};

struct TransparencyParameters
{
    uint Width;
    uint Height;
    uint ViewCount;
    uint MaterialCapacity;
    uint TriangleCapacity;
    uint VisibleCapacity;
    float ColorLimit;
    float MaximumError;
};

// One projected primitive is shared by all of its tile references and hits.
struct TransparencyTriangle
{
    float2 First;
    float2 Second;
    float2 Third;
    float InverseArea;
    float3 Depths;
    uint Visibility;
    uint Bin;
    uint View;
};

ConstantBuffer<TransparencyParameters> TransparencyUniforms;
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

RWStructuredBuffer<TransparencyTriangle> TransparencyTriangles;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWStructuredBuffer<uint> TransparencyTileCounts;
RWStructuredBuffer<uint> TransparencyReferences;
RWByteAddressBuffer TransparencyArguments;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWByteAddressBuffer TransparencyTriangleCount;

uint2 TransparencyTileExtent()
{
    return (uint2(TransparencyUniforms.Width, TransparencyUniforms.Height) + 15u) / 16u;
}

uint TransparencyTileIndex(uint3 pixel)
{
    uint2 extent = TransparencyTileExtent();
    return (pixel.z * extent.y + pixel.y / 16u) * extent.x + pixel.x / 16u;
}

uint TransparencyVisibleCount(ByteAddressBuffer arguments)
{
    return arguments.Load(4u) + arguments.Load(8u);
}

void ClearTransparency(uint3 thread)
{
    uint2 extent = TransparencyTileExtent();
    if (thread.x < extent.x * extent.y * TransparencyUniforms.ViewCount)
        TransparencyTileCounts[thread.x] = 0u;
    if (thread.x == 0u)
    {
        uint count = TransparencyVisibleCount(TransparencyFirstDraw) + TransparencyVisibleCount(TransparencySecondDraw);
        // Every visible cluster contributes at most 128 source triangles.
        uint groups = count * 2u;
        TransparencyArguments.Store3(0u, uint3(min(groups, 65535u), max(1u, (groups + 65534u) / 65535u), 1u));
        TransparencyTriangleCount.Store(0u, 0u);
    }
}

uint TransparencyVisibleIndex(uint source)
{
    uint firstSoftware = TransparencyFirstDraw.Load(4u);
    uint firstHardware = TransparencyFirstDraw.Load(8u);
    uint secondSoftware = TransparencySecondDraw.Load(4u);
    if (source < firstSoftware) return source;
    source -= firstSoftware;
    if (source < firstHardware) return TransparencyUniforms.VisibleCapacity - source - 1u;
    source -= firstHardware;
    if (source < secondSoftware) return firstSoftware + source;
    source -= secondSoftware;
    return TransparencyUniforms.VisibleCapacity - firstHardware - source - 1u;
}

void BuildTransparency<TMaterials : ITransparencyMaterials>(uniform TMaterials materials, uint3 thread)
{
    uint work = thread.x + thread.y * 65535u * 64u;
    uint source = work / 128u;
    if (source >= TransparencyVisibleCount(TransparencyFirstDraw) + TransparencyVisibleCount(TransparencySecondDraw)) return;
    uint visible = TransparencyVisibleIndex(source);
    DrawRequest request = VisibleClusterRequest(TransparencyVisibleClusters[visible]);
    uint view = DrawRequestViewIndex(request);
    if (view >= TransparencyUniforms.ViewCount) return;
    uint triangle = work % 128u;
    uint cluster = request.PageOffset + TransparencyPageHeap.Load(request.PageOffset + 16u) +
        DrawRequestClusterID(request) * PACKED_CLUSTER_STRIDE_BYTES;
    uint count = (TransparencyPageHeap.Load(cluster + 60u) >> 8u) & 255u;
    if (triangle >= count) return;
    uint material = LoadClusterMaterialSlotOffset(TransparencyInstances,
        LoadRenderInstanceMetadata(TransparencyProperties, CLUSTER_MATERIAL_SLOT_OFFSET_METADATA_WORD), request.InstanceID) +
        LocalMaterial(TransparencyPageHeap, cluster, request.PageOffset, triangle);
    if (TransparencyMaterialStates[material] == 0u) return;
    uint bin = TransparencyMaterialBins[material];
    uint visibility = EncodeVisBuffer(visible, triangle);
    float4 first, second, third;
    if (!materials.project(bin, visibility, view, first, second, third)) return;
    float2 size = float2(TransparencyUniforms.Width, TransparencyUniforms.Height);
    float2 a = (first.xy / first.w * float2(.5, -.5) + .5) * size;
    float2 b = (second.xy / second.w * float2(.5, -.5) + .5) * size;
    float2 c = (third.xy / third.w * float2(.5, -.5) + .5) * size;
    a = floor(a * CLUSTER_RASTER_SUBPIXEL_SCALE_F + .5) / CLUSTER_RASTER_SUBPIXEL_SCALE_F;
    b = floor(b * CLUSTER_RASTER_SUBPIXEL_SCALE_F + .5) / CLUSTER_RASTER_SUBPIXEL_SCALE_F;
    c = floor(c * CLUSTER_RASTER_SUBPIXEL_SCALE_F + .5) / CLUSTER_RASTER_SUBPIXEL_SCALE_F;
    float2 edge1 = b - a, edge2 = c - a;
    float area = edge1.x * edge2.y - edge1.y * edge2.x;
    if (first.w <= 0.0 || second.w <= 0.0 || third.w <= 0.0 || area == 0.0) return;
    // The user's material state selects whether both orientations contribute.
    if (TransparencyMaterialStates[material] == 1u && area >= 0.0) return;
    int2 minimum = max(int2(0), int2(ceil(min(a, min(b, c)) - .5)));
    int2 maximum = min(int2(size) - 1, int2(floor(max(a, max(b, c)) - .5)));
    if (any(minimum > maximum)) return;
    TransparencyTriangle value;
    value.First = a;
    value.Second = b;
    value.Third = c;
    value.InverseArea = 1.0 / area;
    value.Depths = float3(first.z / first.w, second.z / second.w, third.z / third.w);
    value.Visibility = visibility;
    value.Bin = bin;
    value.View = view;
    uint index;
    TransparencyTriangleCount.InterlockedAdd(0u, 1u, index);
    TransparencyTriangles[index] = value;
    uint2 extent = TransparencyTileExtent();
    for (uint y = uint(minimum.y) / 16u; y <= uint(maximum.y) / 16u; y++)
    for (uint x = uint(minimum.x) / 16u; x <= uint(maximum.x) / 16u; x++)
    {
        uint tile = (view * extent.y + y) * extent.x + x;
        uint offset;
        InterlockedAdd(TransparencyTileCounts[tile], 1u, offset);
        TransparencyReferences[tile * TransparencyUniforms.TriangleCapacity + offset] = index;
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

bool TransparencyBefore(float depth, uint identity, float otherDepth, uint otherIdentity)
{
    return depth < otherDepth || (depth == otherDepth && identity < otherIdentity);
}

void RequestTransparencyShadows<TMaterials : ITransparencyMaterials>(uniform TMaterials materials, uint3 pixel)
{
    if (pixel.x >= TransparencyUniforms.Width || pixel.y >= TransparencyUniforms.Height || pixel.z >= TransparencyUniforms.ViewCount) return;
    uint tile = TransparencyTileIndex(pixel);
    uint count = TransparencyTileCounts[tile];
    float opaque = TransparencyOpaqueDepth.Load(int4(pixel, 0));
    for (uint candidate = 0u; candidate < count; candidate++)
    {
        uint reference = TransparencyReferences[tile * TransparencyUniforms.TriangleCapacity + candidate];
        TransparencyTriangle triangle = TransparencyTriangles[reference];
        float depth;
        if (!TransparencyCovered(triangle, float2(pixel.xy) + .5, depth)) continue;
        if (!materials.cover(triangle.Bin, triangle.Visibility, pixel, depth, depth) || depth < 0.0 || depth > opaque) continue;
        SurfaceReceiver receiver = (SurfaceReceiver)0;
        materials.receiver(triangle.Bin, triangle.Visibility, pixel, depth,
            receiver.Position, receiver.GeometricNormal, receiver.Flags);
        receiver.Depth = depth;
        MarkVirtualShadowReceiver(pixel.xy, receiver);
    }
}

void ResolveTransparency<TMaterials : ITransparencyMaterials>(uniform TMaterials materials, uint3 pixel)
{
    if (pixel.x >= TransparencyUniforms.Width || pixel.y >= TransparencyUniforms.Height || pixel.z >= TransparencyUniforms.ViewCount) return;
    uint tile = TransparencyTileIndex(pixel);
    uint count = TransparencyTileCounts[tile];
    if (count == 0u) return;
    float opaque = TransparencyOpaqueDepth.Load(int4(pixel, 0));
    float4 background = TransparencyColor[pixel];
    float3 color = 0.0.xxx;
    float transmission = 1.0;
    float previousDepth = -1.0;
    uint previousIdentity = 0u;
    bool remaining = true;
    while (remaining)
    {
        float4 depths = 2.0.xxxx;
        uint4 references = 0xFFFFFFFFu.xxxx;
        uint4 identities = 0xFFFFFFFFu.xxxx;
        [loop]
        for (uint candidate = 0u; candidate < count; candidate++)
        {
            uint reference = TransparencyReferences[tile * TransparencyUniforms.TriangleCapacity + candidate];
            TransparencyTriangle triangle = TransparencyTriangles[reference];
            float depth;
            if (!TransparencyCovered(triangle, float2(pixel.xy) + .5, depth)) continue;
            if (!materials.cover(triangle.Bin, triangle.Visibility, pixel, depth, depth) || depth < 0.0 || depth > opaque) continue;
            uint identity = triangle.Visibility;
            if (!TransparencyBefore(previousDepth, previousIdentity, depth, identity)) continue;
            [unroll]
            for (uint item = 0u; item < 4u; item++)
            {
                if (TransparencyBefore(depth, identity, depths[item], identities[item]))
                {
                    float displacedDepth = depths[item]; depths[item] = depth; depth = displacedDepth;
                    uint displacedReference = references[item]; references[item] = reference; reference = displacedReference;
                    uint displacedIdentity = identities[item]; identities[item] = identity; identity = displacedIdentity;
                }
            }
        }
        remaining = references.w != 0xFFFFFFFFu;
        [unroll]
        for (uint item = 0u; item < 4u; item++)
        {
            if (references[item] == 0xFFFFFFFFu) break;
            TransparencyTriangle triangle = TransparencyTriangles[references[item]];
            float4 surface = materials.shade(triangle.Bin, triangle.Visibility, pixel, depths[item]);
            float alpha = saturate(surface.a);
            color += transmission * alpha * surface.rgb;
            transmission *= 1.0 - alpha;
            previousDepth = depths[item];
            previousIdentity = identities[item];
        }
        float limit = max(TransparencyUniforms.ColorLimit, max(background.r, max(background.g, background.b)));
        if (remaining && TransparencyUniforms.MaximumError > 0.0 && transmission * limit * .5 <= TransparencyUniforms.MaximumError)
        {
            color += transmission * limit * .5;
            transmission = 0.0;
            break;
        }
    }
    TransparencyColor[pixel] = float4(color + transmission * background.rgb, background.a);
}
