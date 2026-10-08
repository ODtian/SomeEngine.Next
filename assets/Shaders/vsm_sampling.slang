#ifndef VSM_SAMPLING_SLANG
#define VSM_SAMPLING_SLANG

struct VirtualShadowMapParameters
{
    float4x4 LightViewProjection;
    uint VirtualResolution;
    uint PageSize;
    uint AtlasSize;
    uint PhysicalPagesPerRow;
    uint MaxPhysicalPages;
    int VirtualPageOriginX;
    float DepthBias;
    uint PageTableOffset;
    uint CacheGeneration;
    int VirtualPageOriginY;
    int ClipmapLevel;
    uint LightIndex;
    float3 ClipmapWorldOrigin;
    float ResolutionLodBias;
    int FirstClipmapLevel;
    uint ClipmapLevelCount;
    uint MipLevelCount;
    uint MinMipLevel;
    uint Flags;
    uint LightKind;
    uint FaceIndex;
    float WorldUnitsPerCentimeter;
    float3 LightPosition;
    float LightRange;
    float GeometryLodScale;
    float MaxPixelsPerEdge;
    float MinPixelsPerEdgeHardware;
    float DicingRate;
    float DepthCenterZ;
    float DepthRadius;
    float2 DepthCullRange;
    float3 WorldOriginHigh;
    float OriginHighPadding;
    float3 WorldOriginLow;
    float OriginLowPadding;
    int ClipmapCornerRelativeOffsetX;
    int ClipmapCornerRelativeOffsetY;
    uint2 WorldPageOriginX;
    uint2 WorldPageOriginY;
    float PageDilationBorder;
    float AddressPadding;
    float3 LightDirection;
    float SourceRadius;
    float SourceAngleRadians;
    float ProjectionDepthScale;
    float ProjectionDepthOffset;
    float TexelDitherScale;
    float3 WorldTranslation;
    float TranslationPadding;
    float4x4 ShadowUvNormal;
    float4x4 ShadowUv;
};

// Positions passed to this file are offsets from the owning Game world's origin.
// Users with another high/low reference can translate before projecting or sampling.
float3 TranslateVirtualShadowWorldPosition(float3 position, float3 originHigh,
    float3 originLow, VirtualShadowMapParameters view)
{
    return position + ((originHigh - view.WorldOriginHigh) + (originLow - view.WorldOriginLow)) - view.WorldTranslation;
}

float4 ProjectVirtualShadowClip(VirtualShadowMapParameters view, float3 worldPosition)
{
    return mul(float4(worldPosition + view.WorldTranslation, 1.0), view.LightViewProjection);
}

static const uint VSM_LIGHT_DIRECTIONAL = 0u;
static const uint VSM_LIGHT_POINT = 1u;
static const uint VSM_LIGHT_SPOT = 2u;
static const uint VSM_VIEW_COARSE_CLIP_LEVEL = 1u << 0u;
static const uint VSM_VIEW_FORCE_CACHE_DYNAMIC_COARSE = 1u << 1u;
static const uint VSM_VIEW_USE_RECEIVER_MASK = 1u << 2u;
static const uint VSM_VIEW_PREVIOUS_PROJECTION_VALID = 1u << 3u;
static const uint VSM_VIEW_ORTHOGRAPHIC_RECEIVER = 1u << 4u;
static const uint VSM_VIEW_EVEN_MIP_LEVELS = 1u << 5u;
static const uint VSM_VIEW_ODD_MIP_LEVELS = 1u << 6u;
static const uint VSM_VIEW_SINGLE_PAGE = 1u << 7u;
static const uint VSM_VIEW_DISTANT_LIGHT = 1u << 8u;
static const uint VSM_VIEW_CACHED_DISTANT_LIGHT = 1u << 9u;
static const uint VSM_VIEW_PREFILTERED_DISTANT = 1u << 10u;
static const uint VSM_VIEW_FIRST_PERSON_SHADOW = 1u << 11u;
static const uint VSM_PAGE_REQUEST_DETAIL = 1u << 0u;
static const uint VSM_PAGE_REQUEST_COARSE = 1u << 1u;
static const uint VSM_PAGE_REQUEST_SECONDARY = 1u << 2u;

static const uint VSM_PAGE_UNMAPPED = 0u;
static const uint VSM_PAGE_LOCKED = 0xFFFFFFFFu;
// A completed masked page is usable only in its covered receiver cells this frame.
// It is not a whole-page cache entry or a source of prior-frame HiZ.
static const uint VSM_PAGE_RECEIVER_DEPTH = 0x80000000u;
static const uint VSM_PAGE_MASKED = 1u;
static const uint VSM_PAGE_PRIOR_HIZ = 2u;
#include "vsm_page_entry.slang"

