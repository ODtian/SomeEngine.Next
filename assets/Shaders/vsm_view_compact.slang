#include "vsm_sampling.slang"
#include "cluster_view.slang"
#include "cluster_raster_target_invocation.slang"
#include "resource_effects.slang"

struct VirtualShadowViewCompactUniforms
{
    uint ViewCount;
    uint ActivePageCapacity;
    uint PageSize;
    uint PageHiZMipCount;
    uint PhysicalPagesPerRow;
    uint InstanceGroupCount;
    uint HasPreviousHistory;
    uint Pad0;
};

ConstantBuffer<VirtualShadowViewCompactUniforms> ViewCompact;
StructuredBuffer<uint2> ActivePhysicalPages;
StructuredBuffer<uint> ActivePhysicalPageCount;
StructuredBuffer<VirtualShadowMapParameters> ShadowViews;
StructuredBuffer<VirtualShadowMapParameters> PreviousShadowViews;
StructuredBuffer<uint4> PreviousShadowPageTable;
StructuredBuffer<ClusterViewUniforms> ClusterSourceViews;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWStructuredBuffer<ClusterViewUniforms> RasterInvocationViews;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWStructuredBuffer<ClusterRasterTargetInvocation> RasterTargetInvocations;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)]
RWStructuredBuffer<uint> RasterRegionGrid;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWByteAddressBuffer ClusterInvocationDispatchArgs;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWByteAddressBuffer ShadowPageWorkDispatchArgs;

int2 ResolveInvocationFineWorldPage(
    uint tableIndex,
    VirtualShadowMapParameters view)
{
    uint dimension = VirtualShadowPagesPerRow(view);
    uint mask = dimension - 1u;
    uint localIndex = tableIndex - view.PageTableOffset;
    uint2 slot = uint2(localIndex % dimension, localIndex / dimension);
    uint2 originSlot = uint2(
        asuint(view.VirtualPageOriginX) & mask,
        asuint(view.VirtualPageOriginY) & mask);
    return int2(view.VirtualPageOriginX, view.VirtualPageOriginY) +
        int2((slot - originSlot) & mask);
}

int2 ResolveInvocationCoarseWorldPage(
    uint tableIndex,
    VirtualShadowMapParameters view)
{
    uint dimension = VirtualShadowCoarsePagesPerRow(view);
    uint mask = dimension - 1u;
    uint localIndex = tableIndex - view.CoarsePageTableOffset;
    uint2 slot = uint2(localIndex % dimension, localIndex / dimension);
    int scale = int(1u << view.CoarsePageShift);
    int2 origin = int2(
        VirtualShadowFloorDiv(view.VirtualPageOriginX, scale),
        VirtualShadowFloorDiv(view.VirtualPageOriginY, scale));
    uint2 originSlot = uint2(asuint(origin.x) & mask, asuint(origin.y) & mask);
    return origin + int2((slot - originSlot) & mask);
}

float4x4 BuildPageCrop(
    VirtualShadowMapParameters view,
    int2 worldPage,
    uint pageScale)
{
    float pagesPerRow = float(VirtualShadowPagesPerRow(view));
    float scale = pagesPerRow / float(pageScale);
    float2 origin = float2(view.VirtualPageOriginX, view.VirtualPageOriginY) /
        float(pageScale);
    float2 page = float2(worldPage);
    return float4x4(
        scale, 0.0, 0.0, 0.0,
        0.0, scale, 0.0, 0.0,
        0.0, 0.0, 1.0, 0.0,
        scale - 1.0 + 2.0 * origin.x - 2.0 * page.x,
        1.0 - scale - 2.0 * origin.y + 2.0 * page.y,
        0.0,
        1.0);
}

bool ResolvePreviousLayer(
    uint tableIndex,
    int2 expectedWorldPage,
    out uint layer)
{
    if (ViewCompact.HasPreviousHistory == 0u)
    {
        layer = CLUSTER_INVALID_HIZ_LAYER;
        return false;
    }
    uint4 entry = PreviousShadowPageTable[tableIndex];
    if (!IsVirtualShadowPageTag(entry, expectedWorldPage) ||
        entry.x == VSM_PAGE_UNMAPPED ||
        entry.x == VSM_PAGE_LOCKED)
    {
        layer = CLUSTER_INVALID_HIZ_LAYER;
        return false;
    }
    return DecodeVirtualShadowPage(entry.x, layer);
}

