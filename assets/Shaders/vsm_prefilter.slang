#define VSM_WHOLE_PAGE_SAMPLING 1
#include "vsm_sampling.slang"
#include "vsm_prefilter_sampling.slang"
#include "vsm_prefilter_math.slang"
#include "resource_effects.slang"

#include "vsm_prefilter_common.slang"

ConstantBuffer<VirtualShadowPrefilterUniforms> Prefilter;
StructuredBuffer<VirtualShadowMapParameters> FilterViews;
StructuredBuffer<VirtualShadowMapParameters> PreviousFilterViews;
StructuredBuffer<VirtualShadowPrefilterProjection> FilterProjections;
StructuredBuffer<uint> FilterPageReferences;
StructuredBuffer<uint> FilterPageTable;
Texture2DArray<uint> FilterDepthAtlas;
RWTexture2D<float> FilterAxisX;
RWTexture2DArray<float> FilterSeparate;
RWTexture2D<float> FilterMerged;
StructuredBuffer<uint2> FilterReceiverMasks;


RWStructuredBuffer<uint> FilterPreviousPageTable;

RWStructuredBuffer<uint2> FilterPreviousReceiverMasks;


StructuredBuffer<uint> FilterPages;
ByteAddressBuffer FilterPageCount;

uint2 VirtualShadowFilterAtlasPixel(VirtualShadowMapParameters view, uint atlasPage, uint2 localPixel, bool border)
{
    return uint2(atlasPage % view.PhysicalPagesPerRow, atlasPage / view.PhysicalPagesPerRow) *
        (view.PageSize + (border ? 2u : 0u)) + localPixel + (border ? 1u : 0u);
}

uint VirtualShadowFilterNeighbor(VirtualShadowMapParameters view, uint2 page, int2 localPixel, out uint2 pixel)
{
    int2 virtualPixel = int2(page * view.PageSize) + localPixel;
    uint atlasPage;
    if (all(virtualPixel >= 0) && all(virtualPixel < int(view.VirtualResolution)))
    {
        uint2 neighbor = uint2(virtualPixel) / view.PageSize;
        uint entry = FilterPageTable[VirtualShadowPageTableIndex(view, 0u, neighbor)];
        if (IsVirtualShadowPageTag(entry, VirtualShadowWorldPage(view, 0u, neighbor)) &&
            VirtualShadowPageHasOwnDepth(entry) &&
            DecodeVirtualShadowPage(entry, view.PhysicalPagesPerRow, atlasPage))
        {
            pixel = uint2(virtualPixel) & (view.PageSize - 1u);
            return atlasPage;
        }
    }
    uint current = FilterPageTable[VirtualShadowPageTableIndex(view, 0u, page)];
    DecodeVirtualShadowPage(current, view.PhysicalPagesPerRow, atlasPage);
    pixel = uint2(clamp(localPixel, 0, int(view.PageSize) - 1));
    return atlasPage;
}

bool VirtualShadowFilterSample(VirtualShadowMapParameters view, bool previous, float2 uv, out float visibility)
{
    visibility = 1.0;
    if (any(uv < 0.0) || any(uv >= 1.0)) return false;
    float2 texel = uv * float(view.VirtualResolution) - .5;
    uint2 center = min(uint2(uv * float(view.VirtualResolution)), view.VirtualResolution - 1u);
    uint2 virtualPage = center / view.PageSize;
    uint index = VirtualShadowPageTableIndex(view, 0u, virtualPage);
    uint entry = previous ? FilterPreviousPageTable[index] : FilterPageTable[index];
    uint atlasPage;
    if (!IsVirtualShadowPageTag(entry, VirtualShadowWorldPage(view, 0u, virtualPage)) ||
        !VirtualShadowPageHasOwnDepth(entry) ||
        !DecodeVirtualShadowPage(entry, view.PhysicalPagesPerRow, atlasPage)) return false;
    uint layer = Prefilter.WriteLayer ^ (previous ? 1u : 0u);
    uint2 atlasBase = uint2(atlasPage % view.PhysicalPagesPerRow, atlasPage / view.PhysicalPagesPerRow) * (view.PageSize + 2u);
    int2 base = int2(floor(texel)) - int2(virtualPage * view.PageSize) + int2(atlasBase) + 1;
    float2 fraction = frac(texel);
    float top = lerp(FilterSeparate[int3(base, layer)], FilterSeparate[int3(base + int2(1, 0), layer)], fraction.x);
    float bottom = lerp(FilterSeparate[int3(base + int2(0, 1), layer)], FilterSeparate[int3(base + 1, layer)], fraction.x);
    visibility = lerp(top, bottom, fraction.y);
    return true;
}

