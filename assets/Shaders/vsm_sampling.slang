#ifndef VSM_SAMPLING_SLANG
#define VSM_SAMPLING_SLANG

__exported import vsm_types;
__exported import vsm_addressing;
__exported import vsm_receiver_mask_common;

__exported import vsm_page_entry;

#ifndef VSM_WHOLE_PAGE_SAMPLING
Texture2D<uint> VirtualShadowReceiverMasks;
#endif

bool TryResolveVirtualShadowPage(
    VirtualShadowMapParameters parameters,
    StructuredBuffer<uint> pageTable,
    uint mipLevel,
    uint2 localPage,
    out uint atlasPage)
{
    int2 worldPage = VirtualShadowWorldPage(parameters, mipLevel, localPage);
    uint entry = pageTable[
        VirtualShadowPageTableIndex(parameters, mipLevel, localPage)];
    return IsVirtualShadowPageTag(entry, worldPage) &&
        (entry & VSM_PAGE_PARTIAL_DEPTH) == 0u &&
        DecodeVirtualShadowPage(entry, parameters.AtlasPagesPerRow, atlasPage);
}

bool TryResolveVirtualShadowPixel(
    VirtualShadowMapParameters parameters,
    StructuredBuffer<uint> pageTable,
    uint mipLevel,
    uint2 mipPixel,
    out uint atlasPage,
    out bool wholePage)
{
    uint2 localPage = mipPixel >> firstbithigh(parameters.PageSize);
    uint tableIndex = VirtualShadowPageTableIndex(parameters, mipLevel, localPage);
    uint entry = pageTable[tableIndex];
    wholePage = (entry & VSM_PAGE_PARTIAL_DEPTH) == 0u;
    if (!IsVirtualShadowPageTag(entry, VirtualShadowWorldPage(parameters, mipLevel, localPage)) ||
        !DecodeVirtualShadowPage(entry, parameters.AtlasPagesPerRow, atlasPage))
        return false;
    if (wholePage) return true;
#ifndef VSM_WHOLE_PAGE_SAMPLING
    if ((entry & VSM_PAGE_PARTIAL_DEPTH) != 0u)
        return true;
#endif
    return false;
}

bool TryReadVirtualShadowMipReverseDepth(
    VirtualShadowMapParameters parameters,
    StructuredBuffer<uint> pageTable,
    Texture2DArray<uint> atlas,
    uint mipLevel,
    uint2 mipPixel,
    out float storedDepth)
{
    uint atlasPage;
    bool wholePage;
    if (!TryResolveVirtualShadowPixel(
            parameters, pageTable, mipLevel, mipPixel, atlasPage, wholePage))
    {
        storedDepth = 0.0;
        return false;
    }
    storedDepth = ReadVirtualShadowReverseDepth(
        parameters,
        atlas,
        atlasPage,
        mipPixel & (parameters.PageSize - 1u));
    return true;
}

bool TryReadVirtualShadowMipDepth(VirtualShadowMapParameters parameters,
    StructuredBuffer<uint> pageTable, Texture2DArray<uint> atlas,
    uint mipLevel, uint2 mipPixel, out float storedDepth)
{
    float reverseDepth;
    bool resident = TryReadVirtualShadowMipReverseDepth(parameters, pageTable, atlas,
        mipLevel, mipPixel, reverseDepth);
    storedDepth = resident ? 1.0 - reverseDepth : 0.0;
    return resident;
}

bool TryResolveVirtualShadowResidentPixel(
    VirtualShadowMapParameters parameters, StructuredBuffer<uint> pageTable,
    StructuredBuffer<uint> residentPages, uint firstMip, float2 uv,
    out uint resolvedMip, out uint atlasPage, out bool wholePage)
{
    uint mipCount = max(parameters.MipLevelCount, 1u);
    uint mip = clamp(firstMip, VirtualShadowFirstMip(parameters), mipCount - 1u);
    while (mip < mipCount)
    {
        uint resolution = VirtualShadowMipResolution(parameters, mip);
        uint2 pixel = min(uint2(uv * float(resolution)), resolution - 1u);
        uint2 localPage = pixel / parameters.PageSize;
        uint source = pageTable[VirtualShadowPageTableIndex(parameters, mip, localPage)];
        if ((source & VSM_PAGE_ANY_DEPTH) == 0u) { mip++; continue; }
        resolvedMip = mip + VirtualShadowPageLevelOffset(source);
        resolution = VirtualShadowMipResolution(parameters, resolvedMip);
        pixel = min(uint2(uv * float(resolution)), resolution - 1u);
        uint sourceIndex = VirtualShadowPageTableIndex(parameters, resolvedMip, pixel / parameters.PageSize);
        uint entry = pageTable[sourceIndex];
        wholePage = (entry & VSM_PAGE_PARTIAL_DEPTH) == 0u;
        bool completed = wholePage || (entry & VSM_PAGE_PARTIAL_DEPTH) != 0u;
        if (completed && IsVirtualShadowPageTag(entry, VirtualShadowWorldPage(parameters, resolvedMip, pixel / parameters.PageSize)) &&
            DecodeVirtualShadowPage(entry, parameters.AtlasPagesPerRow, atlasPage))
        {
            if (wholePage) return true;
#ifndef VSM_WHOLE_PAGE_SAMPLING
            return true;
#endif
        }
        mip = resolvedMip + 1u;
    }
    resolvedMip = mipCount - 1u;
    atlasPage = 0u;
    wholePage = false;
    return false;
}

