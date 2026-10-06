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
    float2 DepthPadding;
    float3 WorldOriginHigh;
    float OriginHighPadding;
    float3 WorldOriginLow;
    float OriginLowPadding;
    int ClipmapCornerRelativeOffsetX;
    int ClipmapCornerRelativeOffsetY;
    uint2 WorldPageOriginX;
    uint2 WorldPageOriginY;
    float2 AddressPadding;
};

// Positions passed to this file are offsets from the owning Game world's origin.
// Users with another high/low reference can translate before projecting or sampling.
float3 TranslateVirtualShadowWorldPosition(float3 position, float3 originHigh,
    float3 originLow, VirtualShadowMapParameters view)
{
    return position + ((originHigh - view.WorldOriginHigh) + (originLow - view.WorldOriginLow));
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
static const uint VSM_PAGE_REQUEST_DETAIL = 1u << 0u;
static const uint VSM_PAGE_REQUEST_COARSE = 1u << 1u;

static const uint VSM_PAGE_UNMAPPED = 0u;
static const uint VSM_PAGE_LOCKED = 0xFFFFFFFFu;
// A completed masked page is usable only in its covered receiver cells this frame.
// It is not a whole-page cache entry or a source of prior-frame HiZ.
static const uint VSM_PAGE_RECEIVER_DEPTH = 0x80000000u;
static const uint VSM_PAGE_MASKED = 1u;
static const uint VSM_PAGE_PRIOR_HIZ = 2u;
#ifndef VSM_WHOLE_PAGE_SAMPLING
StructuredBuffer<uint2> VirtualShadowReceiverMasks;
#endif

uint PackVirtualShadowPage(uint pageEntry)
{
    return pageEntry;
}

bool DecodeVirtualShadowPage(uint packedEntry, out uint atlasPage)
{
    atlasPage = packedEntry > 0u && packedEntry != VSM_PAGE_LOCKED
        ? packedEntry - 1u
        : 0u;
    return packedEntry != VSM_PAGE_UNMAPPED && packedEntry != VSM_PAGE_LOCKED;
}

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
    return VirtualShadowMipOffset(parameters, parameters.MipLevelCount);
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

bool IsVirtualShadowPageTag(uint4 entry, int2 worldPage)
{
    return asint(entry.y) == worldPage.x && asint(entry.z) == worldPage.y;
}

bool ProjectVirtualShadowPosition(
    VirtualShadowMapParameters parameters,
    float3 worldPosition,
    out float3 ndc,
    out float2 uv)
{
    float4 clip = mul(float4(worldPosition, 1.0), parameters.LightViewProjection);
    if (clip.w <= 0.0)
    {
        ndc = float3(0.0);
        uv = float2(0.0);
        return false;
    }
    ndc = clip.xyz / clip.w;
    uv = ndc.xy * float2(0.5, -0.5) + 0.5;
    return all(uv >= 0.0) && all(uv < 1.0) && ndc.z >= 0.0 && ndc.z <= 1.0;
}

bool ProjectVirtualShadowFootprintPosition(
    VirtualShadowMapParameters parameters,
    float3 worldPosition,
    out float2 uv)
{
    float4 clip = mul(float4(worldPosition, 1.0), parameters.LightViewProjection);
    if (clip.w <= 1e-8)
    {
        uv = 0.0.xx;
        return false;
    }
    float2 ndc = clip.xy / clip.w;
    uv = ndc * float2(0.5, -0.5) + 0.5;
    return true;
}

float ReadVirtualShadowDepth(
    VirtualShadowMapParameters parameters,
    Texture2D<uint> atlas,
    uint atlasPage,
    uint2 pagePixel)
{
    uint2 atlasPageCoord = uint2(
        atlasPage & (parameters.PhysicalPagesPerRow - 1u),
        atlasPage >> firstbithigh(parameters.PhysicalPagesPerRow));
    uint2 atlasPixel = atlasPageCoord * parameters.PageSize + pagePixel;
    return 1.0 - asfloat(atlas.Load(int3(atlasPixel, 0)));
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
    StructuredBuffer<uint4> pageTable,
    uint mipLevel,
    uint2 localPage,
    out uint atlasPage)
{
    int2 worldPage = VirtualShadowWorldPage(parameters, mipLevel, localPage);
    uint4 entry = pageTable[
        VirtualShadowPageTableIndex(parameters, mipLevel, localPage)];
    return IsVirtualShadowPageTag(entry, worldPage) &&
        entry.w == parameters.CacheGeneration &&
        DecodeVirtualShadowPage(entry.x, atlasPage);
}

bool TryResolveVirtualShadowPixel(
    VirtualShadowMapParameters parameters,
    StructuredBuffer<uint4> pageTable,
    uint mipLevel,
    uint2 mipPixel,
    out uint atlasPage,
    out bool wholePage)
{
    uint2 localPage = mipPixel >> firstbithigh(parameters.PageSize);
    uint tableIndex = VirtualShadowPageTableIndex(parameters, mipLevel, localPage);
    uint4 entry = pageTable[tableIndex];
    wholePage = entry.w == parameters.CacheGeneration;
    if (!IsVirtualShadowPageTag(entry, VirtualShadowWorldPage(parameters, mipLevel, localPage)) ||
        !DecodeVirtualShadowPage(entry.x, atlasPage))
        return false;
    if (wholePage) return true;
#ifndef VSM_WHOLE_PAGE_SAMPLING
    if (entry.w == (parameters.CacheGeneration | VSM_PAGE_RECEIVER_DEPTH))
    {
        uint2 cell = (mipPixel & (parameters.PageSize - 1u)) * 8u / parameters.PageSize;
        uint bit = cell.y * 8u + cell.x;
        uint2 mask = VirtualShadowReceiverMasks[tableIndex];
        return ((bit < 32u ? mask.x : mask.y) & (1u << (bit & 31u))) != 0u;
    }
#endif
    return false;
}

bool TryReadVirtualShadowMipDepth(
    VirtualShadowMapParameters parameters,
    StructuredBuffer<uint4> pageTable,
    Texture2D<uint> atlas,
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
    storedDepth = ReadVirtualShadowDepth(
        parameters,
        atlas,
        atlasPage,
        mipPixel & (parameters.PageSize - 1u));
    return true;
}

bool TryResolveVirtualShadowResidentPixel(
    VirtualShadowMapParameters parameters, StructuredBuffer<uint4> pageTable,
    StructuredBuffer<uint2> residentPages, uint firstMip, float2 uv,
    out uint resolvedMip, out uint atlasPage, out bool wholePage)
{
    uint mipCount = max(parameters.MipLevelCount, 1u);
    uint mip = clamp(firstMip, VirtualShadowFirstMip(parameters), mipCount - 1u);
    while (mip < mipCount)
    {
        uint resolution = VirtualShadowMipResolution(parameters, mip);
        uint2 pixel = min(uint2(uv * float(resolution)), resolution - 1u);
        uint2 localPage = pixel / parameters.PageSize;
        uint2 source = residentPages[VirtualShadowPageTableIndex(parameters, mip, localPage)];
        if (source.x == 0u) break;
        resolvedMip = mip + source.y;
        resolution = VirtualShadowMipResolution(parameters, resolvedMip);
        pixel = min(uint2(uv * float(resolution)), resolution - 1u);
        uint sourceIndex = source.x - 1u;
        uint4 entry = pageTable[sourceIndex];
        wholePage = entry.w == parameters.CacheGeneration;
        bool completed = wholePage || entry.w == (parameters.CacheGeneration | VSM_PAGE_RECEIVER_DEPTH);
        if (completed && IsVirtualShadowPageTag(entry, VirtualShadowWorldPage(parameters, resolvedMip, pixel / parameters.PageSize)) &&
            DecodeVirtualShadowPage(entry.x, atlasPage))
        {
            if (wholePage) return true;
#ifndef VSM_WHOLE_PAGE_SAMPLING
            uint2 cell = (pixel & (parameters.PageSize - 1u)) * 8u / parameters.PageSize;
            uint bit = cell.y * 8u + cell.x;
            uint2 mask = VirtualShadowReceiverMasks[sourceIndex];
            if (((bit < 32u ? mask.x : mask.y) & (1u << (bit & 31u))) != 0u) return true;
#endif
        }
        mip = resolvedMip + 1u;
    }
    resolvedMip = mipCount - 1u;
    atlasPage = 0u;
    wholePage = false;
    return false;
}

bool TryReadVirtualShadowResidentDepth(VirtualShadowMapParameters parameters,
    StructuredBuffer<uint4> pageTable, StructuredBuffer<uint2> residentPages, Texture2D<uint> atlas,
    uint firstMip, float2 uv, out float storedDepth, out uint resolvedMip)
{
    uint page;
    bool whole;
    if (!TryResolveVirtualShadowResidentPixel(parameters, pageTable, residentPages, firstMip, uv,
            resolvedMip, page, whole)) { storedDepth = 0.0; return false; }
    uint resolution = VirtualShadowMipResolution(parameters, resolvedMip);
    uint2 pixel = min(uint2(uv * float(resolution)), resolution - 1u);
    storedDepth = ReadVirtualShadowDepth(parameters, atlas, page, pixel & (parameters.PageSize - 1u));
    return true;
}

#ifdef VSM_RESIDENT_LOOKUP_ENABLED
StructuredBuffer<uint2> VirtualShadowResidentPages;
#endif

bool TryReadVirtualShadowDepthAtOrCoarser(
    VirtualShadowMapParameters parameters,
    StructuredBuffer<uint4> pageTable,
    Texture2D<uint> atlas,
    uint firstMip,
    float2 uv,
    out float storedDepth,
    out uint resolvedMip)
{
#ifdef VSM_RESIDENT_LOOKUP_ENABLED
    return TryReadVirtualShadowResidentDepth(parameters, pageTable, VirtualShadowResidentPages, atlas,
        firstMip, uv, storedDepth, resolvedMip);
#else
    uint mipCount = max(parameters.MipLevelCount, 1u);
    [loop]
    for (uint mip = clamp(firstMip, VirtualShadowFirstMip(parameters), mipCount - 1u); mip < mipCount; mip++)
    {
        uint resolution = VirtualShadowMipResolution(parameters, mip);
        uint2 pixel = min((uint2)(uv * float(resolution)), resolution - 1u);
        if (TryReadVirtualShadowMipDepth(
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
    float4 lightClip = mul(float4(worldPosition, 1.0), parameters.LightViewProjection);
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
    StructuredBuffer<uint4> pageTable,
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
    StructuredBuffer<uint4> pageTable,
    Texture2D<uint> atlas,
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
    StructuredBuffer<uint4> pageTable,
    Texture2D<uint> atlas,
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
    StructuredBuffer<uint4> pageTable,
    Texture2D<uint> atlas,
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
        storedDepth = 1.0 - asfloat(atlas.Load(int3(atlasPixel, 0)));
        texelCenter = (float2(pixel) + 0.5) / float(resolution);
        return true;
    }
    return TryReadVirtualShadowDepthCached(
        parameters, pageTable, atlas, context,
        (float2(pixel) + 0.5) / float(resolution), storedDepth, texelCenter);
}

bool TryReadVirtualShadowFilterDepth(
    VirtualShadowMapParameters parameters,
    StructuredBuffer<uint4> pageTable,
    Texture2D<uint> atlas,
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
    float4 clip = mul(float4(worldPosition, 1.0), parameters.LightViewProjection);
    float4 clipDx = mul(float4(worldPositionDx, 1.0), parameters.LightViewProjection);
    float4 clipDy = mul(float4(worldPositionDy, 1.0), parameters.LightViewProjection);
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
    VirtualShadowMapParameters parameters, float3 ndc, float3 normal)
{
    normal *= rsqrt(max(dot(normal, normal), 1e-30));
    float3 tangent = cross(normal, abs(normal.y) < 0.99 ? float3(0, 1, 0) : float3(1, 0, 0));
    tangent *= rsqrt(max(dot(tangent, tangent), 1e-30));
    float3 bitangent = cross(normal, tangent);
    float3 clipW = float3(parameters.LightViewProjection[0][3],
        parameters.LightViewProjection[1][3], parameters.LightViewProjection[2][3]);
    float3 clipX = float3(parameters.LightViewProjection[0][0],
        parameters.LightViewProjection[1][0], parameters.LightViewProjection[2][0]) - ndc.x * clipW;
    float3 clipY = float3(parameters.LightViewProjection[0][1],
        parameters.LightViewProjection[1][1], parameters.LightViewProjection[2][1]) - ndc.y * clipW;
    float3 clipZ = float3(parameters.LightViewProjection[0][2],
        parameters.LightViewProjection[1][2], parameters.LightViewProjection[2][2]) - ndc.z * clipW;
    // Differentiate the light projection directly. Camera pixel jitter and the
    // precision of adjacent reconstructed world positions leave this plane unchanged.
    float2 uvDx = float2(dot(tangent, clipX), dot(tangent, clipY)) * float2(0.5, -0.5);
    float2 uvDy = float2(dot(bitangent, clipX), dot(bitangent, clipY)) * float2(0.5, -0.5);
    float depthDx = dot(tangent, clipZ);
    float depthDy = dot(bitangent, clipZ);
    float determinant = uvDx.x * uvDy.y - uvDx.y * uvDy.x;
    if (abs(determinant) < 1e-15) return 0.0.xx;
    return float2(depthDx * uvDy.y - depthDy * uvDx.y,
        uvDx.x * depthDy - uvDy.x * depthDx) / determinant;
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
    StructuredBuffer<uint4> pageTable,
    Texture2D<uint> atlas,
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
    StructuredBuffer<uint4> pageTable,
    Texture2D<uint> atlas,
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
    VirtualShadowMapParameters parameters, StructuredBuffer<uint4> pageTable, Texture2D<uint> atlas,
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
    VirtualShadowMapParameters parameters, StructuredBuffer<uint4> pageTable, Texture2D<uint> atlas,
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
    StructuredBuffer<uint4> pageTable,
    Texture2D<uint> atlas,
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

bool TryReadVirtualShadowDirectionalResidentDepth(StructuredBuffer<VirtualShadowMapParameters> views,
    StructuredBuffer<uint4> pageTable, StructuredBuffer<uint2> residentPages, Texture2D<uint> atlas,
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
        uint2 reference = residentPages[VirtualShadowPageTableIndex(current, 0u, uint2(localPage))];
        if (reference.x == 0u) break;
        uint sourceOffset = offset + reference.y;
        VirtualShadowMapParameters source = views[requestedViewIndex + sourceOffset];
        int2 sourcePage = sourceOffset == 0u ? int2(basePage) : VirtualShadowCoarseClipmapPage(basePage, requested, source, sourceOffset);
        float4 transform = VirtualShadowClipmapScaleBias(requested, source, sourceOffset);
        float2 sourceUV = uv * transform.x + transform.yz;
        uint2 pixel = clamp(uint2(max(sourceUV, 0.0.xx) * float(source.VirtualResolution)),
            uint2(sourcePage) * source.PageSize, (uint2(sourcePage) + 1u) * source.PageSize - 1u);
        float depth;
        if (TryReadVirtualShadowMipDepth(source, pageTable, atlas, 0u, pixel, depth))
        {
            storedDepth = VirtualShadowDepthInRequestedClipmap(depth, transform);
            sourceViewIndex = requestedViewIndex + sourceOffset;
            return true;
        }
        offset = sourceOffset + 1u;
    }
    storedDepth = 0.0;
    sourceViewIndex = requestedViewIndex;
    return false;
}

bool TrySampleDirectionalVirtualShadowMap(
    StructuredBuffer<VirtualShadowMapParameters> views,
    StructuredBuffer<uint4> pageTable,
    Texture2D<uint> atlas,
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
        uint2 reference = VirtualShadowResidentPages[VirtualShadowPageTableIndex(current, 0u, uint2(localPage))];
        if (reference.x == 0u) break;
        uint sourceOffset = offset + reference.y;
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
                visibility, CalculateVirtualShadowReceiverPlaneDepthGradient(source, sourceNdc, receiverPlaneNormal))) return true;
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
                        views[firstView + level], ndc, receiverPlaneNormal))) return true;
        }
    }