#ifndef VSM_WHOLE_PAGE_SAMPLING
StructuredBuffer<uint2> VirtualShadowReceiverMasks;
#endif

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
    uint first = 0u;
    uint last = viewCount;
    while (first < last)
    {
        uint middle = first + ((last - first) >> 1u);
        if (views[middle].PageTableOffset <= tableIndex) first = middle + 1u;
        else last = middle;
    }
    return first == 0u ? viewCount : first - 1u;
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
        atlasPage & (parameters.PhysicalPagesPerRow - 1u),
        atlasPage >> firstbithigh(parameters.PhysicalPagesPerRow));
    uint2 atlasPixel = atlasPageCoord * parameters.PageSize + pagePixel;
    return asfloat(atlas.Load(int4(atlasPixel, 0, 0)));
}

float ReadVirtualShadowDepth(VirtualShadowMapParameters parameters,
    Texture2DArray<uint> atlas, uint atlasPage, uint2 pagePixel)
{
    return 1.0 - ReadVirtualShadowReverseDepth(parameters, atlas, atlasPage, pagePixel);
}

float2 VirtualShadowDiskSample(uint index)
{
    switch (index & 15u)
    {
        case 0u: return float2(-0.613392, 0.617481);
        case 1u: return float2(0.170019, -0.040254);
        case 2u: return float2(-0.299417, 0.791925);
        case 3u: return float2(0.645680, 0.493210);
        case 4u: return float2(-0.651784, -0.717887);
        case 5u: return float2(0.421003, 0.027070);
        case 6u: return float2(-0.817194, -0.271096);
        case 7u: return float2(-0.705374, -0.668203);
        case 8u: return float2(0.977050, -0.108615);
        case 9u: return float2(0.063326, 0.142369);
        case 10u: return float2(0.203528, 0.214331);
        case 11u: return float2(-0.667531, 0.326090);
        case 12u: return float2(-0.098422, -0.295755);
        case 13u: return float2(-0.885922, 0.215369);
        case 14u: return float2(0.566637, 0.605213);
        default: return float2(0.039766, -0.396100);
    }
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
        DecodeVirtualShadowPage(entry, parameters.PhysicalPagesPerRow, atlasPage);
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
        !DecodeVirtualShadowPage(entry, parameters.PhysicalPagesPerRow, atlasPage))
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
            DecodeVirtualShadowPage(entry, parameters.PhysicalPagesPerRow, atlasPage))
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
    if ((parameters.Flags & VSM_VIEW_USE_RECEIVER_MASK) == 0u)
        return 0u;
    return VirtualShadowLocalMipFromNominalPixelRadius(
        parameters, worldPosition, nominalWorldPixelRadius, dynamicResolutionLodBias);
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
    uint pressureLevels = (uint)max(
        floor(pixelLevel + dynamicResolutionLodBias) - floor(pixelLevel), 0.0);
    for (uint level = firstLevel; level < viewCount; level++)
    {
        float3 ndc;
        float2 uv;
        if (ProjectVirtualShadowPosition(views[firstView + level], worldPosition, ndc, uv))
            // Pool pressure reduces the density of the covering projection.
            // A smaller projection outside the receiver cannot absorb that bias.
            return min(level + pressureLevels, viewCount - 1u);
    }
    return viewCount;
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
    uint2 atlasOrigin = uint2(context.AtlasPage & (parameters.PhysicalPagesPerRow - 1u),
        context.AtlasPage >> firstbithigh(parameters.PhysicalPagesPerRow)) * parameters.PageSize;
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
            VirtualShadowDiskSample(index), sine, cosine) * blockerSearchRadiusTexels;
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
            VirtualShadowDiskSample(index), sine, cosine) * filterRadius;
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

#include "vsm_resident_math.slang"
#include "vsm_prefilter_sampling.slang"
#ifdef VSM_PREFILTERED_LOOKUP_ENABLED
Texture2D<float> VirtualShadowPrefilteredAtlas;
#endif

bool TryReadVirtualShadowDirectionalResidentReverseDepth(StructuredBuffer<VirtualShadowMapParameters> views,
    StructuredBuffer<uint> pageTable, StructuredBuffer<uint> residentPages, Texture2DArray<uint> atlas,
    uint requestedViewIndex, float2 uv, out float storedDepth, out uint sourceViewIndex)
{
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
            storedDepth = VirtualShadowReverseDepthInRequestedClipmap(depth, transform);
            sourceViewIndex = requestedViewIndex + sourceOffset;
            return true;
        }
        offset = sourceOffset + 1u;
    }
    storedDepth = 0.0;
    sourceViewIndex = requestedViewIndex;
    return false;
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
        firstView, clipmapLevelCount, worldPosition);
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
