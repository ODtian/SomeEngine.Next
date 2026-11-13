__exported import screen_binning;
#include "vsm_page_lists.slang"

struct VirtualShadowAvailablePageParameters
{
    uint MaxPages;
    uint3 Padding;
};

ConstantBuffer<VirtualShadowAvailablePageParameters> AvailablePages;

[shader("compute")]
[numthreads(256, 1, 1)]
void CountAvailableShadowPages(uint3 dispatchId : SV_DispatchThreadID)
{
    uint index = ScreenBinningThreadIndex(dispatchId);
    uint capacity = AvailablePages.MaxPages;
    if (index <= capacity)
    {
        uint count = 0u;
        if (index < capacity)
            count = ShadowPageLists[ShadowPageListStart(VSM_PAGE_LIST_LRU, capacity) + index] != 0xffffffffu ? 1u : 0u;
        ScreenBinningScanValues[index] = count;
    }
    if (index == 0u)
        ScreenBinningScanValues[capacity + 1u] = ShadowPageListCount(VSM_PAGE_LIST_EMPTY, capacity);
}

[shader("compute")]
[numthreads(256, 1, 1)]
void FillAvailableShadowPages(uint3 dispatchId : SV_DispatchThreadID)
{
    uint index = ScreenBinningThreadIndex(dispatchId);
    uint capacity = AvailablePages.MaxPages;
    uint cachedCount = ScreenBinningScanValues[capacity];
    uint emptyCount = ScreenBinningScanValues[capacity + 1u];
    uint availableStart = ShadowPageListStart(VSM_PAGE_LIST_AVAILABLE, capacity);
    if (index < capacity)
    {
        uint page = ShadowPageLists[ShadowPageListStart(VSM_PAGE_LIST_LRU, capacity) + index];
        if (page != 0xffffffffu)
            ShadowPageLists[availableStart + ScreenBinningScanValues[index]] = page;
    }
    if (index < emptyCount)
        ShadowPageLists[availableStart + cachedCount + index] =
            ShadowPageLists[ShadowPageListStart(VSM_PAGE_LIST_EMPTY, capacity) + index];
    if (index == 0u)
    {
        ShadowPageLists[ShadowPageListCountIndex(VSM_PAGE_LIST_AVAILABLE, capacity)] = cachedCount + emptyCount;
        ShadowPageLists[ShadowPageListCountIndex(VSM_PAGE_LIST_EMPTY, capacity)] = 0u;
    }
}
