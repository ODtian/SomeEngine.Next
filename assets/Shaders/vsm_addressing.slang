__exported import vsm_types;

StructuredBuffer<uint4> ShadowRanges;

uint VirtualShadowPagesPerRow(VirtualShadowMapParameters parameters)
{
    return parameters.VirtualResolution >> firstbithigh(parameters.PageSize);
}

uint VirtualShadowMipPagesPerRow(
    VirtualShadowMapParameters parameters,
    uint mipLevel)
{
    return max(VirtualShadowPagesPerRow(parameters) >> mipLevel, 1u);
}



uint VirtualShadowMipResolution(
    VirtualShadowMapParameters parameters,
    uint mipLevel)
{
    return max(parameters.VirtualResolution >> mipLevel, parameters.PageSize);
}

uint VirtualShadowFirstMip(VirtualShadowMapParameters parameters)
{
    return (parameters.Flags & VSM_VIEW_SINGLE_PAGE) != 0u
        ? parameters.MipLevelCount - 1u : parameters.MinMipLevel;
}

uint VirtualShadowMipOffset(
    VirtualShadowMapParameters parameters,
    uint mipLevel)
{
    uint dimension = VirtualShadowPagesPerRow(parameters);
    uint shrinkingLevels = min(mipLevel, (uint)firstbithigh(dimension));
    uint lastDimension = dimension >> shrinkingLevels;
    // Page dimensions are powers of two. Sum the area series without walking
    // earlier mips for every lookup; any remaining levels contain one page.
    return ((dimension * dimension - lastDimension * lastDimension) / 3u) * 4u +
        mipLevel - shrinkingLevels;
}

uint VirtualShadowPageTableEntriesPerView(VirtualShadowMapParameters parameters)
{
    return (parameters.Flags & VSM_VIEW_SINGLE_PAGE) != 0u ? 1u :
        VirtualShadowMipOffset(parameters, parameters.MipLevelCount);
}

uint VirtualShadowPageHierarchyMipWordCount(
    VirtualShadowMapParameters parameters,
    uint mipLevel)
{
    uint dimension = VirtualShadowMipPagesPerRow(parameters, mipLevel);
    uint result = 0u;
    [loop]
    while (true)
    {
        result += dimension * ((dimension + 31u) / 32u);
        if (dimension == 1u)
            return result;
        dimension >>= 1u;
    }
}

uint VirtualShadowPageHierarchyMipOffset(
    VirtualShadowMapParameters parameters,
    uint mipLevel)
{
    uint result = 0u;
    [loop]
    for (uint mip = 0u; mip < mipLevel; mip++)
        result += VirtualShadowPageHierarchyMipWordCount(parameters, mip);
    return result;
}

uint VirtualShadowPageHierarchyLevelOffset(
    VirtualShadowMapParameters parameters,
    uint mipLevel,
    uint hierarchyLevel)
{
    uint result = VirtualShadowPageHierarchyMipOffset(parameters, mipLevel);
    uint dimension = VirtualShadowMipPagesPerRow(parameters, mipLevel);
    [loop]
    for (uint level = 0u; level < hierarchyLevel; level++)
    {
        result += dimension * ((dimension + 31u) / 32u);
        dimension = max(dimension >> 1u, 1u);
    }
    return result;
}

uint VirtualShadowPageHierarchyDimension(
    VirtualShadowMapParameters parameters,
    uint mipLevel,
    uint hierarchyLevel)
{
    return max(
        VirtualShadowMipPagesPerRow(parameters, mipLevel) >> hierarchyLevel,
        1u);
}

int2 VirtualShadowWorldPage(
    VirtualShadowMapParameters parameters,
    uint mipLevel,
    uint2 localPage)
{
    int2 origin = int2(parameters.VirtualPageOriginX, parameters.VirtualPageOriginY) >> mipLevel;
    return origin +
        int2(localPage);
}

uint2 VirtualShadowTableSlot(
    VirtualShadowMapParameters parameters,
    uint mipLevel,
    int2 worldPage)
{
    uint mask = VirtualShadowMipPagesPerRow(parameters, mipLevel) - 1u;
    return uint2(asuint(worldPage.x) & mask, asuint(worldPage.y) & mask);
}