bool TryReadVirtualShadowResidentReverseDepth(VirtualShadowMapParameters parameters,
    StructuredBuffer<uint> pageTable, StructuredBuffer<uint> residentPages, Texture2DArray<uint> atlas,
    uint firstMip, float2 uv, out float storedDepth, out uint resolvedMip)
{
    uint page;
    bool whole;
    if (!TryResolveVirtualShadowResidentPixel(parameters, pageTable, residentPages, firstMip, uv,
            resolvedMip, page, whole)) { storedDepth = 0.0; return false; }
    uint resolution = VirtualShadowMipResolution(parameters, resolvedMip);
    uint2 pixel = min(uint2(uv * float(resolution)), resolution - 1u);
    storedDepth = ReadVirtualShadowReverseDepth(parameters, atlas, page, pixel & (parameters.PageSize - 1u));
    return true;
}

bool TryReadVirtualShadowResidentDepth(VirtualShadowMapParameters parameters,
    StructuredBuffer<uint> pageTable, StructuredBuffer<uint> residentPages, Texture2DArray<uint> atlas,
    uint firstMip, float2 uv, out float storedDepth, out uint resolvedMip)
{
    float reverseDepth;
    bool resident = TryReadVirtualShadowResidentReverseDepth(parameters, pageTable, residentPages,
        atlas, firstMip, uv, reverseDepth, resolvedMip);
    storedDepth = resident ? 1.0 - reverseDepth : 0.0;
    return resident;
}

#ifdef VSM_RESIDENT_LOOKUP_ENABLED
StructuredBuffer<uint> VirtualShadowResidentPages;
#endif

bool TryReadVirtualShadowReverseDepthAtOrCoarser(
    VirtualShadowMapParameters parameters,
    StructuredBuffer<uint> pageTable,
    Texture2DArray<uint> atlas,
    uint firstMip,
    float2 uv,
    out float storedDepth,
    out uint resolvedMip)
{
#ifdef VSM_RESIDENT_LOOKUP_ENABLED
    return TryReadVirtualShadowResidentReverseDepth(parameters, pageTable, VirtualShadowResidentPages, atlas,
        firstMip, uv, storedDepth, resolvedMip);
#else
    uint mipCount = max(parameters.MipLevelCount, 1u);
    [loop]
    for (uint mip = clamp(firstMip, VirtualShadowFirstMip(parameters), mipCount - 1u); mip < mipCount; mip++)
    {
        uint resolution = VirtualShadowMipResolution(parameters, mip);
        uint2 pixel = min((uint2)(uv * float(resolution)), resolution - 1u);
        if (TryReadVirtualShadowMipReverseDepth(
                parameters, pageTable, atlas, mip, pixel, storedDepth))
        {
            resolvedMip = mip;
            return true;
        }
    }
    storedDepth = 0.0;
    resolvedMip = mipCount - 1u;
    return false;
#endif
}

bool TryReadVirtualShadowDepthAtOrCoarser(VirtualShadowMapParameters parameters,
    StructuredBuffer<uint> pageTable, Texture2DArray<uint> atlas, uint firstMip, float2 uv,
    out float storedDepth, out uint resolvedMip)
{
    float reverseDepth;
    bool resident = TryReadVirtualShadowReverseDepthAtOrCoarser(parameters, pageTable, atlas,
        firstMip, uv, reverseDepth, resolvedMip);
    storedDepth = resident ? 1.0 - reverseDepth : 0.0;
    return resident;
}

struct VirtualShadowFilterContext
{
    uint MipLevel;
    uint2 LocalPage;
    uint AtlasPage;
    bool Resident;
    bool WholePage;
};

VirtualShadowFilterContext BuildVirtualShadowFilterContext(
    VirtualShadowMapParameters parameters,
    StructuredBuffer<uint> pageTable,
    uint firstMip,
    float2 uv)
{
    VirtualShadowFilterContext context = (VirtualShadowFilterContext)0;
#ifdef VSM_RESIDENT_LOOKUP_ENABLED
    uint resolvedMip, atlasPage;
    bool whole;
    if (TryResolveVirtualShadowResidentPixel(parameters, pageTable, VirtualShadowResidentPages,
            firstMip, uv, resolvedMip, atlasPage, whole))
    {
        uint resolution = VirtualShadowMipResolution(parameters, resolvedMip);
        uint2 pixel = min(uint2(uv * float(resolution)), resolution - 1u);
        context.MipLevel = resolvedMip;
        context.LocalPage = pixel / parameters.PageSize;
        context.AtlasPage = atlasPage;
        context.WholePage = whole;
        context.Resident = true;
    }
    return context;
#else
    uint mipCount = max(parameters.MipLevelCount, 1u);
    [loop]
    for (uint mip = clamp(firstMip, VirtualShadowFirstMip(parameters), mipCount - 1u); mip < mipCount; mip++)
    {
        uint resolution = VirtualShadowMipResolution(parameters, mip);
        uint2 pixel = min((uint2)(uv * float(resolution)), resolution - 1u);
        uint2 localPage = pixel >> firstbithigh(parameters.PageSize);
        uint atlasPage;
        bool wholePage;
        if (TryResolveVirtualShadowPixel(
                parameters, pageTable, mip, pixel, atlasPage, wholePage))
        {
            context.WholePage = wholePage;
            context.MipLevel = mip;
            context.LocalPage = localPage;
            context.AtlasPage = atlasPage;
            context.Resident = true;
            return context;
        }
    }
    context.MipLevel = mipCount - 1u;
    return context;
#endif
}

