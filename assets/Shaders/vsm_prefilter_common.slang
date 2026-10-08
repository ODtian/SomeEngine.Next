#ifndef VSM_PREFILTER_COMMON_SLANG
#define VSM_PREFILTER_COMMON_SLANG

struct VirtualShadowPrefilterUniforms
{
    uint ViewCount;
    uint EntriesPerView;
    uint EntryCapacity;
    uint FrameIndex;
    uint PageSize;
    uint PagesPerRow;
    uint Mode;
    uint WriteLayer;
    uint HasHistory;
    uint PreviousViewCount;
    float HistoryWeight;
    uint EntryCount;
};
struct VirtualShadowPrefilterProjection
{
    float4x4 ShadowToWorld;
    uint PreviousFirstView;
    uint Pad0;
    uint Pad1;
    uint Pad2;
};
bool ResolveVirtualShadowFilterPage(StructuredBuffer<VirtualShadowMapParameters> views,
    StructuredBuffer<uint> pageReferences, StructuredBuffer<uint> pageTable, uint viewCount, uint entryCount, uint atlasPage, out VirtualShadowMapParameters view, out uint viewIndex, out uint2 page)
{
    view = (VirtualShadowMapParameters)0;
    page = 0u.xx;
    viewIndex = 0u;
    uint index = pageReferences[atlasPage];
    if (index >= entryCount) return false;
    viewIndex = VirtualShadowViewFromPageTableIndex(views, viewCount, index);
    view = views[viewIndex];
    uint mip;
    uint entry = pageTable[index];
    return (view.Flags & VSM_VIEW_PREFILTERED_DISTANT) != 0u &&
        VirtualShadowDecodePageTableIndex(view, index, mip, page) && mip == 0u &&
        (entry & VSM_PAGE_ANY_DEPTH) != 0u && IsVirtualShadowPageTag(entry, VirtualShadowWorldPage(view, 0u, page)) &&
        VirtualShadowPageHasOwnDepth(entry);
}


#endif