uint VirtualShadowPageTableIndex(
    VirtualShadowMapParameters parameters,
    uint mipLevel,
    uint2 localPage)
{
    if ((parameters.Flags & VSM_VIEW_SINGLE_PAGE) != 0u) return parameters.PageTableOffset;
    int2 worldPage = VirtualShadowWorldPage(parameters, mipLevel, localPage);
    uint2 slot = VirtualShadowTableSlot(parameters, mipLevel, worldPage);
    uint dimension = VirtualShadowMipPagesPerRow(parameters, mipLevel);
    return parameters.PageTableOffset + VirtualShadowMipOffset(parameters, mipLevel) +
        slot.y * dimension + slot.x;
}

int VirtualShadowFloorDiv(int value, int divisor)
{
    // Page dimensions and coarse spans are powers of two. Signed shifting also
    // floors negative scrolling coordinates without a division and correction.
    return value >> firstbithigh(uint(divisor));
}

int VirtualShadowFloorMod(int value, int divisor)
{
    return value & (divisor - 1);
}

uint2 VirtualShadowMipPagePixel(
    VirtualShadowMapParameters parameters,
    uint mipLevel,
    uint2 virtualPixel)
{
    uint mipResolution = VirtualShadowMipResolution(parameters, mipLevel);
    uint2 mipPixel = min(virtualPixel >> mipLevel, mipResolution - 1u);
    return mipPixel & (parameters.PageSize - 1u);
}

uint2 VirtualShadowMipLocalPage(
    VirtualShadowMapParameters parameters,
    uint mipLevel,
    uint2 virtualPixel)
{
    uint mipResolution = VirtualShadowMipResolution(parameters, mipLevel);
    uint2 mipPixel = min(virtualPixel >> mipLevel, mipResolution - 1u);
    return mipPixel >> firstbithigh(parameters.PageSize);
}

uint VirtualShadowViewFromPageTableIndex(StructuredBuffer<VirtualShadowMapParameters> views,
    uint viewCount, uint tableIndex)
{
    uint rangeCount, rangeStride;
    ShadowRanges.GetDimensions(rangeCount, rangeStride);
    uint first = 1u;
    uint last = rangeCount;
    while (first < last)
    {
        uint middle = first + ((last - first) >> 1u);
        if (ShadowRanges[middle].x <= tableIndex) first = middle + 1u;
        else last = middle;
    }
    if (first == 1u) return viewCount;
    uint4 range = ShadowRanges[first - 1u];
    return tableIndex - range.x < range.y ? range.w : viewCount;
}

bool VirtualShadowDecodePageTableIndex(
    VirtualShadowMapParameters parameters,
    uint tableIndex,
    out uint mipLevel,
    out uint2 localPage)
{
    if (tableIndex < parameters.PageTableOffset)
    {
        mipLevel = 0u;
        localPage = 0u.xx;
        return false;
    }
    if ((parameters.Flags & VSM_VIEW_SINGLE_PAGE) != 0u)
    {
        mipLevel = parameters.MipLevelCount - 1u;
        localPage = 0u.xx;
        return tableIndex == parameters.PageTableOffset;
    }
    uint localIndex = tableIndex - parameters.PageTableOffset;
    [loop]
    for (uint level = 0u; level < parameters.MipLevelCount; level++)
    {
        uint dimension = VirtualShadowMipPagesPerRow(parameters, level);
        uint count = dimension * dimension;
        if (localIndex < count)
        {
            mipLevel = level;
            uint2 slot = uint2(localIndex % dimension, localIndex / dimension);
            uint mask = dimension - 1u;
            int2 origin = int2(
                parameters.VirtualPageOriginX,
                parameters.VirtualPageOriginY) >> level;
            uint2 originSlot = uint2(
                asuint(origin.x) & mask,
                asuint(origin.y) & mask);
            localPage = (slot - originSlot) & mask;
            return level >= VirtualShadowFirstMip(parameters);
        }
        localIndex -= count;
    }
    mipLevel = 0u;
    localPage = 0u.xx;
    return false;
}

bool IsVirtualShadowPageTag(uint entry, int2 worldPage)
{
    return VirtualShadowPageHasOwnDepth(entry);
}

bool ProjectVirtualShadowPosition(
    VirtualShadowMapParameters parameters,
    float3 worldPosition,
    out float3 ndc,
    out float2 uv)
{
    float4 clip = mul(float4(worldPosition + parameters.WorldTranslation, 1.0), parameters.ShadowUv);
    if (clip.w <= 0.0)
    {
        ndc = float3(0.0);
        uv = float2(0.0);
        return false;
    }
    float3 projected = clip.xyz / clip.w;
    uv = projected.xy;
    ndc = float3(uv * float2(2.0, -2.0) + float2(-1.0, 1.0), projected.z);
    return all(uv >= 0.0) && all(uv < 1.0) && ndc.z >= 0.0 && ndc.z <= 1.0;
}

