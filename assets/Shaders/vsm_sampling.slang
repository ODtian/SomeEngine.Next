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
    uint CoarsePageShift;
    uint CoarsePageTableOffset;
    uint LightKind;
    uint FaceIndex;
    float3 LightPosition;
    float LightRange;
};

static const uint VSM_LIGHT_DIRECTIONAL = 0u;
static const uint VSM_LIGHT_POINT = 1u;
static const uint VSM_LIGHT_SPOT = 2u;

static const uint VSM_PAGE_UNMAPPED = 0u;
static const uint VSM_PAGE_LOCKED = 0xFFFFFFFFu;

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
    return parameters.VirtualResolution / parameters.PageSize;
}

uint VirtualShadowNextPowerOfTwo(uint value)
{
    value = max(value, 1u) - 1u;
    value |= value >> 1u;
    value |= value >> 2u;
    value |= value >> 4u;
    value |= value >> 8u;
    value |= value >> 16u;
    return value + 1u;
}

uint VirtualShadowCoarsePagesPerRow(VirtualShadowMapParameters parameters)
{
    uint finePages = VirtualShadowPagesPerRow(parameters);
    uint scale = 1u << parameters.CoarsePageShift;
    uint coverage = ((finePages + scale - 1u) >> parameters.CoarsePageShift) + 1u;
    return VirtualShadowNextPowerOfTwo(coverage);
}

int2 VirtualShadowWorldPage(
    VirtualShadowMapParameters parameters,
    uint2 localPage)
{
    return int2(parameters.VirtualPageOriginX, parameters.VirtualPageOriginY) +
        int2(localPage);
}

uint2 VirtualShadowTableSlot(
    VirtualShadowMapParameters parameters,
    int2 worldPage)
{
    uint mask = VirtualShadowPagesPerRow(parameters) - 1u;
    return uint2(asuint(worldPage.x) & mask, asuint(worldPage.y) & mask);
}

uint VirtualShadowPageTableIndex(
    VirtualShadowMapParameters parameters,
    uint2 localPage)
{
    int2 worldPage = VirtualShadowWorldPage(parameters, localPage);
    uint2 slot = VirtualShadowTableSlot(parameters, worldPage);
    return parameters.PageTableOffset +
        slot.y * VirtualShadowPagesPerRow(parameters) + slot.x;
}

int VirtualShadowFloorDiv(int value, int divisor)
{
    int quotient = value / divisor;
    int remainder = value % divisor;
    return remainder < 0 ? quotient - 1 : quotient;
}

int VirtualShadowFloorMod(int value, int divisor)
{
    int remainder = value % divisor;
    return remainder < 0 ? remainder + divisor : remainder;
}

int2 VirtualShadowWorldPixel(
    VirtualShadowMapParameters parameters,
    uint2 virtualPixel)
{
    return int2(parameters.VirtualPageOriginX, parameters.VirtualPageOriginY) *
        int(parameters.PageSize) + int2(virtualPixel);
}

int2 VirtualShadowCoarseWorldPage(
    VirtualShadowMapParameters parameters,
    uint2 virtualPixel)
{
    int span = int(parameters.PageSize << parameters.CoarsePageShift);
    int2 worldPixel = VirtualShadowWorldPixel(parameters, virtualPixel);
    return int2(
        VirtualShadowFloorDiv(worldPixel.x, span),
        VirtualShadowFloorDiv(worldPixel.y, span));
}

uint2 VirtualShadowCoarseTableSlot(
    VirtualShadowMapParameters parameters,
    int2 coarseWorldPage)
{
    uint mask = VirtualShadowCoarsePagesPerRow(parameters) - 1u;
    return uint2(
        asuint(coarseWorldPage.x) & mask,
        asuint(coarseWorldPage.y) & mask);
}

uint VirtualShadowCoarsePageTableIndex(
    VirtualShadowMapParameters parameters,
    int2 coarseWorldPage)
{
    uint2 slot = VirtualShadowCoarseTableSlot(parameters, coarseWorldPage);
    return parameters.CoarsePageTableOffset +
        slot.y * VirtualShadowCoarsePagesPerRow(parameters) + slot.x;
}

