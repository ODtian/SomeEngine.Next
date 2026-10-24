#ifndef VSM_PAGE_LISTS_SLANG
#define VSM_PAGE_LISTS_SLANG

#include "resource_effects.slang"

static const uint VSM_PAGE_LIST_LRU = 0u;
static const uint VSM_PAGE_LIST_AVAILABLE = 1u;
static const uint VSM_PAGE_LIST_EMPTY = 2u;
static const uint VSM_PAGE_LIST_REQUESTED = 3u;
static const uint VSM_PAGE_LIST_COUNT = 4u;

#ifdef VSM_PAGE_LIST_READ_ONLY
StructuredBuffer<uint> ShadowPageLists;
#else
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWStructuredBuffer<uint> ShadowPageLists;
#endif

uint ShadowPageListStart(uint list, uint capacity)
{
    return list * (capacity + 1u);
}

uint ShadowPageListCountIndex(uint list, uint capacity)
{
    return ShadowPageListStart(list, capacity) + capacity;
}

uint ShadowPageListCount(uint list, uint capacity)
{
    return ShadowPageLists[ShadowPageListCountIndex(list, capacity)];
}

#ifndef VSM_PAGE_LIST_READ_ONLY
void AppendShadowPage(uint list, uint capacity, uint page)
{
    uint index;
    InterlockedAdd(ShadowPageLists[ShadowPageListCountIndex(list, capacity)], 1u, index);
    if (index < capacity)
        ShadowPageLists[ShadowPageListStart(list, capacity) + index] = page;
}
#endif

#endif