bool TryReadVirtualShadowDepthCached(
    VirtualShadowMapParameters parameters,
    StructuredBuffer<uint> pageTable,
    Texture2DArray<uint> atlas,
    VirtualShadowFilterContext context,
    float2 uv,
    out float storedDepth,
    out float2 texelCenter)
{
    uint resolution = VirtualShadowMipResolution(parameters, context.MipLevel);
    uint2 pixel = min((uint2)(uv * float(resolution)), resolution - 1u);
    texelCenter = (float2(pixel) + 0.5) / float(resolution);
    uint2 localPage = pixel >> firstbithigh(parameters.PageSize);
    if (context.Resident && context.WholePage && all(localPage == context.LocalPage))
    {
        storedDepth = ReadVirtualShadowDepth(
            parameters,
            atlas,
            context.AtlasPage,
            pixel & (parameters.PageSize - 1u));
        return true;
    }
    uint resolvedMip;
    bool resident = TryReadVirtualShadowDepthAtOrCoarser(
        parameters,
        pageTable,
        atlas,
        context.MipLevel,
        uv,
        storedDepth,
        resolvedMip);
    resolution = VirtualShadowMipResolution(parameters, resolvedMip);
    pixel = min((uint2)(uv * float(resolution)), resolution - 1u);
    texelCenter = (float2(pixel) + 0.5) / float(resolution);
    return resident;
}

bool TryReadVirtualShadowDepthCached(
    VirtualShadowMapParameters parameters,
    StructuredBuffer<uint> pageTable,
    Texture2DArray<uint> atlas,
    VirtualShadowFilterContext context,
    float2 uv,
    out float storedDepth)
{
    float2 texelCenter;
    return TryReadVirtualShadowDepthCached(parameters, pageTable, atlas,
        context, uv, storedDepth, texelCenter);
}

bool TryReadVirtualShadowFilterDepth(
    VirtualShadowMapParameters parameters,
    StructuredBuffer<uint> pageTable,
    Texture2DArray<uint> atlas,
    VirtualShadowFilterContext context,
    uint2 pixel,
    uint resolution,
    bool insidePage,
    uint2 atlasOrigin,
    out float storedDepth,
    out float2 texelCenter)
{
    if (insidePage)
    {
        uint2 atlasPixel = atlasOrigin + (pixel & (parameters.PageSize - 1u));
        storedDepth = 1.0 - asfloat(atlas.Load(int4(atlasPixel, 0, 0)));
        texelCenter = (float2(pixel) + 0.5) / float(resolution);
        return true;
    }
    return TryReadVirtualShadowDepthCached(
        parameters, pageTable, atlas, context,
        (float2(pixel) + 0.5) / float(resolution), storedDepth, texelCenter);
}

bool TryReadVirtualShadowFilterDepth(
    VirtualShadowMapParameters parameters,
    StructuredBuffer<uint> pageTable,
    Texture2DArray<uint> atlas,
    VirtualShadowFilterContext context,
    uint2 pixel,
    uint resolution,
    bool insidePage,
    uint2 atlasOrigin,
    out float storedDepth)
{
    float2 texelCenter;
    return TryReadVirtualShadowFilterDepth(parameters, pageTable, atlas, context,
        pixel, resolution, insidePage, atlasOrigin, storedDepth, texelCenter);
}

float2 CalculateVirtualShadowReceiverDepthGradient(
    VirtualShadowMapParameters parameters,
    float3 worldPosition,
    float3 worldPositionDx,
    float3 worldPositionDy)
{
    float4 clip = ProjectVirtualShadowClip(parameters, worldPosition);
    float4 clipDx = ProjectVirtualShadowClip(parameters, worldPositionDx);
    float4 clipDy = ProjectVirtualShadowClip(parameters, worldPositionDy);
    if (min(clipDx.w, clipDy.w) <= 1e-8) return 0.0.xx;
    float3 ndc = clip.xyz / clip.w;
    float3 ndcDx = clipDx.xyz / clipDx.w - ndc;
    float3 ndcDy = clipDy.xyz / clipDy.w - ndc;
    float2 uvDx = ndcDx.xy * float2(0.5, -0.5);
    float2 uvDy = ndcDy.xy * float2(0.5, -0.5);
    float determinant = uvDx.x * uvDy.y - uvDx.y * uvDy.x;
    if (abs(determinant) < 1e-15) return 0.0.xx;
    return float2(ndcDx.z * uvDy.y - ndcDy.z * uvDx.y,
        uvDx.x * ndcDy.z - uvDy.x * ndcDx.z) / determinant;
}

float2 CalculateVirtualShadowReceiverPlaneDepthGradient(
    VirtualShadowMapParameters parameters, float3 worldPosition, float3 normal)
{
    float3 viewPosition = worldPosition + parameters.WorldTranslation;
    float4 normalPlane = float4(normal, -dot(normal, viewPosition));
    float4 uvPlane = mul(normalPlane, parameters.ShadowUvNormal);
    return abs(uvPlane.z) > 1e-15 ? -uvPlane.xy / uvPlane.z : 0.0.xx;
}
float VirtualShadowReceiverDepthAtTexel(
    float depth, float2 uv, float2 depthGradient, uint2 pixel, uint resolution)
{
    // The atlas stores depth at its texel center. Evaluate the receiver plane
    // there as well, so a sloped surface compares its own matching depth.
    return depth + dot((float2(pixel) + 0.5) / float(resolution) - uv, depthGradient);
}

