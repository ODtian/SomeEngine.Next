#ifndef CLUSTER_HIZ_SHARED_SLANG
#define CLUSTER_HIZ_SHARED_SLANG

bool WorldAabbOutsideFrustum(
    float3 worldMin,
    float3 worldMax,
    bool usePrev,
    ClusterViewUniforms view)
{
    float4x4 viewProjection = usePrev ? view.PrevViewProj : view.ViewProj;
    bool allLeft = true;
    bool allRight = true;
    bool allBottom = true;
    bool allTop = true;
    bool allNear = true;
    bool allFar = true;
    [unroll]
    for (uint index = 0u; index < 8u; index++)
    {
        float3 corner = float3(
            (index & 1u) != 0u ? worldMax.x : worldMin.x,
            (index & 2u) != 0u ? worldMax.y : worldMin.y,
            (index & 4u) != 0u ? worldMax.z : worldMin.z);
        float4 clip = mul(float4(corner, 1.0), viewProjection);
        allLeft = allLeft && clip.x < -clip.w;
        allRight = allRight && clip.x > clip.w;
        allBottom = allBottom && clip.y < -clip.w;
        allTop = allTop && clip.y > clip.w;
        allNear = allNear && clip.z < 0.0;
        allFar = allFar && clip.z > clip.w;
    }
    return allLeft || allRight || allBottom || allTop || allNear || allFar;
}

void BuildWorldAabbScreenBoundsAndNearDepth(
    float3 worldMin,
    float3 worldMax,
    bool usePrev,
    ClusterViewUniforms view,
    out float2 minUv,
    out float2 maxUv,
    out float nearDepth,
    out uint mipLevel)
{
    float4x4 viewProjMat = usePrev ? view.PrevViewProj : view.ViewProj;
    float2 ndcMin = 1e20.xx;
    float2 ndcMax = -1e20.xx;
    uint validCornerCount = 0u;
    bool touchesNearPlane = false;
    nearDepth = 1.0;

    [unroll]
    for (uint index = 0u; index < 8u; index++)
    {
        float3 corner = float3(
            (index & 1u) != 0u ? worldMax.x : worldMin.x,
            (index & 2u) != 0u ? worldMax.y : worldMin.y,
            (index & 4u) != 0u ? worldMax.z : worldMin.z);
        float4 clip = mul(float4(corner, 1.0), viewProjMat);
        if (clip.w <= 1e-5 || clip.z < 0.0)
        {
            touchesNearPlane = true;
            continue;
        }

        float3 ndc = clip.xyz / clip.w;
        ndcMin = min(ndcMin, ndc.xy);
        ndcMax = max(ndcMax, ndc.xy);
        nearDepth = min(nearDepth, saturate(ndc.z));
        validCornerCount++;
    }

    if (touchesNearPlane || validCornerCount == 0u)
    {
        minUv = 0.0.xx;
        maxUv = 1.0.xx;
        nearDepth = 0.0;
    }
    else
    {
        ndcMin = clamp(ndcMin, -1.0.xx, 1.0.xx);
        ndcMax = clamp(ndcMax, -1.0.xx, 1.0.xx);
        float2 uv0 = ndcMin * float2(0.5, -0.5) + 0.5.xx;
        float2 uv1 = ndcMax * float2(0.5, -0.5) + 0.5.xx;
        minUv = saturate(min(uv0, uv1));
        maxUv = saturate(max(uv0, uv1));
    }

    float2 onePixel = float2(1.0 / view.ScreenWidth, 1.0 / view.ScreenHeight);
    if (view.HiZMipCount > 0u)
        onePixel = max(view.HiZInvSize, 1e-6.xx);
    // Raster selection needs the geometry footprint, without HiZ's minimum
    // query size. Expand only when sampling the hierarchy below.
    float2 queryMaxUv = min(1.0.xx, max(maxUv, minUv + onePixel));
    float pixelDiameterX = (queryMaxUv.x - minUv.x) / onePixel.x;
    float pixelDiameterY = (queryMaxUv.y - minUv.y) / onePixel.y;
    float pixelDiameter = max(1.0, max(pixelDiameterX, pixelDiameterY));
    float mipValue = max(0.0, ceil(log2(pixelDiameter)));
    uint maxMip = view.HiZMipCount > 0u ? view.HiZMipCount - 1u : 0u;
    mipLevel = uint(clamp(int(mipValue), 0, int(maxMip)));

    if (any(isnan(minUv)) || any(isnan(maxUv)) || isnan(nearDepth))
    {
        minUv = 0.0.xx;
        maxUv = 1.0.xx;
        nearDepth = 0.0;
        mipLevel = 0u;
    }
}

float SampleHiZMax(
    Texture2DArray<float> hizTexture,
    float2 minUv,
    float2 maxUv,
    uint mipLevel,
    uint viewIndex,
    ClusterViewUniforms view)
{
    maxUv = min(1.0.xx, max(maxUv, minUv + max(view.HiZInvSize, 1e-6.xx)));
    float2 fullSize = 1.0.xx / max(view.HiZInvSize, 1e-6.xx);
    float2 pixelMin = minUv * fullSize;
    float2 pixelMax = maxUv * fullSize;
    float divisor = exp2(float(mipLevel));
    float2 mipSize = max(1.0.xx, floor(fullSize / divisor));
    float2 maxCoordinate = mipSize - 1.0.xx;
    int2 p0 = int2(clamp(pixelMin / divisor, 0.0.xx, maxCoordinate));
    int2 p1 = int2(clamp(
        float2(pixelMax.x, pixelMin.y) / divisor,
        0.0.xx,
        maxCoordinate));
    int2 p2 = int2(clamp(
        float2(pixelMin.x, pixelMax.y) / divisor,
        0.0.xx,
        maxCoordinate));
    int2 p3 = int2(clamp(pixelMax / divisor, 0.0.xx, maxCoordinate));
    float d0 = hizTexture.Load(int4(p0, viewIndex, mipLevel));
    float d1 = hizTexture.Load(int4(p1, viewIndex, mipLevel));
    float d2 = hizTexture.Load(int4(p2, viewIndex, mipLevel));
    float d3 = hizTexture.Load(int4(p3, viewIndex, mipLevel));
    return max(max(d0, d1), max(d2, d3));
}

bool IsWorldAabbOccludedByHiZ(
    Texture2DArray<float> hizTexture,
    float3 worldMin,
    float3 worldMax,
    bool usePrev,
    uint viewIndex,
    ClusterViewUniforms view,
    out float2 minUv,
    out float2 maxUv)
{
    if (view.CullingEnabled == 0u || view.HiZMipCount == 0u)
    {
        minUv = 0.0.xx;
        maxUv = 1.0.xx;
        return false;
    }

    float nearDepth;
    uint mipLevel;
    BuildWorldAabbScreenBoundsAndNearDepth(
        worldMin,
        worldMax,
        usePrev,
        view,
        minUv,
        maxUv,
        nearDepth,
        mipLevel);
    float epsilon = 5e-5 + 0.5 * max(view.HiZInvSize.x, view.HiZInvSize.y);
    float hizDepth = SampleHiZMax(
        hizTexture,
        minUv,
        maxUv,
        mipLevel,
        viewIndex,
        view);
    return nearDepth > hizDepth + epsilon;
}

#endif