uint2 VirtualShadowCoarseAtlasPixel(
    VirtualShadowMapParameters parameters,
    uint2 virtualPixel)
{
    int span = int(parameters.PageSize << parameters.CoarsePageShift);
    int2 worldPixel = VirtualShadowWorldPixel(parameters, virtualPixel);
    return uint2(
        uint(VirtualShadowFloorMod(worldPixel.x, span)) >> parameters.CoarsePageShift,
        uint(VirtualShadowFloorMod(worldPixel.y, span)) >> parameters.CoarsePageShift);
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

float ReadVirtualShadowDepth(
    VirtualShadowMapParameters parameters,
    Texture2D<uint> atlas,
    uint atlasPage,
    uint2 pagePixel)
{
    uint2 atlasPageCoord = uint2(
        atlasPage % parameters.PhysicalPagesPerRow,
        atlasPage / parameters.PhysicalPagesPerRow);
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

bool TryResolveVirtualShadowFinePage(
    VirtualShadowMapParameters parameters,
    StructuredBuffer<uint4> pageTable,
    uint2 localPage,
    out uint atlasPage)
{
    int2 worldPage = VirtualShadowWorldPage(parameters, localPage);
    uint4 entry = pageTable[VirtualShadowPageTableIndex(parameters, localPage)];
    return IsVirtualShadowPageTag(entry, worldPage) &&
        entry.w == parameters.CacheGeneration &&
        DecodeVirtualShadowPage(entry.x, atlasPage);
}

bool TryResolveVirtualShadowCoarsePage(
    VirtualShadowMapParameters parameters,
    StructuredBuffer<uint4> pageTable,
    int2 worldPage,
    out uint atlasPage)
{
    if (parameters.CoarsePageShift == 0u)
    {
        atlasPage = 0u;
        return false;
    }
    uint4 entry = pageTable[
        VirtualShadowCoarsePageTableIndex(parameters, worldPage)];
    return IsVirtualShadowPageTag(entry, worldPage) &&
        entry.w == parameters.CacheGeneration &&
        DecodeVirtualShadowPage(entry.x, atlasPage);
}

bool TryReadVirtualShadowFineDepth(
    VirtualShadowMapParameters parameters,
    StructuredBuffer<uint4> pageTable,
    Texture2D<uint> atlas,
    uint2 virtualPixel,
    out float storedDepth)
{
    uint2 localPage = virtualPixel / parameters.PageSize;
    uint atlasPage;
    if (!TryResolveVirtualShadowFinePage(
            parameters,
            pageTable,
            localPage,
            atlasPage))
    {
        storedDepth = 0.0;
        return false;
    }
    storedDepth = ReadVirtualShadowDepth(
        parameters,
        atlas,
        atlasPage,
        virtualPixel % parameters.PageSize);
    return true;
}

bool TryReadVirtualShadowCoarseDepth(
    VirtualShadowMapParameters parameters,
    StructuredBuffer<uint4> pageTable,
    Texture2D<uint> atlas,
    uint2 virtualPixel,
    out float storedDepth)
{
    if (parameters.CoarsePageShift == 0u)
    {
        storedDepth = 0.0;
        return false;
    }
    int2 worldPage = VirtualShadowCoarseWorldPage(parameters, virtualPixel);
    uint atlasPage;
    if (!TryResolveVirtualShadowCoarsePage(
            parameters,
            pageTable,
            worldPage,
            atlasPage))
    {
        storedDepth = 0.0;
        return false;
    }
    storedDepth = ReadVirtualShadowDepth(
        parameters,
        atlas,
        atlasPage,
        VirtualShadowCoarseAtlasPixel(parameters, virtualPixel));
    return true;
}

bool TryReadVirtualShadowDepth(
    VirtualShadowMapParameters parameters,
    StructuredBuffer<uint4> pageTable,
    Texture2D<uint> atlas,
    uint2 virtualPixel,
    out float storedDepth)
{
    return TryReadVirtualShadowFineDepth(
            parameters,
            pageTable,
            atlas,
            virtualPixel,
            storedDepth) ||
        TryReadVirtualShadowCoarseDepth(
            parameters,
            pageTable,
            atlas,
            virtualPixel,
            storedDepth);
}

bool TrySampleVirtualShadowFine(
    VirtualShadowMapParameters parameters,
    StructuredBuffer<uint4> pageTable,
    Texture2D<uint> atlas,
    float3 worldPosition,
    out float shadow)
{
    float3 ndc;
    float2 uv;
    if (!ProjectVirtualShadowPosition(parameters, worldPosition, ndc, uv))
    {
        shadow = 0.0;
        return false;
    }

    uint2 virtualPixel = min(
        (uint2)(uv * float(parameters.VirtualResolution)),
        parameters.VirtualResolution - 1u);
    float storedDepth;
    if (!TryReadVirtualShadowFineDepth(
            parameters, pageTable, atlas, virtualPixel, storedDepth))
    {
        shadow = 0.0;
        return false;
    }
    shadow = ndc.z <= storedDepth + parameters.DepthBias ? 1.0 : 0.0;
    return true;
}

bool TrySampleVirtualShadowCoarse(
    VirtualShadowMapParameters parameters,
    StructuredBuffer<uint4> pageTable,
    Texture2D<uint> atlas,
    float3 worldPosition,
    out float shadow)
{
    if (parameters.CoarsePageShift == 0u)
    {
        shadow = 0.0;
        return false;
    }

    float3 ndc;
    float2 uv;
    if (!ProjectVirtualShadowPosition(parameters, worldPosition, ndc, uv))
    {
        shadow = 0.0;
        return false;
    }

    uint2 virtualPixel = min(
        (uint2)(uv * float(parameters.VirtualResolution)),
        parameters.VirtualResolution - 1u);
    float storedDepth;
    if (!TryReadVirtualShadowCoarseDepth(
            parameters, pageTable, atlas, virtualPixel, storedDepth))
    {
        shadow = 0.0;
        return false;
    }
    shadow = ndc.z <= storedDepth + parameters.DepthBias ? 1.0 : 0.0;
    return true;
}

bool TrySampleVirtualShadowMap(
    VirtualShadowMapParameters parameters,
    StructuredBuffer<uint4> pageTable,
    Texture2D<uint> atlas,
    float3 worldPosition,
    out float shadow)
{
    return TrySampleVirtualShadowFine(parameters, pageTable, atlas, worldPosition, shadow) ||
        TrySampleVirtualShadowCoarse(parameters, pageTable, atlas, worldPosition, shadow);
}

float SampleVirtualShadowMap(
    VirtualShadowMapParameters parameters,
    StructuredBuffer<uint4> pageTable,
    Texture2D<uint> atlas,
    float3 worldPosition)
{
    float3 ndc;
    float2 uv;
    if (!ProjectVirtualShadowPosition(parameters, worldPosition, ndc, uv))
        return 1.0;
    uint2 virtualPixel = min(
        (uint2)(uv * float(parameters.VirtualResolution)),
        parameters.VirtualResolution - 1u);
    float storedDepth;
    if (!TryReadVirtualShadowDepth(
            parameters, pageTable, atlas, virtualPixel, storedDepth))
        return 0.0;
    return ndc.z <= storedDepth + parameters.DepthBias ? 1.0 : 0.0;
}

struct VirtualShadowFilterContext
{
    uint2 FineLocalPage;
    uint FineAtlasPage;
    bool FineResident;
    int2 CoarseWorldPage;
    uint CoarseAtlasPage;
    bool CoarseResident;
};

VirtualShadowFilterContext BuildVirtualShadowFilterContext(
    VirtualShadowMapParameters parameters,
    StructuredBuffer<uint4> pageTable,
    uint2 centerPixel)
{
    VirtualShadowFilterContext context;
    context.FineLocalPage = centerPixel / parameters.PageSize;
    context.FineResident = TryResolveVirtualShadowFinePage(
        parameters,
        pageTable,
        context.FineLocalPage,
        context.FineAtlasPage);
    context.CoarseWorldPage = VirtualShadowCoarseWorldPage(parameters, centerPixel);
    context.CoarseResident = TryResolveVirtualShadowCoarsePage(
        parameters,
        pageTable,
        context.CoarseWorldPage,
        context.CoarseAtlasPage);
    return context;
}

bool TryReadVirtualShadowDepthCached(
    VirtualShadowMapParameters parameters,
    StructuredBuffer<uint4> pageTable,
    Texture2D<uint> atlas,
    VirtualShadowFilterContext context,
    uint2 virtualPixel,
    out float storedDepth)
{
    uint2 localPage = virtualPixel / parameters.PageSize;
    if (all(localPage == context.FineLocalPage))
    {
        if (context.FineResident)
        {
            storedDepth = ReadVirtualShadowDepth(
                parameters,
                atlas,
                context.FineAtlasPage,
                virtualPixel % parameters.PageSize);
            return true;
        }
    }
    else if (TryReadVirtualShadowFineDepth(
            parameters,
            pageTable,
            atlas,
            virtualPixel,
            storedDepth))
    {
        return true;
    }

    int2 coarseWorldPage = VirtualShadowCoarseWorldPage(parameters, virtualPixel);
    if (all(coarseWorldPage == context.CoarseWorldPage))
    {
        if (!context.CoarseResident)
        {
            storedDepth = 0.0;
            return false;
        }
        storedDepth = ReadVirtualShadowDepth(
            parameters,
            atlas,
            context.CoarseAtlasPage,
            VirtualShadowCoarseAtlasPixel(parameters, virtualPixel));
        return true;
    }
    return TryReadVirtualShadowCoarseDepth(
        parameters,
        pageTable,
        atlas,
        virtualPixel,
        storedDepth);
}

float SampleVirtualShadowMapFilteredProjected(
    VirtualShadowMapParameters parameters,
    StructuredBuffer<uint4> pageTable,
    Texture2D<uint> atlas,
    float3 ndc,
    float2 uv,
    uint blockerSampleCount,
    uint filterSampleCount,
    float blockerSearchRadiusTexels,
    float maxFilterRadiusTexels)
{
    float2 virtualPosition = uv * float(parameters.VirtualResolution);
    float angle = frac(sin(dot(
        floor(virtualPosition),
        float2(12.9898, 78.233))) * 43758.5453) * 6.2831853;
    float sine;
    float cosine;
    sincos(angle, sine, cosine);
    uint2 centerPixel = (uint2)clamp(
        floor(virtualPosition),
        0.0,
        float(parameters.VirtualResolution - 1u));
    VirtualShadowFilterContext context = BuildVirtualShadowFilterContext(
        parameters,
        pageTable,
        centerPixel);
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
            float(parameters.VirtualResolution - 1u));
        float storedDepth;
        bool resident = TryReadVirtualShadowDepthCached(
            parameters, pageTable, atlas, context, samplePixel, storedDepth);
        if (!resident)
        {
            blockerCount++;
            blockerDepthSum += max(ndc.z - max(parameters.DepthBias, 1e-5), 0.0);
        }
        else if (ndc.z > storedDepth + parameters.DepthBias)
        {
            blockerCount++;
            blockerDepthSum += storedDepth;
        }
    }
    if (blockerCount == 0u)
        return 1.0;

    float averageBlockerDepth = blockerDepthSum / float(blockerCount);
    float separation = saturate(
        (ndc.z - averageBlockerDepth) / max(averageBlockerDepth, 1e-4));
    float filterRadius = min(
        maxFilterRadiusTexels,
        blockerSearchRadiusTexels + separation * maxFilterRadiusTexels);
    float visibility = 0.0;
    uint sampleCount = min(filterSampleCount, 16u);
    for (uint index = 0u; index < sampleCount; index++)
    {
        float2 offset = RotateVirtualShadowDiskSample(
            VirtualShadowDiskSample(index), sine, cosine) * filterRadius;
        uint2 samplePixel = (uint2)clamp(
            floor(virtualPosition + offset),
            0.0,
            float(parameters.VirtualResolution - 1u));
        float storedDepth;
        if (TryReadVirtualShadowDepthCached(
                parameters, pageTable, atlas, context, samplePixel, storedDepth))
        {
            visibility += ndc.z <= storedDepth + parameters.DepthBias ? 1.0 : 0.0;
        }
    }
    return visibility / float(max(sampleCount, 1u));
}