bool ProjectVirtualShadowFootprintPosition(
    VirtualShadowMapParameters parameters,
    float3 worldPosition,
    out float2 uv)
{
    float4 clip = mul(float4(worldPosition + parameters.WorldTranslation, 1.0), parameters.ShadowUv);
    if (clip.w <= 1e-8)
    {
        uv = 0.0.xx;
        return false;
    }
    uv = clip.xy / clip.w;
    return true;
}

float ReadVirtualShadowReverseDepth(
    VirtualShadowMapParameters parameters,
    Texture2DArray<uint> atlas,
    uint atlasPage,
    uint2 pagePixel)
{
    uint2 atlasPageCoord = uint2(
        atlasPage & (parameters.AtlasPagesPerRow - 1u),
        atlasPage >> firstbithigh(parameters.AtlasPagesPerRow));
    uint2 atlasPixel = atlasPageCoord * parameters.PageSize + pagePixel;
    return asfloat(atlas.Load(int4(atlasPixel, 0, 0)));
}

float ReadVirtualShadowDepth(VirtualShadowMapParameters parameters,
    Texture2DArray<uint> atlas, uint atlasPage, uint2 pagePixel)
{
    return 1.0 - ReadVirtualShadowReverseDepth(parameters, atlas, atlasPage, pagePixel);
}

float2 VirtualShadowDiskSample(uint index, uint sampleCount)
{
    uint pairs = sampleCount >> 1u;
    if (pairs == 0u || index == pairs * 2u) return 0.0.xx;
    uint pair = index >> 1u;
    // Opposite samples share one equal-area radial stratum. The complete
    // requested count has zero first moment, including a final odd center.
    float radius = sqrt((float(pair) + 0.5) / float(pairs));
    float sine, cosine;
    sincos(float(pair) * 2.39996322973, sine, cosine);
    float2 sample = radius * float2(cosine, sine);
    return (index & 1u) == 0u ? sample : -sample;
}

float2 RotateVirtualShadowDiskSample(
    float2 value,
    float sine,
    float cosine)
{
    return float2(
        value.x * cosine - value.y * sine,
        value.x * sine + value.y * cosine);
}


float GetVirtualShadowPixelWidth(float2 pixelDx, float2 pixelDy)
{
    float squareX = dot(pixelDx, pixelDx);
    float squareY = dot(pixelDy, pixelDy);
    float crossTerm = dot(pixelDx, pixelDy);
    float difference = squareX - squareY;
    float largestSquare = 0.5 * (squareX + squareY +
        sqrt(max(difference * difference + 4.0 * crossTerm * crossTerm, 0.0)));
    // The determinant is the product of the two principal widths. Dividing
    // by the larger width preserves the smaller one under a skewed projection.
    float area = abs(pixelDx.x * pixelDy.y - pixelDx.y * pixelDy.x);
    return area * rsqrt(max(largestSquare, 1e-30));
}

uint VirtualShadowLocalMipFromNominalPixelRadius(
    VirtualShadowMapParameters parameters,
    float3 worldPosition,
    float nominalWorldPixelRadius,
    float dynamicResolutionLodBias)
{
    float3 ndc;
    float2 uv;
    if (!ProjectVirtualShadowPosition(parameters, worldPosition, ndc, uv))
    {
        return VirtualShadowFirstMip(parameters);
    }
    float projectionScale = length(float3(parameters.LightViewProjection[0][0],
        parameters.LightViewProjection[1][0], parameters.LightViewProjection[2][0]));
    float4 lightClip = ProjectVirtualShadowClip(parameters, worldPosition);
    float footprint = abs(nominalWorldPixelRadius * projectionScale / lightClip.w) *
        float(2u * parameters.VirtualResolution);
    float mipFloat = log2(footprint) +
        parameters.ResolutionLodBias + dynamicResolutionLodBias;
    uint mip = (uint)max(floor(mipFloat), 0.0);
    mip = clamp(
        mip,
        VirtualShadowFirstMip(parameters),
        max(parameters.MipLevelCount, 1u) - 1u);
    if ((parameters.Flags & VSM_VIEW_EVEN_MIP_LEVELS) != 0u)
        mip &= ~1u;
    else if ((parameters.Flags & VSM_VIEW_ODD_MIP_LEVELS) != 0u)
        mip |= 1u;
    return mip;
}

