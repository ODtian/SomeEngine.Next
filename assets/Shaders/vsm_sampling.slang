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
    uint Pad0;
    uint Pad1;
};

static const uint VSM_PAGE_UNMAPPED = 0u;
static const uint VSM_PAGE_LOCKED = 0xFFFFFFFFu;

uint PackVirtualShadowPage(uint physicalPageEntry)
{
    return physicalPageEntry;
}

bool DecodeVirtualShadowPage(
    uint packedEntry,
    out uint physicalPage)
{
    physicalPage = packedEntry > 0u && packedEntry != VSM_PAGE_LOCKED
        ? packedEntry - 1u
        : 0u;
    return packedEntry != VSM_PAGE_UNMAPPED && packedEntry != VSM_PAGE_LOCKED;
}

uint VirtualShadowPagesPerRow(VirtualShadowMapParameters parameters)
{
    return parameters.VirtualResolution / parameters.PageSize;
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

bool TrySampleVirtualShadowMap(
    VirtualShadowMapParameters parameters,
    StructuredBuffer<uint4> pageTable,
    Texture2D<uint> physicalAtlas,
    float3 worldPosition,
    out float shadow)
{
    float3 ndc;
    float2 uv;
    if (!ProjectVirtualShadowPosition(parameters, worldPosition, ndc, uv))
    {
        shadow = 1.0;
        return false;
    }

    uint2 virtualPixel = min(
        (uint2)(uv * float(parameters.VirtualResolution)),
        parameters.VirtualResolution - 1u);
    uint2 localPage = virtualPixel / parameters.PageSize;
    int2 worldPage = VirtualShadowWorldPage(parameters, localPage);
    uint pageTableIndex = VirtualShadowPageTableIndex(parameters, localPage);
    uint4 pageEntry = pageTable[pageTableIndex];
    uint physicalPage;
    if (!IsVirtualShadowPageTag(pageEntry, worldPage) ||
        pageEntry.w != parameters.CacheGeneration ||
        !DecodeVirtualShadowPage(pageEntry.x, physicalPage))
    {
        shadow = 1.0;
        return false;
    }
    uint2 physicalPageCoord = uint2(
        physicalPage % parameters.PhysicalPagesPerRow,
        physicalPage / parameters.PhysicalPagesPerRow);
    uint2 atlasPixel =
        physicalPageCoord * parameters.PageSize +
        virtualPixel % parameters.PageSize;
    float storedDepth = 1.0 - asfloat(physicalAtlas.Load(int3(atlasPixel, 0)));
    shadow = ndc.z <= storedDepth + parameters.DepthBias ? 1.0 : 0.0;
    return true;
}

float SampleVirtualShadowMap(
    VirtualShadowMapParameters parameters,
    StructuredBuffer<uint4> pageTable,
    Texture2D<uint> physicalAtlas,
    float3 worldPosition)
{
    float shadow;
    return TrySampleVirtualShadowMap(
        parameters,
        pageTable,
        physicalAtlas,
        worldPosition,
        shadow)
        ? shadow
        : 1.0;
}

float SampleDirectionalVirtualShadowMap(
    StructuredBuffer<VirtualShadowMapParameters> views,
    StructuredBuffer<uint4> pageTable,
    Texture2D<uint> physicalAtlas,
    uint firstView,
    uint clipmapLevelCount,
    float3 worldPosition)
{
    if (clipmapLevelCount == 0u)
        return 1.0;
    VirtualShadowMapParameters baseView = views[firstView];
    float distanceToClipmapOrigin = length(
        worldPosition - baseView.ClipmapWorldOrigin);
    int absoluteLevel = (int)floor(
        log2(max(distanceToClipmapOrigin, 1e-6)) +
        baseView.ResolutionLodBias);
    uint firstLevel = (uint)max(
        0,
        absoluteLevel - baseView.FirstClipmapLevel);
    if (firstLevel >= clipmapLevelCount)
        return 1.0;
    for (uint level = firstLevel; level < clipmapLevelCount; level++)
    {
        float shadow;
        if (TrySampleVirtualShadowMap(
                views[firstView + level],
                pageTable,
                physicalAtlas,
                worldPosition,
                shadow))
        {
            return shadow;
        }
    }
    return 1.0;
}

#endif