float VirtualShadowFilterPrevious(VirtualShadowMapParameters view, uint viewIndex, uint2 page, uint2 localPixel, float current)
{
    uint first = FilterProjections[viewIndex].PreviousFirstView;
    if (Prefilter.HasHistory == 0u || first >= Prefilter.PreviousViewCount) return current;
    float2 uv = (float2(page * view.PageSize + localPixel) + .5) / float(view.VirtualResolution);
    float depth = (view.DepthCullRange.x + view.DepthCullRange.y) * .5;
    float4 world = mul(float4(uv * float2(2.0, -2.0) + float2(-1.0, 1.0), depth, 1.0),
        FilterProjections[viewIndex].ShadowToWorld);
    float3 localWorld = world.xyz / world.w;
    VirtualShadowMapParameters previousBase = PreviousFilterViews[first];
    float3 previousWorld = TranslateVirtualShadowWorldPosition(localWorld, view.WorldOriginHigh, view.WorldOriginLow, previousBase);
    uint count = min(previousBase.ClipmapLevelCount, Prefilter.PreviousViewCount - first);
    uint level = SelectDirectionalVirtualShadowView(PreviousFilterViews, first, count, previousWorld,
        previousWorld, previousWorld, 0.0);
    if (level >= count) return current;
    VirtualShadowMapParameters previous = PreviousFilterViews[first + level];
    float3 ndc;
    float2 previousUV;
    if (!ProjectVirtualShadowPosition(previous, previousWorld, ndc, previousUV)) return current;
    // History table halves correspond to the two separate texture layers.
    previous.PageTableOffset += (Prefilter.WriteLayer ^ 1u) * Prefilter.EntryCapacity;
    uint2 previousPixel = min(uint2(previousUV * float(previous.VirtualResolution)), previous.VirtualResolution - 1u);
    uint previousIndex = VirtualShadowPageTableIndex(previous, 0u, previousPixel / previous.PageSize);
    if ((FilterPreviousPageTable[previousIndex] & VSM_PAGE_PARTIAL_DEPTH) != 0u)
    {
        uint2 cell = (previousPixel & (previous.PageSize - 1u)) * 8u / previous.PageSize;
        uint bit = cell.y * 8u + cell.x;
        uint2 mask = FilterPreviousReceiverMasks[previousIndex];
        if (((bit < 32u ? mask.x : mask.y) & (1u << (bit & 31u))) == 0u) return current;
    }
    float factor;
    if (!VirtualShadowFilterSample(previous, true, previousUV, factor)) return current;
    return VirtualShadowPrefilterHistory(current, factor, Prefilter.HistoryWeight);
}

