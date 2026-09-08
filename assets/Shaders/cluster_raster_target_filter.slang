#ifndef CLUSTER_RASTER_TARGET_FILTER_SLANG
#define CLUSTER_RASTER_TARGET_FILTER_SLANG

#include "cluster_raster_region_access.slang"
Texture2DArray<float> RasterTargetCurrentHiZ;
Texture2DArray<float> RasterTargetPreviousHiZ;
StructuredBuffer<uint> RasterRegionGrid;

uint4 ClusterRasterGridBounds(float2 minUv, float2 maxUv, ClusterViewUniforms view)
{
    uint dimension = max(view.Pad6, 1u);
    uint2 first = min(uint2(saturate(minUv) * dimension), dimension - 1u);
    uint2 last = min(uint2(saturate(maxUv) * dimension), dimension - 1u);
    return uint4(first, last);
}

uint ClusterRasterGridMask(uint row, uint word, uint4 bounds, ClusterViewUniforms view)
{
    uint mask = RasterRegionGrid[view.TargetViewIndex + row * ((view.Pad6 + 31u) / 32u) + word];
    if (word == bounds.x / 32u)
        mask &= 0xFFFFFFFFu << (bounds.x & 31u);
    if (word == bounds.z / 32u)
        mask &= 0xFFFFFFFFu >> (31u - (bounds.z & 31u));
    return mask;
}

uint ClusterRasterGridRegion(uint row, uint column, ClusterViewUniforms view)
{
    return RasterRegionGrid[view.Pad5 + row * view.Pad6 + column] - 1u;
}

bool ClusterTargetPolicyScreenBoundsAccepts(
    float2 minUv,
    float2 maxUv,
    ClusterViewUniforms view)
{
    if (any(maxUv < 0.0.xx) || any(minUv > 1.0.xx) || any(maxUv < minUv))
        return false;
    uint4 bounds = ClusterRasterGridBounds(minUv, maxUv, view);
    for (uint row = bounds.y; row <= bounds.w; row++)
        for (uint word = bounds.x / 32u; word <= bounds.z / 32u; word++)
            if (ClusterRasterGridMask(row, word, bounds, view) != 0u)
                return true;
    return false;
}

bool RasterTargetWorldAabbOutsideFrustum(
    float3 worldMin,
    float3 worldMax,
    bool usePrevious,
    ClusterViewUniforms view)
{
    float4x4 viewProjection = usePrevious ? view.PrevViewProj : view.ViewProj;
    bool4 outsideSides = true.xxxx;
    bool2 outsideDepth = true.xx;
    [unroll]
    for (uint index = 0u; index < 8u; index++)
    {
        float3 corner = float3(
            (index & 1u) != 0u ? worldMax.x : worldMin.x,
            (index & 2u) != 0u ? worldMax.y : worldMin.y,
            (index & 4u) != 0u ? worldMax.z : worldMin.z);
        float4 clip = mul(float4(corner, 1.0), viewProjection);
        outsideSides &= bool4(
            clip.x < -clip.w,
            clip.x > clip.w,
            clip.y < -clip.w,
            clip.y > clip.w);
        outsideDepth &= bool2(clip.z < 0.0, clip.z > clip.w);
    }
    return any(outsideSides) || any(outsideDepth);
}

float SampleRasterTargetHiZMax(
    Texture2DArray<float> source,
    float2 minUv,
    float2 maxUv,
    uint mip,
    uint layer,
    ClusterViewUniforms view)
{
    float2 fullSize = 1.0.xx / max(view.HiZInvSize, 1e-6.xx);
    float divisor = exp2(float(mip));
    float2 mipMax = max(1.0.xx, floor(fullSize / divisor)) - 1.0.xx;
    float2 pixelMin = minUv * fullSize / divisor;
    float2 pixelMax = maxUv * fullSize / divisor;
    int2 p0 = int2(clamp(pixelMin, 0.0.xx, mipMax));
    int2 p1 = int2(clamp(float2(pixelMax.x, pixelMin.y), 0.0.xx, mipMax));
    int2 p2 = int2(clamp(float2(pixelMin.x, pixelMax.y), 0.0.xx, mipMax));
    int2 p3 = int2(clamp(pixelMax, 0.0.xx, mipMax));
    return max(
        max(source.Load(int4(p0, layer, mip)), source.Load(int4(p1, layer, mip))),
        max(source.Load(int4(p2, layer, mip)), source.Load(int4(p3, layer, mip))));
}