#endif
    visibility = 1.0;
    return false;
}

float SampleDirectionalVirtualShadowMap(
    StructuredBuffer<VirtualShadowMapParameters> views,
    StructuredBuffer<uint4> pageTable,
    Texture2D<uint> atlas,
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
    StructuredBuffer<uint4> pageTable,
    Texture2D<uint> atlas,
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
        visibility, CalculateVirtualShadowReceiverPlaneDepthGradient(view, ndc, receiverPlaneNormal));
}

float SamplePointVirtualShadowMap(
    StructuredBuffer<VirtualShadowMapParameters> views,
    StructuredBuffer<uint4> pageTable,
    Texture2D<uint> atlas,
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
    StructuredBuffer<uint4> pageTable,
    Texture2D<uint> atlas,
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
        visibility, CalculateVirtualShadowReceiverPlaneDepthGradient(view, ndc, receiverPlaneNormal));
}

float SampleSpotVirtualShadowMap(
    StructuredBuffer<VirtualShadowMapParameters> views,
    StructuredBuffer<uint4> pageTable,
    Texture2D<uint> atlas,
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
    StructuredBuffer<VirtualShadowMapParameters> views, StructuredBuffer<uint4> pageTable, Texture2D<uint> atlas,
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
    StructuredBuffer<VirtualShadowMapParameters> views, StructuredBuffer<uint4> pageTable, Texture2D<uint> atlas,
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
    StructuredBuffer<VirtualShadowMapParameters> views, StructuredBuffer<uint4> pageTable, Texture2D<uint> atlas,
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
