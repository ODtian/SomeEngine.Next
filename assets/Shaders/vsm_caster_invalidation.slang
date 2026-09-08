#include "vsm_sampling.slang"
#include "resource_effects.slang"

struct VirtualShadowInvalidationUniforms
{
    uint ChangedInstanceCount;
    uint ViewCount;
    uint PageTableEntryCount;
    uint PageDilation;
};

ConstantBuffer<VirtualShadowInvalidationUniforms> Invalidation;
StructuredBuffer<VirtualShadowMapParameters> ShadowViews;
StructuredBuffer<float4> InstanceWorldBounds;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWStructuredBuffer<uint4> ShadowPageTable;

StructuredBuffer<uint> ChangedInstances;

bool ProjectShadowBounds(
    VirtualShadowMapParameters view,
    float3 worldMin,
    float3 worldMax,
    out float2 uvMin,
    out float2 uvMax)
{
    float3 corners[8];
    corners[0] = float3(worldMin.x, worldMin.y, worldMin.z);
    corners[1] = float3(worldMax.x, worldMin.y, worldMin.z);
    corners[2] = float3(worldMin.x, worldMax.y, worldMin.z);
    corners[3] = float3(worldMax.x, worldMax.y, worldMin.z);
    corners[4] = float3(worldMin.x, worldMin.y, worldMax.z);
    corners[5] = float3(worldMax.x, worldMin.y, worldMax.z);
    corners[6] = float3(worldMin.x, worldMax.y, worldMax.z);
    corners[7] = float3(worldMax.x, worldMax.y, worldMax.z);

    float2 ndcMin = 1e20.xx;
    float2 ndcMax = -1e20.xx;
    bool anyPoint = false;
    [unroll]
    for (uint corner = 0u; corner < 8u; corner++)
    {
        float4 clip = mul(float4(corners[corner], 1.0), view.LightViewProjection);
        if (clip.w <= 1e-5)
        {
            uvMin = 0.0.xx;
            uvMax = 1.0.xx;
            return true;
        }
        float2 ndc = clip.xy / clip.w;
        ndcMin = min(ndcMin, ndc);
        ndcMax = max(ndcMax, ndc);
        anyPoint = true;
    }
    if (!anyPoint || ndcMax.x < -1.0 || ndcMin.x > 1.0 ||
        ndcMax.y < -1.0 || ndcMin.y > 1.0)
    {
        uvMin = 0.0.xx;
        uvMax = 0.0.xx;
        return false;
    }
    ndcMin = clamp(ndcMin, -1.0.xx, 1.0.xx);
    ndcMax = clamp(ndcMax, -1.0.xx, 1.0.xx);
    float2 a = ndcMin * float2(0.5, -0.5) + 0.5.xx;
    float2 b = ndcMax * float2(0.5, -0.5) + 0.5.xx;
    uvMin = saturate(min(a, b));
    uvMax = saturate(max(a, b));
    return true;
}

groupshared uint4 FineRect[2];
groupshared int4 CoarseRect[2];

void BuildShadowRect(uint index, VirtualShadowMapParameters view, float3 worldMin, float3 worldMax)
{
    float2 uvMin;
    float2 uvMax;
    if (any(worldMin > worldMax) || !ProjectShadowBounds(view, worldMin, worldMax, uvMin, uvMax))
    {
        FineRect[index] = uint4(1u, 1u, 0u, 0u);
        CoarseRect[index] = int4(1, 1, 0, 0);
        return;
    }
    uint dimension = VirtualShadowPagesPerRow(view);
    uint2 low = min((uint2)(uvMin * float(dimension)), dimension - 1u);
    uint2 high = min((uint2)(uvMax * float(dimension)), dimension - 1u);
    low -= min(low, Invalidation.PageDilation.xx);
    high = min(high + Invalidation.PageDilation, dimension - 1u);
    FineRect[index] = uint4(low, high);
    // Both levels cover the same dilated footprint, including fractional clipmap origins.
    uint2 virtualMin = low * view.PageSize;
    uint2 virtualMax = min((high + 1u) * view.PageSize - 1u, view.VirtualResolution - 1u);
    CoarseRect[index] = int4(VirtualShadowCoarseWorldPage(view, virtualMin),
        VirtualShadowCoarseWorldPage(view, virtualMax));
}

void InvalidateShadowRect(uint index, uint lane, VirtualShadowMapParameters view)
{
    uint4 fine = FineRect[index];
    if (any(fine.xy > fine.zw))
        return;
    uint2 extent = fine.zw - fine.xy + 1u;
    for (uint cell = lane; cell < extent.x * extent.y; cell += 64u)
    {
        uint2 local = fine.xy + uint2(cell % extent.x, cell / extent.x);
        uint tableIndex = VirtualShadowPageTableIndex(view, local);
        if (tableIndex < Invalidation.PageTableEntryCount &&
            IsVirtualShadowPageTag(ShadowPageTable[tableIndex], VirtualShadowWorldPage(view, local)))
        {
            uint ignored;
            InterlockedExchange(ShadowPageTable[tableIndex].w, 0u, ignored);
        }
    }
    int4 coarse = CoarseRect[index];
    extent = uint2(coarse.zw - coarse.xy + 1);
    for (uint cell = lane; cell < extent.x * extent.y; cell += 64u)
    {
        int2 world = coarse.xy + int2(cell % extent.x, cell / extent.x);
        uint tableIndex = VirtualShadowCoarsePageTableIndex(view, world);
        if (tableIndex < Invalidation.PageTableEntryCount &&
            IsVirtualShadowPageTag(ShadowPageTable[tableIndex], world))
        {
            uint ignored;
            InterlockedExchange(ShadowPageTable[tableIndex].w, 0u, ignored);
        }
    }
}

[shader("compute")]
[numthreads(64, 1, 1)]
void InvalidateMovedVirtualShadowCasters(uint3 group : SV_GroupID, uint lane : SV_GroupIndex)
{
    uint changedIndex = group.x + group.y * 65535u;
    if (changedIndex >= Invalidation.ChangedInstanceCount || group.z >= Invalidation.ViewCount)
        return;
    uint instanceId = ChangedInstances[changedIndex];
    VirtualShadowMapParameters view = ShadowViews[group.z];
    if (lane == 0u)
    {
        BuildShadowRect(0u, view, InstanceWorldBounds[instanceId * 4u].xyz,
            InstanceWorldBounds[instanceId * 4u + 1u].xyz);
        BuildShadowRect(1u, view, InstanceWorldBounds[instanceId * 4u + 2u].xyz,
            InstanceWorldBounds[instanceId * 4u + 3u].xyz);
    }
    GroupMemoryBarrierWithGroupSync();
    InvalidateShadowRect(0u, lane, view);
    InvalidateShadowRect(1u, lane, view);
}