bool TryFilterVirtualShadowPage(
    VirtualShadowMapParameters parameters,
    StructuredBuffer<uint> pageTable,
    Texture2DArray<uint> atlas,
    float3 ndc,
    float2 uv,
    VirtualShadowFilterContext context,
    uint blockerSampleCount,
    uint filterSampleCount,
    float blockerSearchRadiusTexels,
    float maxFilterRadiusTexels,
    out float visibility,
    float2 receiverDepthGradient = 0.0.xx)
{
    visibility = 1.0;
    if (!context.Resident) return false;
    if (maxFilterRadiusTexels <= 0.0)
    {
        float storedDepth;
        float2 texelCenter;
        if (!TryReadVirtualShadowDepthCached(parameters, pageTable, atlas, context,
                uv, storedDepth, texelCenter)) return false;
        float slopeBias = 2.0 * max(0.0, -dot(texelCenter - uv, receiverDepthGradient));
        float depthScale = length(float3(parameters.LightViewProjection[0][2],
            parameters.LightViewProjection[1][2], parameters.LightViewProjection[2][2]));
        slopeBias = min(slopeBias, 100.0 * parameters.WorldUnitsPerCentimeter * depthScale);
        visibility = storedDepth >= 1.0 || ndc.z <= storedDepth + slopeBias + parameters.DepthBias ? 1.0 : 0.0;
        return true;
    }
    uint filterMip = context.MipLevel;
    uint filterResolution = VirtualShadowMipResolution(parameters, filterMip);
    float2 virtualPosition = uv * float(filterResolution);
    float radius = max(blockerSearchRadiusTexels, maxFilterRadiusTexels);
    uint2 pageOrigin = context.LocalPage * parameters.PageSize;
    bool insidePage = context.Resident && context.WholePage &&
        all(virtualPosition - radius >= float2(pageOrigin)) &&
        all(virtualPosition + radius < float2(pageOrigin + parameters.PageSize));
    uint2 atlasOrigin = uint2(context.AtlasPage & (parameters.AtlasPagesPerRow - 1u),
        context.AtlasPage >> firstbithigh(parameters.AtlasPagesPerRow)) * parameters.PageSize;
    float angle = frac(sin(dot(
        floor(virtualPosition),
        float2(12.9898, 78.233))) * 43758.5453) * 6.2831853;
    float sine;
    float cosine;
    sincos(angle, sine, cosine);
    uint blockerCount = 0u;
    float blockerDepthSum = 0.0;
    uint searchCount = min(blockerSampleCount, 16u);
    for (uint index = 0u; index < searchCount; index++)
    {
        float2 offset = RotateVirtualShadowDiskSample(
            VirtualShadowDiskSample(index, searchCount), sine, cosine) * blockerSearchRadiusTexels;
        uint2 samplePixel = (uint2)clamp(
            floor(virtualPosition + offset),
            0.0,
            float(filterResolution - 1u));
        float storedDepth;
        float2 sampleCenter;
        bool resident = TryReadVirtualShadowFilterDepth(
            parameters, pageTable, atlas, context, samplePixel, filterResolution,
            insidePage, atlasOrigin, storedDepth, sampleCenter);
        // Missing or unfinished depth is not an invented blocker. Retry the
        // complete footprint at a coarser mip/clipmap, or report invalidity.
        if (!resident) return false;
        if (storedDepth < 1.0 && ndc.z + dot(sampleCenter - uv, receiverDepthGradient) >
            storedDepth + parameters.DepthBias)
        {
            blockerCount++;
            blockerDepthSum += storedDepth;
        }
    }
    if (blockerCount == 0u) return true;

    float averageBlockerDepth = blockerDepthSum / float(blockerCount);
    float separation = saturate(
        (ndc.z - averageBlockerDepth) / max(averageBlockerDepth, 1e-4));
    // Searching for blockers does not set the minimum penumbra width.
    float filterRadius = min(
        maxFilterRadiusTexels,
        max(1.0, separation * maxFilterRadiusTexels));
    float visibleSamples = 0.0;
    uint sampleCount = min(filterSampleCount, 16u);
    for (uint index = 0u; index < sampleCount; index++)
    {
        float2 offset = RotateVirtualShadowDiskSample(
            VirtualShadowDiskSample(index, sampleCount), sine, cosine) * filterRadius;
        uint2 samplePixel = (uint2)clamp(
            floor(virtualPosition + offset),
            0.0,
            float(filterResolution - 1u));
        float storedDepth;
        float2 sampleCenter;
        if (!TryReadVirtualShadowFilterDepth(
                parameters, pageTable, atlas, context, samplePixel, filterResolution,
                insidePage, atlasOrigin, storedDepth, sampleCenter)) return false;
        visibleSamples += storedDepth >= 1.0 || ndc.z + dot(sampleCenter - uv, receiverDepthGradient) <=
            storedDepth + parameters.DepthBias ? 1.0 : 0.0;
    }
    visibility = visibleSamples / float(max(sampleCount, 1u));
    return true;
}

bool TrySampleVirtualShadowMapFilteredProjected(
    VirtualShadowMapParameters parameters,
    StructuredBuffer<uint> pageTable,
    Texture2DArray<uint> atlas,
    float3 ndc,
    float2 uv,
    uint firstMip,
    uint blockerSampleCount,
    uint filterSampleCount,
    float blockerSearchRadiusTexels,
    float maxFilterRadiusTexels,
    out float visibility,
    float2 receiverDepthGradient = 0.0.xx)
{
    visibility = 1.0;
    uint mipCount = max(parameters.MipLevelCount, 1u);
    uint mip = clamp(firstMip, VirtualShadowFirstMip(parameters), mipCount - 1u);
    while (mip < mipCount)
    {
        VirtualShadowFilterContext context = BuildVirtualShadowFilterContext(parameters, pageTable, mip, uv);
        if (!context.Resident) return false;
        if (TryFilterVirtualShadowPage(parameters, pageTable, atlas, ndc, uv, context,
                blockerSampleCount, filterSampleCount, blockerSearchRadiusTexels, maxFilterRadiusTexels,
                visibility, receiverDepthGradient)) return true;
        mip = context.MipLevel + 1u;
    }
    visibility = 1.0;
    return false;
}