float SampleVirtualShadowMapFiltered(
    VirtualShadowMapParameters parameters,
    StructuredBuffer<uint4> pageTable,
    Texture2D<uint> atlas,
    float3 worldPosition,
    uint blockerSampleCount,
    uint filterSampleCount,
    float blockerSearchRadiusTexels,
    float maxFilterRadiusTexels)
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
        blockerSampleCount,
        filterSampleCount,
        blockerSearchRadiusTexels,
        maxFilterRadiusTexels);
}

float SampleDirectionalVirtualShadowMap(
    StructuredBuffer<VirtualShadowMapParameters> views,
    StructuredBuffer<uint4> pageTable,
    Texture2D<uint> atlas,
    uint firstView,
    uint clipmapLevelCount,
    float3 worldPosition,
    uint blockerSampleCount,
    uint filterSampleCount,
    float blockerSearchRadiusTexels,
    float maxFilterRadiusTexels)
{
    if (clipmapLevelCount == 0u)
        return 1.0;
    VirtualShadowMapParameters baseView = views[firstView];
    float distanceToClipmapOrigin = length(worldPosition - baseView.ClipmapWorldOrigin);
    int absoluteLevel = (int)floor(
        log2(max(distanceToClipmapOrigin, 1e-6)) + baseView.ResolutionLodBias);
    uint firstLevel = (uint)max(0, absoluteLevel - baseView.FirstClipmapLevel);
    if (firstLevel >= clipmapLevelCount)
        return 1.0;

    for (uint level = firstLevel; level < clipmapLevelCount; level++)
    {
        float3 ndc;
        float2 uv;
        if (ProjectVirtualShadowPosition(
                views[firstView + level], worldPosition, ndc, uv))
        {
            return SampleVirtualShadowMapFilteredProjected(
                views[firstView + level],
                pageTable,
                atlas,
                ndc,
                uv,
                blockerSampleCount,
                filterSampleCount,
                blockerSearchRadiusTexels,
                maxFilterRadiusTexels);
        }
    }
    return 1.0;
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

float SamplePointVirtualShadowMap(
    StructuredBuffer<VirtualShadowMapParameters> views,
    StructuredBuffer<uint4> pageTable,
    Texture2D<uint> atlas,
    uint firstView,
    float3 worldPosition,
    uint blockerSampleCount,
    uint filterSampleCount,
    float blockerSearchRadiusTexels,
    float maxFilterRadiusTexels)
{
    VirtualShadowMapParameters baseView = views[firstView];
    uint faceIndex = VirtualShadowPointFace(worldPosition - baseView.LightPosition);
    return SampleVirtualShadowMapFiltered(
        views[firstView + faceIndex],
        pageTable,
        atlas,
        worldPosition,
        blockerSampleCount,
        filterSampleCount,
        blockerSearchRadiusTexels,
        maxFilterRadiusTexels);
}

float SampleSpotVirtualShadowMap(
    StructuredBuffer<VirtualShadowMapParameters> views,
    StructuredBuffer<uint4> pageTable,
    Texture2D<uint> atlas,
    uint viewIndex,
    float3 worldPosition,
    uint blockerSampleCount,
    uint filterSampleCount,
    float blockerSearchRadiusTexels,
    float maxFilterRadiusTexels)
{
    return SampleVirtualShadowMapFiltered(
        views[viewIndex],
        pageTable,
        atlas,
        worldPosition,
        blockerSampleCount,
        filterSampleCount,
        blockerSearchRadiusTexels,
        maxFilterRadiusTexels);
}

#endif