[shader("compute")]
[numthreads(8,8,1)]
void PrefilterVirtualShadowDistant(uint3 thread : SV_DispatchThreadID, uint3 group : SV_GroupID, uint lane : SV_GroupIndex)
{
    uint work = group.x + group.y * 65535u;
    uint selectedCount = FilterPageCount.Load(12u);
    if (Prefilter.Mode == 5u)
    {
        if (lane != 0u || work >= selectedCount) return;
        uint index = FilterPageReferences[FilterPages[work]];
        uint destination = Prefilter.WriteLayer * Prefilter.EntryCapacity + index;
        FilterPreviousPageTable[destination] = FilterPageTable[index];
        FilterPreviousReceiverMasks[destination] = FilterReceiverMasks[index];
        return;
    }
    bool border = Prefilter.Mode == 2u || Prefilter.Mode == 4u;
    uint stride = Prefilter.PageSize + (border ? 2u : 0u);
    uint tiles = (stride + 7u) / 8u;
    uint listIndex = work / (tiles * tiles);
    if (listIndex >= selectedCount) return;
    uint tile = work % (tiles * tiles);
    uint atlasPage = FilterPages[listIndex];
    uint2 pixel = uint2(tile % tiles, tile / tiles) * 8u + thread.xy % 8u;
    if (any(pixel >= stride)) return;
    uint2 atlasAddress = uint2(atlasPage % Prefilter.PagesPerRow, atlasPage / Prefilter.PagesPerRow);
    uint2 destinationPixel = atlasAddress * stride + pixel;
    VirtualShadowMapParameters view;
    uint viewIndex;
    uint2 page;
    if (!ResolveVirtualShadowFilterPage(FilterViews, FilterPageReferences, FilterPageTable,
        Prefilter.ViewCount, Prefilter.EntryCount, atlasPage, view, viewIndex, page)) return;
    if (border)
    {
        if (all(pixel > 0u) && all(pixel <= view.PageSize)) return;
        uint2 sourcePixel;
        uint source = VirtualShadowFilterNeighbor(view, page, int2(pixel) - 1, sourcePixel);
        uint2 sourcePosition = VirtualShadowFilterAtlasPixel(view, source, sourcePixel, true);
        if (Prefilter.Mode == 2u) FilterSeparate[uint3(destinationPixel, Prefilter.WriteLayer)] = FilterSeparate[uint3(sourcePosition, Prefilter.WriteLayer)];
        else FilterMerged[destinationPixel] = FilterMerged[sourcePosition];
        return;
    }
    uint2 padded = VirtualShadowFilterAtlasPixel(view, atlasPage, pixel, true);
    if (Prefilter.Mode == 3u)
    {
        float factor = FilterSeparate[uint3(padded, Prefilter.WriteLayer)];
        float2 uv = (float2(page * view.PageSize + pixel) + .5) / float(view.VirtualResolution);
        uint remaining = view.ClipmapLevelCount - uint(view.ClipmapLevel - view.FirstClipmapLevel);
        for (uint offset = 1u; offset < remaining; offset++)
        {
            VirtualShadowMapParameters coarse = FilterViews[viewIndex + offset];
            float4 transform = VirtualShadowClipmapScaleBias(view, coarse, offset);
            float coarseFactor;
            if (VirtualShadowFilterSample(coarse, false, uv * transform.x + transform.yz, coarseFactor)) factor *= coarseFactor;
        }
        FilterMerged[padded] = factor;
        return;
    }
    float samples[5];
    for (int tap = -2; tap <= 2; tap++)
    {
        uint2 neighborPixel;
        uint neighbor = VirtualShadowFilterNeighbor(view, page,
            int2(pixel) + (Prefilter.Mode == 0u ? int2(tap, 0) : int2(0, tap)), neighborPixel);
        uint2 source = VirtualShadowFilterAtlasPixel(view, neighbor, neighborPixel, false);
        float value = Prefilter.Mode == 0u
            ? VirtualShadowPrefilterOcclusion(asfloat(FilterDepthAtlas.Load(int4(source, 0, 0))), 1.0 - view.DepthCullRange.x)
            : FilterAxisX[source];
        samples[tap + 2] = value;
    }
    float factor = VirtualShadowPrefilterFiveSamples(samples[0], samples[1], samples[2], samples[3], samples[4]);
    if (Prefilter.Mode == 0u) FilterAxisX[destinationPixel] = factor;
    else FilterSeparate[uint3(padded, Prefilter.WriteLayer)] = VirtualShadowFilterPrevious(view, viewIndex, page, pixel, factor);
}