float FilterVirtualShadowPage(
    VirtualShadowMapParameters parameters, StructuredBuffer<uint> pageTable, Texture2DArray<uint> atlas,
    float3 ndc, float2 uv, VirtualShadowFilterContext context,
    uint blockerSampleCount, uint filterSampleCount, float blockerSearchRadiusTexels,
    float maxFilterRadiusTexels, float2 receiverDepthGradient = 0.0.xx)
{
    float visibility;
    TrySampleVirtualShadowMapFilteredProjected(parameters, pageTable, atlas, ndc, uv, context.MipLevel,
        blockerSampleCount, filterSampleCount, blockerSearchRadiusTexels, maxFilterRadiusTexels,
        visibility, receiverDepthGradient);
    return visibility;
}

float SampleVirtualShadowMapFilteredProjected(
    VirtualShadowMapParameters parameters, StructuredBuffer<uint> pageTable, Texture2DArray<uint> atlas,
    float3 ndc, float2 uv, uint firstMip,
    uint blockerSampleCount, uint filterSampleCount, float blockerSearchRadiusTexels,
    float maxFilterRadiusTexels, float2 receiverDepthGradient = 0.0.xx)
{
    float visibility;
    TrySampleVirtualShadowMapFilteredProjected(parameters, pageTable, atlas, ndc, uv, firstMip,
        blockerSampleCount, filterSampleCount, blockerSearchRadiusTexels, maxFilterRadiusTexels,
        visibility, receiverDepthGradient);
    return visibility;
}

float SampleVirtualShadowMapFiltered(
    VirtualShadowMapParameters parameters,
    StructuredBuffer<uint> pageTable,
    Texture2DArray<uint> atlas,
    float3 worldPosition,
    uint firstMip,
    uint blockerSampleCount,
    uint filterSampleCount,
    float blockerSearchRadiusTexels,
    float maxFilterRadiusTexels,
    float2 receiverDepthGradient = 0.0.xx)
{
    float3 ndc;
    float2 uv;
    if (!ProjectVirtualShadowPosition(parameters, worldPosition, ndc, uv))
        return 1.0;
    return SampleVirtualShadowMapFilteredProjected(
        parameters,
        pageTable,
        atlas,
        ndc,
        uv,
        firstMip,
        blockerSampleCount,
        filterSampleCount,
        blockerSearchRadiusTexels,
        maxFilterRadiusTexels,
        receiverDepthGradient);
}

__exported import vsm_resident_math;
__exported import vsm_prefilter_sampling;
#ifdef VSM_PREFILTERED_LOOKUP_ENABLED
Texture2D<float> VirtualShadowPrefilteredAtlas;
#endif

VirtualShadowDepthSample ReadVirtualShadowDirectionalResidentSample(StructuredBuffer<VirtualShadowMapParameters> views,
    StructuredBuffer<uint> pageTable, StructuredBuffer<uint> residentPages, Texture2DArray<uint> atlas,
    uint requestedViewIndex, float2 uv)
{
    VirtualShadowDepthSample sample = (VirtualShadowDepthSample)0;
    sample.ViewIndex = requestedViewIndex;
    VirtualShadowMapParameters requested = views[requestedViewIndex];
    uint2 basePage = min(uint2(uv * float(VirtualShadowPagesPerRow(requested))), VirtualShadowPagesPerRow(requested) - 1u);
    uint remaining = requested.ClipmapLevelCount - uint(requested.ClipmapLevel - requested.FirstClipmapLevel);
    uint offset = 0u;
    while (offset < remaining)
    {
        VirtualShadowMapParameters current = views[requestedViewIndex + offset];
        int2 localPage = offset == 0u ? int2(basePage) : VirtualShadowCoarseClipmapPage(basePage, requested, current, offset);
        if (any(localPage < 0) || any(uint2(localPage) >= VirtualShadowPagesPerRow(current))) break;
        uint reference = pageTable[VirtualShadowPageTableIndex(current, 0u, uint2(localPage))];
        if ((reference & VSM_PAGE_ANY_DEPTH) == 0u) { offset++; continue; }
        uint sourceOffset = offset + VirtualShadowPageLevelOffset(reference);
        VirtualShadowMapParameters source = views[requestedViewIndex + sourceOffset];
        int2 sourcePage = sourceOffset == 0u ? int2(basePage) : VirtualShadowCoarseClipmapPage(basePage, requested, source, sourceOffset);
        float4 transform = VirtualShadowClipmapScaleBias(requested, source, sourceOffset);
        float2 sourceUV = uv * transform.x + transform.yz;
        uint2 pixel = clamp(uint2(max(sourceUV, 0.0.xx) * float(source.VirtualResolution)),
            uint2(sourcePage) * source.PageSize, (uint2(sourcePage) + 1u) * source.PageSize - 1u);
        float depth;
        if (TryReadVirtualShadowMipReverseDepth(source, pageTable, atlas, 0u, pixel, depth))
        {
            sample.Valid = true;
            sample.Depth = depth;
            sample.ViewIndex = requestedViewIndex + sourceOffset;
            sample.UV = sourceUV;
            sample.TexelCenter = (float2(pixel) + 0.5) / float(source.VirtualResolution);
            return sample;
        }
        offset = sourceOffset + 1u;
    }
    return sample;
}