bool ClusterTargetPolicyAccepts(
    ClusterProjectedBounds projectedBounds,
    ClusterViewUniforms view)
{
    if (view.TargetPolicy == 0u)
        return true;
    if (view.CullingEnabled == 0u || projectedBounds.CrossesNearPlane)
        return ClusterTargetPolicyScreenBoundsAccepts(0.0.xx, 1.0.xx, view);
    return ClusterTargetPolicyScreenBoundsAccepts(
        float2(projectedBounds.NdcMin.x, -projectedBounds.NdcMax.y) * 0.5 + 0.5,
        float2(projectedBounds.NdcMax.x, -projectedBounds.NdcMin.y) * 0.5 + 0.5,
        view);
}

bool ClusterRasterRegionOccluded(
    float2 minUv,
    float2 maxUv,
    float nearDepth,
    bool usePrevious,
    ClusterViewUniforms view)
{
    ClusterRasterTargetInvocation invocation =
        RasterTargetInvocations[view.TargetViewIndex];
    uint layer = usePrevious
        ? invocation.PreviousHiZLayer
        : invocation.CurrentHiZLayer;
    if (layer == CLUSTER_INVALID_HIZ_LAYER || view.HiZMipCount == 0u)
        return false;

    float2 pixelExtent = float2(max(invocation.OutputExtent, 1u.xx));
    float2 pixelDiameter = max((maxUv - minUv) * pixelExtent, 1.0.xx);
    uint mip = min(
        uint(max(0.0, ceil(log2(max(pixelDiameter.x, pixelDiameter.y))))),
        view.HiZMipCount - 1u);
    float hizDepth = usePrevious
        ? SampleRasterTargetHiZMax(
            RasterTargetPreviousHiZ,
            minUv,
            maxUv,
            mip,
            layer,
            view)
        : SampleRasterTargetHiZMax(
            RasterTargetCurrentHiZ,
            minUv,
            maxUv,
            mip,
            layer,
            view);
    float epsilon = 5e-5 + 0.5 * max(view.HiZInvSize.x, view.HiZInvSize.y);
    return nearDepth > hizDepth + epsilon;
}

bool ClusterTargetPolicyScreenBoundsOccluded(
    float2 minUv,
    float2 maxUv,
    float nearDepth,
    bool usePrevious,
    ClusterViewUniforms view)
{
    if (view.ViewPadding != 0u)
        return ClusterRasterRegionOccluded(minUv, maxUv, nearDepth, usePrevious, view);
    uint4 bounds = ClusterRasterGridBounds(minUv, maxUv, view);
    for (uint row = bounds.y; row <= bounds.w; row++)
    {
        for (uint word = bounds.x / 32u; word <= bounds.z / 32u; word++)
        {
            uint mask = ClusterRasterGridMask(row, word, bounds, view);
            while (mask != 0u)
            {
                uint column = word * 32u + firstbitlow(mask);
                mask &= mask - 1u;
                uint regionIndex = ClusterRasterGridRegion(row, column, view);
                ClusterViewUniforms regionView = ClusterApplyRasterRegion(
                    view, RasterTargetInvocations[regionIndex], regionIndex);
                float2 page = float2(column, row);
                if (!ClusterRasterRegionOccluded(
                        saturate(minUv * view.Pad6 - page),
                        saturate(maxUv * view.Pad6 - page),
                        nearDepth, usePrevious, regionView))
                    return false;
            }
        }
    }
    return true;
}

bool ClusterTargetPolicyWorldBoundsOccluded(
    float3 worldMin,
    float3 worldMax,
    bool usePrevious,
    ClusterViewUniforms view)
{
    if (usePrevious && view.HasPrevHistory == 0u)
        return false;
    if (RasterTargetWorldAabbOutsideFrustum(
            worldMin,
            worldMax,
            usePrevious,
            view))
        return false;

    float2 minUv;
    float2 maxUv;
    float nearDepth;
    uint unusedMip;
    BuildWorldAabbScreenBoundsAndNearDepth(
        worldMin,
        worldMax,
        usePrevious,
        view,
        minUv,
        maxUv,
        nearDepth,
        unusedMip);
    return ClusterTargetPolicyScreenBoundsOccluded(
        minUv,
        maxUv,
        nearDepth,
        usePrevious,
        view);
}

#endif