uint VirtualShadowLocalSamplingMip(
    VirtualShadowMapParameters parameters,
    float3 worldPosition,
    float nominalWorldPixelRadius,
    float dynamicResolutionLodBias)
{
    return VirtualShadowLocalMipFromNominalPixelRadius(
        parameters, worldPosition, nominalWorldPixelRadius, dynamicResolutionLodBias);
}

float CalculateVirtualShadowLocalSourceSupport(VirtualShadowMapParameters view, float3 position)
{
    float distance = length(position - view.LightPosition);
    float clipW = abs(ProjectVirtualShadowClip(view, position).w);
    float nearDistance = abs(view.ProjectionDepthOffset / view.ProjectionDepthScale)
        * distance / max(clipW, 1e-20);
    float sourceDistance = max(view.SourceRadius, nearDistance);
    return view.SourceRadius * max(distance - sourceDistance, 0.0) / max(sourceDistance, 1e-20);
}

float CalculateDirectionalVirtualShadowDistanceLevel(
    VirtualShadowMapParameters parameters,
    float3 worldPosition)
{
    float3 fromCamera = (worldPosition - parameters.ClipmapWorldOrigin) /
        parameters.WorldUnitsPerCentimeter;
    return 0.5 * log2(max(dot(fromCamera, fromCamera), 1e-12)) -
        float(parameters.FirstClipmapLevel);
}

float CalculateDirectionalVirtualShadowPixelLevel(
    VirtualShadowMapParameters parameters,
    float3 worldPosition,
    float3 worldPositionDx,
    float3 worldPositionDy)
{
    return CalculateDirectionalVirtualShadowDistanceLevel(parameters, worldPosition) +
        parameters.ResolutionLodBias;
}

uint VirtualShadowDirectionalLevelFromReceiverFootprint(
    VirtualShadowMapParameters parameters,
    float3 worldPosition,
    float3 worldPositionDx,
    float3 worldPositionDy,
    float dynamicResolutionLodBias)
{
    float level = CalculateDirectionalVirtualShadowPixelLevel(
        parameters, worldPosition, worldPositionDx, worldPositionDy);
    return (uint)max(floor(level + dynamicResolutionLodBias), 0.0);
}

uint CalculateDirectionalVirtualShadowBiasedLevel(
    StructuredBuffer<VirtualShadowMapParameters> views, uint firstView, uint viewCount,
    float3 worldPosition, float dynamicResolutionLodBias, float receiverResolutionLodBias)
{
    VirtualShadowMapParameters baseView = views[firstView];
    float level = CalculateDirectionalVirtualShadowDistanceLevel(baseView, worldPosition) +
        dynamicResolutionLodBias + receiverResolutionLodBias;
    uint viewIndex = (uint)max(level, 0.0);
    float resolutionBias = viewIndex < viewCount
        ? views[firstView + viewIndex].ResolutionLodBias : baseView.ResolutionLodBias;
    float minimumLevel = max(-float(baseView.FirstClipmapLevel), 0.0);
    return (uint)max(floor(level + resolutionBias), minimumLevel);
}

uint SelectDirectionalVirtualShadowView(
    StructuredBuffer<VirtualShadowMapParameters> views,
    uint firstView,
    uint viewCount,
    float3 worldPosition,
    float3 worldPositionDx,
    float3 worldPositionDy,
    float dynamicResolutionLodBias,
    float receiverResolutionLodBias = 0.0)
{
    VirtualShadowMapParameters baseView = views[firstView];
    float distanceLevel = CalculateDirectionalVirtualShadowDistanceLevel(baseView, worldPosition) + receiverResolutionLodBias;
    uint unbiasedLevel = (uint)max(distanceLevel, 0.0);
    float resolutionBias = unbiasedLevel < viewCount
        ? views[firstView + unbiasedLevel].ResolutionLodBias
        : baseView.ResolutionLodBias;
    float pixelLevel = distanceLevel + resolutionBias;
    uint firstLevel = (uint)max(floor(pixelLevel), 0.0);
    for (uint level = firstLevel; level < viewCount; level++)
    {
        float3 ndc;
        float2 uv;
        if (ProjectVirtualShadowPosition(views[firstView + level], worldPosition, ndc, uv))
            return level;
    }
    return viewCount;
}