bool TryReadVirtualShadowDirectionalResidentSourceReverseDepth(StructuredBuffer<VirtualShadowMapParameters> views,
    StructuredBuffer<uint> pageTable, StructuredBuffer<uint> residentPages, Texture2DArray<uint> atlas,
    uint requestedViewIndex, float2 uv, out float storedDepth, out uint sourceViewIndex)
{
    VirtualShadowDepthSample sample = ReadVirtualShadowDirectionalResidentSample(views, pageTable,
        residentPages, atlas, requestedViewIndex, uv);
    storedDepth = sample.Depth;
    sourceViewIndex = sample.ViewIndex;
    return sample.Valid;
}

bool TryReadVirtualShadowDirectionalResidentReverseDepth(StructuredBuffer<VirtualShadowMapParameters> views,
    StructuredBuffer<uint> pageTable, StructuredBuffer<uint> residentPages, Texture2DArray<uint> atlas,
    uint requestedViewIndex, float2 uv, out float storedDepth, out uint sourceViewIndex)
{
    bool resident = TryReadVirtualShadowDirectionalResidentSourceReverseDepth(views, pageTable,
        residentPages, atlas, requestedViewIndex, uv, storedDepth, sourceViewIndex);
    if (resident && sourceViewIndex != requestedViewIndex)
    {
        float4 transform = VirtualShadowClipmapScaleBias(views[requestedViewIndex], views[sourceViewIndex],
            sourceViewIndex - requestedViewIndex);
        storedDepth = VirtualShadowReverseDepthInRequestedClipmap(storedDepth, transform);
    }
    return resident;
}

bool TryReadVirtualShadowDirectionalResidentDepth(StructuredBuffer<VirtualShadowMapParameters> views,
    StructuredBuffer<uint> pageTable, StructuredBuffer<uint> residentPages, Texture2DArray<uint> atlas,
    uint requestedViewIndex, float2 uv, out float storedDepth, out uint sourceViewIndex)
{
    float reverseDepth;
    bool resident = TryReadVirtualShadowDirectionalResidentReverseDepth(views, pageTable,
        residentPages, atlas, requestedViewIndex, uv, reverseDepth, sourceViewIndex);
    storedDepth = resident ? 1.0 - reverseDepth : 0.0;
    return resident;
}

bool TrySampleDirectionalVirtualShadowMap(
    StructuredBuffer<VirtualShadowMapParameters> views,
    StructuredBuffer<uint> pageTable,
    Texture2DArray<uint> atlas,
    uint firstView,
    uint clipmapLevelCount,
    float dynamicResolutionLodBias,
    float3 worldPosition,
    float3 worldPositionDx,
    float3 worldPositionDy,
    uint blockerSampleCount,
    uint filterSampleCount,
    float blockerSearchRadiusTexels,
    float maxFilterRadiusTexels,
    float3 receiverPlaneNormal,
    out float visibility,
    float receiverResolutionLodBias = 0.0)
{
    visibility = 1.0;
    if (clipmapLevelCount == 0u)
        return true;
    uint firstLevel = SelectDirectionalVirtualShadowView(views, firstView, clipmapLevelCount,
        worldPosition, worldPositionDx, worldPositionDy, dynamicResolutionLodBias, receiverResolutionLodBias);
    if (firstLevel >= clipmapLevelCount)
        return true;

    float distantVisibility = 1.0;
#ifdef VSM_PREFILTERED_LOOKUP_ENABLED
    distantVisibility = SampleDirectionalVirtualShadowPrefiltered(views, pageTable, VirtualShadowPrefilteredAtlas,
        firstView, clipmapLevelCount, worldPosition).Visibility;
    if (distantVisibility <= 0.0001)
    {
        visibility = distantVisibility;
        return true;
    }
#endif

#ifdef VSM_RESIDENT_LOOKUP_ENABLED
    VirtualShadowMapParameters requested = views[firstView + firstLevel];
    float3 requestedNdc;
    float2 requestedUV;
    if (!ProjectVirtualShadowPosition(requested, worldPosition, requestedNdc, requestedUV)) return false;
    uint2 basePage = min(uint2(requestedUV * float(VirtualShadowPagesPerRow(requested))), VirtualShadowPagesPerRow(requested) - 1u);
    uint offset = 0u;
    while (firstLevel + offset < clipmapLevelCount)
    {
        VirtualShadowMapParameters current = views[firstView + firstLevel + offset];
        int2 localPage = offset == 0u ? int2(basePage) : VirtualShadowCoarseClipmapPage(basePage, requested, current, offset);
        if (any(localPage < 0) || any(uint2(localPage) >= VirtualShadowPagesPerRow(current))) break;
        uint reference = VirtualShadowResidentPages[VirtualShadowPageTableIndex(current, 0u, uint2(localPage))];
        if ((reference & VSM_PAGE_ANY_DEPTH) == 0u) { offset++; continue; }
        uint sourceOffset = offset + VirtualShadowPageLevelOffset(reference);
        VirtualShadowMapParameters source = views[firstView + firstLevel + sourceOffset];
        int2 sourcePage = sourceOffset == 0u ? int2(basePage) : VirtualShadowCoarseClipmapPage(basePage, requested, source, sourceOffset);
        float4 transform = VirtualShadowClipmapScaleBias(requested, source, sourceOffset);
        float2 sourceUV = requestedUV * transform.x + transform.yz;
        float2 minimumPixel = float2(sourcePage) * source.PageSize;
        float2 maximumPixel = minimumPixel + source.PageSize - 1e-4;
        float2 lookupUV = clamp(sourceUV * float(source.VirtualResolution), minimumPixel, maximumPixel) / float(source.VirtualResolution);
        float3 sourceNdc = float3(sourceUV * float2(2.0, -2.0) + float2(-1.0, 1.0), requestedNdc.z * transform.x + transform.w);
        if (TrySampleVirtualShadowMapFilteredProjected(source, pageTable, atlas, sourceNdc, lookupUV, 0u,
                blockerSampleCount, filterSampleCount, blockerSearchRadiusTexels, maxFilterRadiusTexels,
                visibility, CalculateVirtualShadowReceiverPlaneDepthGradient(source, worldPosition, receiverPlaneNormal)))
        {
            visibility *= distantVisibility;
            return true;
        }
        offset = sourceOffset + 1u;
    }
#else
    for (uint level = firstLevel; level < clipmapLevelCount; level++)
    {
        float3 ndc;
        float2 uv;
        if (ProjectVirtualShadowPosition(
                views[firstView + level], worldPosition, ndc, uv))
        {
            if (TrySampleVirtualShadowMapFilteredProjected(
                    views[firstView + level], pageTable, atlas, ndc, uv, 0u,
                    blockerSampleCount, filterSampleCount, blockerSearchRadiusTexels, maxFilterRadiusTexels,
                    visibility, CalculateVirtualShadowReceiverPlaneDepthGradient(
                        views[firstView + level], worldPosition, receiverPlaneNormal)))
            {
                visibility *= distantVisibility;
                return true;
            }
        }
    }
#endif
    visibility = 1.0;
    return false;
}

