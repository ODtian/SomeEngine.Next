#ifndef CLUSTER_COMMON_SLANG
#define CLUSTER_COMMON_SLANG

#include "common.slang"
#include "cluster_structures.slang"
#include "cluster_instance_properties.slang"
#include "visbuffer_encoding.slang"

// ── Page Heap helpers ──
// These require a `ByteAddressBuffer PageHeap` to be declared in the calling shader.

/// Load only Cluster-owned topology plus the opaque vertex-blob view.
void LoadClusterInfo(ByteAddressBuffer pageHeap, uint pageOffset, uint clusterId,
                     out uint vertexBlobAddress, out uint vertexBlobSize,
                     out uint tStart, out uint packedCounts)
{
    uint clustersOffset = pageHeap.Load(pageOffset + 16);
    uint vertexBlobsOffset = pageHeap.Load(pageOffset + 20);
    uint clusterOffset = pageOffset + clustersOffset + clusterId * PACKED_CLUSTER_STRIDE_BYTES;
    vertexBlobAddress = pageOffset + vertexBlobsOffset + pageHeap.Load(clusterOffset + 0u);
    vertexBlobSize = pageHeap.Load(clusterOffset + 4u);
    tStart = pageHeap.Load(clusterOffset + 56u) >> 16;
    packedCounts = pageHeap.Load(clusterOffset + 60u);
}

uint GetClusterLocalMaterial(ByteAddressBuffer pageHeap, uint pageOffset, uint clusterId, uint triangle)
{
    uint clusterOffset = pageOffset + pageHeap.Load(pageOffset + 16u) + clusterId * PACKED_CLUSTER_STRIDE_BYTES;
    return LocalMaterial(pageHeap.Load(clusterOffset + 68u), pageHeap.Load(clusterOffset + 72u), triangle);
}

/// Fetch a single uint8 vertex index from the page heap.
uint FetchVertexIndex(ByteAddressBuffer pageHeap, uint pageOffset, uint idxOffset,
                      uint tStart, uint triIdx, uint corner)
{
    uint indexByteAddr = pageOffset + idxOffset + tStart + triIdx * 3 + corner;
    uint alignedAddr = indexByteAddr & ~3;
    uint shift = (indexByteAddr & 3) * 8;
    uint packedIndex = pageHeap.Load(alignedAddr);
    return (packedIndex >> shift) & 0xFF;
}

// ── Instance Data ──
ByteAddressBuffer InstanceData;
ConstantBuffer<RenderInstancePropertyMetadata> InstanceProperties;

RenderInstanceTransform GetCurrentInstanceTransform(uint instanceID)
{
    return LoadRenderTransform(
        InstanceData,
        LoadRenderInstanceMetadata(InstanceProperties, RENDER_CURRENT_TRANSFORM_METADATA_WORD),
        instanceID);
}

RenderInstanceTransform GetPreviousInstanceTransform(uint instanceID)
{
    return LoadRenderTransform(
        InstanceData,
        LoadRenderInstanceMetadata(InstanceProperties, RENDER_PREVIOUS_TRANSFORM_METADATA_WORD),
        instanceID);
}

uint GetClusterInstanceBvhRoot(uint instanceID)
{
    return LoadClusterBvhRoot(
        InstanceData,
        LoadRenderInstanceMetadata(InstanceProperties, CLUSTER_BVH_ROOT_METADATA_WORD),
        instanceID);
}

uint GetClusterMaterialSlotOffset(uint instanceID)
{
    return LoadClusterMaterialSlotOffset(
        InstanceData,
        LoadRenderInstanceMetadata(InstanceProperties, CLUSTER_MATERIAL_SLOT_OFFSET_METADATA_WORD),
        instanceID);
}

float GetInstanceBoundsExpansion(uint instanceID)
{
    return LoadClusterBoundsExpansion(
        InstanceData,
        LoadRenderInstanceMetadata(InstanceProperties, CLUSTER_BOUNDS_EXPANSION_METADATA_WORD),
        instanceID);
}

// ── Screen-space utilities ──

/// Project world position to screen space, returning (screenX, screenY, ndcDepth).
float3 ProjectToScreen(float3 worldPos, float4x4 viewProj, uint screenWidth, uint screenHeight)
{
    float4 clip = mul(float4(worldPos, 1.0), viewProj);
    float3 ndc = clip.xyz / clip.w;
    float2 screen;
    screen.x = (ndc.x * 0.5 + 0.5) * float(screenWidth);
    screen.y = (1.0 - (ndc.y * 0.5 + 0.5)) * float(screenHeight);
    return float3(screen, ndc.z);
}

struct ScreenProjection
{
    float2 Pixel;
    float InvW;
    float Depth;
};

ScreenProjection ProjectToScreenProjection(float3 worldPos, float4x4 viewProj, uint screenWidth, uint screenHeight)
{
    float4 clip = mul(float4(worldPos, 1.0), viewProj);
    float invW = 1.0 / clip.w;
    float2 ndc = clip.xy * invW;

    ScreenProjection projection;
    projection.Pixel.x = (ndc.x * 0.5 + 0.5) * float(screenWidth);
    projection.Pixel.y = (1.0 - (ndc.y * 0.5 + 0.5)) * float(screenHeight);
    projection.InvW = invW;
    projection.Depth = clip.z * invW;
    return projection;
}

/// Compute barycentric coordinates from 2D screen positions.
float3 ComputeBarycentric(float2 p, float2 v0, float2 v1, float2 v2)
{
    float2 e0 = v1 - v0;
    float2 e1 = v2 - v0;
    float2 ep = p - v0;

    // Solve the two screen-space edge equations directly. Forming their Gram
    // matrix adds work and squares the condition number for thin triangles.
    float invDenom = 1.0 / (e0.x * e1.y - e0.y * e1.x);
    float u = (ep.x * e1.y - ep.y * e1.x) * invDenom;
    float v = (e0.x * ep.y - e0.y * ep.x) * invDenom;

    return float3(1.0 - u - v, u, v);
}

float3 ComputePerspectiveBarycentric(float2 p, ScreenProjection v0, ScreenProjection v1, ScreenProjection v2)
{
    float3 bary = ComputeBarycentric(p, v0.Pixel, v1.Pixel, v2.Pixel);
    float3 weighted = bary * float3(v0.InvW, v1.InvW, v2.InvW);
    float denom = weighted.x + weighted.y + weighted.z;
    if (abs(denom) < 1e-8)
        return float3(0.0, 0.0, 0.0);
    return weighted / denom;
}

// ── Debug / visualization ──

uint PCGHash(uint input)
{
    uint state = input * 747796405u + 2891336453u;
    uint word = ((state >> ((state >> 28u) + 4u)) ^ state) * 277803737u;
    return (word >> 22u) ^ word;
}

float3 ColorFromHash(uint hash)
{
    float r = float(hash & 255u) / 255.0;
    float g = float((hash >> 8) & 255u) / 255.0;
    float b = float((hash >> 16) & 255u) / 255.0;
    return float3(r, g, b);
}

#endif
