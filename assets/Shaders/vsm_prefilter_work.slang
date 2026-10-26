#define VSM_WHOLE_PAGE_SAMPLING 1
#include "vsm_sampling.slang"
#include "vsm_prefilter_common.slang"
#include "resource_effects.slang"

ConstantBuffer<VirtualShadowPrefilterUniforms> Prefilter;
StructuredBuffer<VirtualShadowMapParameters> FilterViews;
StructuredBuffer<uint> FilterPageReferences;
StructuredBuffer<uint> FilterPageTable;
StructuredBuffer<uint> FilterRequestFlags;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.None)]
RWStructuredBuffer<uint> FilterPreviousPageTable;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWStructuredBuffer<uint> SelectedFilterPages;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWByteAddressBuffer FilterArguments;

[shader("compute")]
[numthreads(64,1,1)]
void ResetVirtualShadowFilterWork(uint3 group : SV_GroupID, uint lane : SV_GroupIndex)
{
    uint index = (group.x + group.y * 65535u) * 64u + lane;
    if (index < Prefilter.EntryCapacity)
        FilterPreviousPageTable[Prefilter.WriteLayer * Prefilter.EntryCapacity + index] = 0u;
    if (index < 6u)
    {
        FilterArguments.Store4(index * 16u, uint4(0u, 1u, 1u, 0u));
    }
    return;
}

[shader("compute")]
[numthreads(64,1,1)]
void SelectVirtualShadowFilterPages(uint3 group : SV_GroupID, uint lane : SV_GroupIndex)
{
    uint atlasPage = group.x * 64u + lane;
    uint capacity, dataStride;
    FilterPageReferences.GetDimensions(capacity, dataStride);
    if (atlasPage >= capacity) return;
    VirtualShadowMapParameters view;
    uint viewIndex;
    uint2 page;
    if (!ResolveVirtualShadowFilterPage(FilterViews, FilterPageReferences, FilterPageTable,
        Prefilter.ViewCount, Prefilter.EntryCount, atlasPage, view, viewIndex, page)) return;
    uint tableIndex = FilterPageReferences[atlasPage];
    if ((FilterRequestFlags[tableIndex] & VSM_PAGE_REQUEST_SECONDARY) == 0u) return;
    uint listIndex;
    FilterArguments.InterlockedAdd(12u, 1u, listIndex);
    SelectedFilterPages[listIndex] = atlasPage;
    for (uint mode = 0u; mode < 6u; mode++)
    {
        uint stride = mode == 2u || mode == 4u ? Prefilter.PageSize + 2u : Prefilter.PageSize;
        uint tiles = (stride + 7u) / 8u;
        uint groups = (listIndex + 1u) * (mode == 5u ? 1u : tiles * tiles);
        uint ignored;
        FilterArguments.InterlockedMax(mode * 16u, min(groups, 65535u), ignored);
        FilterArguments.InterlockedMax(mode * 16u + 4u, (groups + 65534u) / 65535u, ignored);
    }
    return;
}