float SampleDirectionalVirtualShadowMap(
    StructuredBuffer<VirtualShadowMapParameters> views,
    StructuredBuffer<uint> pageTable,
    Texture2DArray<uint> atlas,
    uint firstView,
    uint clipmapLevelCount,
    float dynamicResolutionLodBias,
    float3 worldPosition,
    float3 worldPositionDx,
    float3 worldPositionDy,
    uint blockerSampleCount,
    uint filterSampleCount,
    float blockerSearchRadiusTexels,
    float maxFilterRadiusTexels)
{
    return SampleDirectionalVirtualShadowMap(views, pageTable, atlas,
        firstView, clipmapLevelCount, dynamicResolutionLodBias,
        worldPosition, worldPositionDx, worldPositionDy, blockerSampleCount, filterSampleCount,
        blockerSearchRadiusTexels, maxFilterRadiusTexels,
        cross(worldPositionDx - worldPosition, worldPositionDy - worldPosition));
}

uint VirtualShadowPointFace(float3 lightToReceiver)
{
    float3 magnitude = abs(lightToReceiver);
    if (magnitude.x >= magnitude.y && magnitude.x >= magnitude.z)
        return lightToReceiver.x >= 0.0 ? 0u : 1u;
    if (magnitude.y >= magnitude.z)
        return lightToReceiver.y >= 0.0 ? 2u : 3u;
    return lightToReceiver.z >= 0.0 ? 4u : 5u;
}

bool TrySamplePointVirtualShadowMap(
    StructuredBuffer<VirtualShadowMapParameters> views,
    StructuredBuffer<uint> pageTable,
    Texture2DArray<uint> atlas,
    uint firstView,
    float dynamicResolutionLodBias, float nominalWorldPixelRadius,
    float3 worldPosition,
    float3 worldPositionDx,
    float3 worldPositionDy,
    uint blockerSampleCount,
    uint filterSampleCount,
    float blockerSearchRadiusTexels,
    float maxFilterRadiusTexels,
    float3 receiverPlaneNormal,
    out float visibility)
{
    visibility = 1.0;
    VirtualShadowMapParameters baseView = views[firstView];
    uint faceIndex = VirtualShadowPointFace(worldPosition - baseView.LightPosition);
    VirtualShadowMapParameters view = views[firstView + faceIndex];
    uint mipLevel = VirtualShadowLocalSamplingMip(
        view,
        worldPosition,
        nominalWorldPixelRadius,
        dynamicResolutionLodBias);
    float3 ndc;
    float2 uv;
    if (!ProjectVirtualShadowPosition(view, worldPosition, ndc, uv)) return true;
    return TrySampleVirtualShadowMapFilteredProjected(
        view, pageTable, atlas, ndc, uv, mipLevel,
        blockerSampleCount, filterSampleCount, blockerSearchRadiusTexels, maxFilterRadiusTexels,
        visibility, CalculateVirtualShadowReceiverPlaneDepthGradient(view, worldPosition, receiverPlaneNormal));
}

float SamplePointVirtualShadowMap(
    StructuredBuffer<VirtualShadowMapParameters> views,
    StructuredBuffer<uint> pageTable,
    Texture2DArray<uint> atlas,
    uint firstView,
    float dynamicResolutionLodBias, float nominalWorldPixelRadius,
    float3 worldPosition,
    float3 worldPositionDx,
    float3 worldPositionDy,
    uint blockerSampleCount,
    uint filterSampleCount,
    float blockerSearchRadiusTexels,
    float maxFilterRadiusTexels)
{
    return SamplePointVirtualShadowMap(views, pageTable, atlas,
        firstView, dynamicResolutionLodBias, nominalWorldPixelRadius,
        worldPosition, worldPositionDx, worldPositionDy, blockerSampleCount, filterSampleCount,
        blockerSearchRadiusTexels, maxFilterRadiusTexels,
        cross(worldPositionDx - worldPosition, worldPositionDy - worldPosition));
}