uint RasterGridWordCount(uint dimension)
{
    return dimension * dimension + dimension * ((dimension + 31u) / 32u);
}

float4x4 BuildRasterGridCrop(
    VirtualShadowMapParameters source, int2 origin, uint pageScale, uint dimension)
{
    float4x4 crop = BuildPageCrop(source, origin, pageScale);
    crop[0][0] /= float(dimension);
    crop[1][1] /= float(dimension);
    crop[3][0] = (crop[3][0] + 1.0) / float(dimension) - 1.0;
    crop[3][1] = (crop[3][1] - 1.0) / float(dimension) + 1.0;
    return crop;
}

uint RasterGridOffset(uint sourceViewIndex, bool coarse, uint fineDimension, uint coarseDimension)
{
    return coarse
        ? ViewCompact.ViewCount * RasterGridWordCount(fineDimension) +
            sourceViewIndex * RasterGridWordCount(coarseDimension)
        : sourceViewIndex * RasterGridWordCount(fineDimension);
}

int2 RasterGridOrigin(VirtualShadowMapParameters source, bool coarse)
{
    if (!coarse)
        return int2(source.VirtualPageOriginX, source.VirtualPageOriginY);
    int scale = int(1u << source.CoarsePageShift);
    return int2(VirtualShadowFloorDiv(source.VirtualPageOriginX, scale),
        VirtualShadowFloorDiv(source.VirtualPageOriginY, scale));
}

