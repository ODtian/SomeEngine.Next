#ifndef VSM_PREFILTER_SAMPLING_SLANG
#define VSM_PREFILTER_SAMPLING_SLANG

bool TrySampleVirtualShadowPrefiltered(VirtualShadowMapParameters view, StructuredBuffer<uint> pageTable,
    Texture2D<float> filteredAtlas, float2 uv, out float visibility)
{
    visibility = 1.0;
    if (any(uv < 0.0) || any(uv >= 1.0)) return false;
    float2 texel = uv * float(view.VirtualResolution) - .5;
    uint2 center = min(uint2(uv * float(view.VirtualResolution)), view.VirtualResolution - 1u);
    uint2 virtualPage = center / view.PageSize;
    uint entry = pageTable[VirtualShadowPageTableIndex(view, 0u, virtualPage)];
    uint atlasPage;
    if (!IsVirtualShadowPageTag(entry, VirtualShadowWorldPage(view, 0u, virtualPage)) ||
        !VirtualShadowPageHasOwnDepth(entry) ||
        !DecodeVirtualShadowPage(entry, view.PhysicalPagesPerRow, atlasPage)) return false;
    uint2 atlasBase = uint2(atlasPage % view.PhysicalPagesPerRow, atlasPage / view.PhysicalPagesPerRow) * (view.PageSize + 2u);
    int2 base = int2(floor(texel)) - int2(virtualPage * view.PageSize) + int2(atlasBase) + 1;
    float2 fraction = frac(texel);
    float top = lerp(filteredAtlas.Load(int3(base, 0)), filteredAtlas.Load(int3(base + int2(1, 0), 0)), fraction.x);
    float bottom = lerp(filteredAtlas.Load(int3(base + int2(0, 1), 0)), filteredAtlas.Load(int3(base + 1, 0)), fraction.x);
    visibility = lerp(top, bottom, fraction.y);
    return true;
}

struct VirtualShadowPrefilterResult
{
    bool Valid;
    float Visibility;
    float OccluderDistance;
    uint ViewIndex;
};

VirtualShadowPrefilterResult SampleDirectionalVirtualShadowPrefiltered(
    StructuredBuffer<VirtualShadowMapParameters> views, StructuredBuffer<uint> pageTable,
    Texture2D<float> filteredAtlas, uint firstView, uint viewCount, float3 worldPosition)
{
    VirtualShadowPrefilterResult result = (VirtualShadowPrefilterResult)0;
    result.Visibility = 1.0;
    result.OccluderDistance = -1.0;
    if (viewCount == 0u || (views[firstView].Flags & VSM_VIEW_PREFILTERED_DISTANT) == 0u) return result;
    uint level = SelectDirectionalVirtualShadowView(views, firstView, viewCount,
        worldPosition, worldPosition, worldPosition, 0.0) + 1u;
    if (level >= viewCount) return result;
    VirtualShadowMapParameters view = views[firstView + level];
    float3 ndc;
    float2 uv;
    if (ProjectVirtualShadowPosition(view, worldPosition, ndc, uv) &&
        TrySampleVirtualShadowPrefiltered(view, pageTable, filteredAtlas, uv, result.Visibility))
    {
        result.Valid = true;
        result.ViewIndex = firstView + level;
        result.OccluderDistance = max(1e-6 * view.WorldUnitsPerCentimeter,
            (view.DepthCullRange.y - view.DepthCullRange.x) / max(abs(view.ProjectionDepthScale), 1e-20));
    }
    return result;
}

#endif