bool TrySampleSpotVirtualShadowMap(
    StructuredBuffer<VirtualShadowMapParameters> views,
    StructuredBuffer<uint> pageTable,
    Texture2DArray<uint> atlas,
    uint viewIndex,
    float dynamicResolutionLodBias, float nominalWorldPixelRadius,
    float3 worldPosition,
    float3 worldPositionDx,
    float3 worldPositionDy,
    uint blockerSampleCount,
    uint filterSampleCount,
    float blockerSearchRadiusTexels,
    float maxFilterRadiusTexels,
    float3 receiverPlaneNormal,
    out float visibility)
{
    visibility = 1.0;
    VirtualShadowMapParameters view = views[viewIndex];
    uint mipLevel = VirtualShadowLocalSamplingMip(
        view,
        worldPosition,
        nominalWorldPixelRadius,
        dynamicResolutionLodBias);
    float3 ndc;
    float2 uv;
    if (!ProjectVirtualShadowPosition(view, worldPosition, ndc, uv)) return true;
    return TrySampleVirtualShadowMapFilteredProjected(
        view, pageTable, atlas, ndc, uv, mipLevel,
        blockerSampleCount, filterSampleCount, blockerSearchRadiusTexels, maxFilterRadiusTexels,
        visibility, CalculateVirtualShadowReceiverPlaneDepthGradient(view, worldPosition, receiverPlaneNormal));
}

float SampleSpotVirtualShadowMap(
    StructuredBuffer<VirtualShadowMapParameters> views,
    StructuredBuffer<uint> pageTable,
    Texture2DArray<uint> atlas,
    uint viewIndex,
    float dynamicResolutionLodBias, float nominalWorldPixelRadius,
    float3 worldPosition,
    float3 worldPositionDx,
    float3 worldPositionDy,
    uint blockerSampleCount,
    uint filterSampleCount,
    float blockerSearchRadiusTexels,
    float maxFilterRadiusTexels)
{
    return SampleSpotVirtualShadowMap(views, pageTable, atlas,
        viewIndex, dynamicResolutionLodBias, nominalWorldPixelRadius,
        worldPosition, worldPositionDx, worldPositionDy, blockerSampleCount, filterSampleCount,
        blockerSearchRadiusTexels, maxFilterRadiusTexels,
        cross(worldPositionDx - worldPosition, worldPositionDy - worldPosition));
}

// Float factors expose visibility. Callers that need to
// distinguish missing/unfinished depth use TrySample* and retain its boolean.
float SampleDirectionalVirtualShadowMap(
    StructuredBuffer<VirtualShadowMapParameters> views, StructuredBuffer<uint> pageTable, Texture2DArray<uint> atlas,
    uint firstView, uint clipmapLevelCount, float dynamicResolutionLodBias,
    float3 worldPosition, float3 worldPositionDx, float3 worldPositionDy,
    uint blockerSampleCount, uint filterSampleCount, float blockerSearchRadiusTexels,
    float maxFilterRadiusTexels, float3 receiverPlaneNormal, float receiverResolutionLodBias = 0.0)
{
    float visibility;
    TrySampleDirectionalVirtualShadowMap(views, pageTable, atlas, firstView, clipmapLevelCount,
        dynamicResolutionLodBias, worldPosition, worldPositionDx, worldPositionDy,
        blockerSampleCount, filterSampleCount, blockerSearchRadiusTexels, maxFilterRadiusTexels,
        receiverPlaneNormal, visibility, receiverResolutionLodBias);
    return visibility;
}

float SamplePointVirtualShadowMap(
    StructuredBuffer<VirtualShadowMapParameters> views, StructuredBuffer<uint> pageTable, Texture2DArray<uint> atlas,
    uint firstView, float dynamicResolutionLodBias, float nominalWorldPixelRadius,
    float3 worldPosition, float3 worldPositionDx, float3 worldPositionDy,
    uint blockerSampleCount, uint filterSampleCount, float blockerSearchRadiusTexels,
    float maxFilterRadiusTexels, float3 receiverPlaneNormal)
{
    float visibility;
    TrySamplePointVirtualShadowMap(views, pageTable, atlas, firstView, dynamicResolutionLodBias, nominalWorldPixelRadius,
        worldPosition, worldPositionDx, worldPositionDy, blockerSampleCount, filterSampleCount,
        blockerSearchRadiusTexels, maxFilterRadiusTexels, receiverPlaneNormal, visibility);
    return visibility;
}

float SampleSpotVirtualShadowMap(
    StructuredBuffer<VirtualShadowMapParameters> views, StructuredBuffer<uint> pageTable, Texture2DArray<uint> atlas,
    uint viewIndex, float dynamicResolutionLodBias, float nominalWorldPixelRadius,
    float3 worldPosition, float3 worldPositionDx, float3 worldPositionDy,
    uint blockerSampleCount, uint filterSampleCount, float blockerSearchRadiusTexels,
    float maxFilterRadiusTexels, float3 receiverPlaneNormal)
{
    float visibility;
    TrySampleSpotVirtualShadowMap(views, pageTable, atlas, viewIndex, dynamicResolutionLodBias, nominalWorldPixelRadius,
        worldPosition, worldPositionDx, worldPositionDy, blockerSampleCount, filterSampleCount,
        blockerSearchRadiusTexels, maxFilterRadiusTexels, receiverPlaneNormal, visibility);
    return visibility;
}

#endif