[shader("compute")]
[numthreads(64, 1, 1)]
void CompactVirtualShadowViews(uint3 threadId : SV_DispatchThreadID)
{
    uint count = min(ActivePhysicalPageCount[0], ViewCompact.ActivePageCapacity);
    uint index = threadId.x;
    VirtualShadowMapParameters firstView = ShadowViews[0];
    uint fineDimension = VirtualShadowPagesPerRow(firstView);
    uint coarseDimension = VirtualShadowCoarsePagesPerRow(firstView);

    // Only two shared views per light/clipmap: fine and coarse LOD selection.
    // Align previous projection to the current world grid, so a region uses one crop.
    if (index < 2u * ViewCompact.ViewCount)
    {
        bool coarse = index >= ViewCompact.ViewCount;
        uint sourceIndex = index % ViewCompact.ViewCount;
        VirtualShadowMapParameters source = ShadowViews[sourceIndex];
        VirtualShadowMapParameters previous = PreviousShadowViews[sourceIndex];
        uint dimension = coarse ? coarseDimension : fineDimension;
        uint pageScale = coarse ? 1u << source.CoarsePageShift : 1u;
        int2 origin = RasterGridOrigin(source, coarse);
        float4x4 crop = BuildRasterGridCrop(source, origin, pageScale, dimension);
        float4x4 previousCrop = BuildRasterGridCrop(previous, origin, pageScale, dimension);
        ClusterViewUniforms view = ClusterSourceViews[sourceIndex];
        view.ViewProj = mul(view.ViewProj, crop);
        view.PrevViewProj = mul(view.PrevViewProj, previousCrop);
        float4x4 inverseCrop = float4x4(
            1.0 / crop[0][0], 0.0, 0.0, 0.0,
            0.0, 1.0 / crop[1][1], 0.0, 0.0,
            0.0, 0.0, 1.0, 0.0,
            -crop[3][0] / crop[0][0], -crop[3][1] / crop[1][1], 0.0, 1.0);
        view.ClipToWorld = mul(inverseCrop, view.ClipToWorld);
        view.P00 *= crop[0][0];
        view.P11 *= crop[1][1];
        view.PrevP00 *= previousCrop[0][0];
        view.PrevP11 *= previousCrop[1][1];
        view.ScreenWidth = ViewCompact.PageSize * dimension;
        view.ScreenHeight = ViewCompact.PageSize * dimension;
        view.HiZMipCount = ViewCompact.PageHiZMipCount;
        view.HiZInvSize = 1.0.xx / float(max(ViewCompact.PageSize / 2u, 1u) * dimension);
        view.TargetPolicy = 1u;
        view.ViewPadding = 0u;
        view.Pad5 = RasterGridOffset(sourceIndex, coarse, fineDimension, coarseDimension);
        view.Pad6 = dimension;
        view.TargetViewIndex = view.Pad5 + dimension * dimension;
        view.HasPrevHistory = ViewCompact.HasPreviousHistory;
        RasterInvocationViews[index] = view;
    }

    if (index < count)
    {
        uint2 active = ActivePhysicalPages[index];
        uint physicalPage = active.x;
        uint tableIndex = active.y;
        uint finePageCount = fineDimension * fineDimension * ViewCompact.ViewCount;
        bool coarse = tableIndex >= finePageCount;
        uint sourceIndex = coarse
            ? (tableIndex - finePageCount) / (coarseDimension * coarseDimension)
            : tableIndex / (fineDimension * fineDimension);
        VirtualShadowMapParameters source = ShadowViews[sourceIndex];
        int2 worldPage = coarse
            ? ResolveInvocationCoarseWorldPage(tableIndex, source)
            : ResolveInvocationFineWorldPage(tableIndex, source);
        uint dimension = coarse ? coarseDimension : fineDimension;
        uint2 localPage = uint2(worldPage - RasterGridOrigin(source, coarse));
        uint previousLayer;
        ResolvePreviousLayer(tableIndex, worldPage, previousLayer);

        ClusterRasterTargetInvocation region;
        region.OutputOrigin = uint2(physicalPage % ViewCompact.PhysicalPagesPerRow,
            physicalPage / ViewCompact.PhysicalPagesPerRow) * ViewCompact.PageSize;
        region.OutputExtent = ViewCompact.PageSize.xx;
        region.CurrentHiZLayer = physicalPage;
        region.PreviousHiZLayer = previousLayer;
        region.SourceViewIndex = sourceIndex + (coarse ? ViewCompact.ViewCount : 0u);
        region.Flags = coarse ? 1u : 0u;
        region.Crop = float4(float(dimension).xx,
            float(dimension) - 1.0 - 2.0 * float(localPage.x),
            1.0 - float(dimension) + 2.0 * float(localPage.y));
        RasterTargetInvocations[index] = region;
        uint gridOffset = RasterGridOffset(sourceIndex, coarse, fineDimension, coarseDimension);
        RasterRegionGrid[gridOffset + localPage.y * dimension + localPage.x] = index + 1u;
        uint maskOffset = gridOffset + dimension * dimension +
            localPage.y * ((dimension + 31u) / 32u) + localPage.x / 32u;
        InterlockedOr(RasterRegionGrid[maskOffset], 1u << (localPage.x & 31u));
    }

    if (index != 0u)
        return;
    ClusterInvocationDispatchArgs.Store(0u, count != 0u ? ViewCompact.InstanceGroupCount : 0u);
    ClusterInvocationDispatchArgs.Store(4u, (2u * ViewCompact.ViewCount + 31u) / 32u);
    ClusterInvocationDispatchArgs.Store(8u, 1u);
    ClusterInvocationDispatchArgs.Store(12u, 2u * ViewCompact.ViewCount);
    for (uint mip = 0u; mip < ViewCompact.PageHiZMipCount; mip++)
    {
        uint mipSize = max(max(ViewCompact.PageSize / 2u, 1u) >> mip, 1u);
        uint tiles = (mipSize + 7u) / 8u;
        uint offset = mip * 16u;
        ShadowPageWorkDispatchArgs.Store(offset, count);
        ShadowPageWorkDispatchArgs.Store(offset + 4u, tiles);
        ShadowPageWorkDispatchArgs.Store(offset + 8u, tiles);
        ShadowPageWorkDispatchArgs.Store(offset + 12u, 0u);
    }
}
