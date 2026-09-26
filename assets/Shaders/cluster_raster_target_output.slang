#ifndef CLUSTER_RASTER_TARGET_OUTPUT_SLANG
#define CLUSTER_RASTER_TARGET_OUTPUT_SLANG

#include "cluster_raster_target_view.slang"

StructuredBuffer<ClusterRasterTargetView> RasterTargetViews;
StructuredBuffer<uint> RasterTargetPageHierarchy;
StructuredBuffer<uint4> RasterTargetPageTable;
StructuredBuffer<uint2> RasterTargetReceiverMasks;
RWTexture2D<uint> RasterTargetOutput;

static const uint CLUSTER_RASTER_TARGET_PAGE_CACHE_DIM = 8u;
static const uint CLUSTER_RASTER_TARGET_PAGE_CACHE_COUNT =
    CLUSTER_RASTER_TARGET_PAGE_CACHE_DIM * CLUSTER_RASTER_TARGET_PAGE_CACHE_DIM;
static const uint CLUSTER_RASTER_TARGET_INVALID_PAGE = 0xFFFFFFFFu;

groupshared uint ClusterRasterTargetCachedPages[CLUSTER_RASTER_TARGET_PAGE_CACHE_COUNT];
groupshared uint2 ClusterRasterTargetCachedReceiverMasks[CLUSTER_RASTER_TARGET_PAGE_CACHE_COUNT];
groupshared uint2 ClusterRasterTargetCachedOrigin;
groupshared uint2 ClusterRasterTargetCachedExtent;
groupshared uint ClusterRasterTargetPageCacheEnabled;

bool ClusterRasterTargetPageRequested(
    ClusterRasterTargetView target,
    uint2 localPage)
{
    uint word = localPage.x / 32u;
    uint bit = 1u << (localPage.x & 31u);
    return (RasterTargetPageHierarchy[
        ClusterRasterTargetHierarchyIndex(
            target, 0u, localPage.y, word)] & bit) != 0u;
}

bool ClusterRasterTargetReceiverAccepts(
    ClusterRasterTargetView target,
    uint tableIndex,
    uint2 pagePixel)
{
    if (target.ReceiverMaskEnabled == 0u)
        return true;

    uint2 mask = RasterTargetReceiverMasks[tableIndex];
    uint2 cell = min(
        pagePixel * 8u / max(target.PageSize, 1u),
        7u.xx);
    uint bit = cell.y * 8u + cell.x;
    return bit < 32u
        ? (mask.x & (1u << bit)) != 0u
        : (mask.y & (1u << (bit - 32u))) != 0u;
}

bool ClusterRasterTargetCachedReceiverAccepts(
    ClusterRasterTargetView target,
    uint2 receiverMask,
    uint2 pagePixel)
{
    if (target.ReceiverMaskEnabled == 0u)
        return true;

    uint2 cell = min(
        pagePixel * 8u / max(target.PageSize, 1u),
        7u.xx);
    uint bit = cell.y * 8u + cell.x;
    return bit < 32u
        ? (receiverMask.x & (1u << bit)) != 0u
        : (receiverMask.y & (1u << (bit - 32u))) != 0u;
}

bool ClusterRasterTargetResolvePage(
    ClusterRasterTargetView target,
    uint2 localPage,
    out uint tableIndex,
    out uint page)
{
    tableIndex = ClusterRasterTargetTableIndex(target, localPage);
    uint4 entry = RasterTargetPageTable[tableIndex];
    int2 expectedWorldPage = ClusterRasterTargetWorldPage(target, localPage);
    if (entry.x == 0u ||
        entry.x == 0xFFFFFFFFu ||
        entry.w != target.CacheGeneration ||
        asint(entry.y) != expectedWorldPage.x ||
        asint(entry.z) != expectedWorldPage.y)
    {
        page = CLUSTER_RASTER_TARGET_INVALID_PAGE;
        return false;
    }
    page = entry.x - 1u;
    return true;
}

int4 PrepareRasterTargetScissor(
    uint visibleClusterIndex,
    uint targetViewIndex,
    uint screenWidth,
    uint screenHeight)
{
    ClusterRasterTargetView target = RasterTargetViews[targetViewIndex];
    uint4 pageWindow = ClusterUnpackRasterTargetPageWindow(
        VisibleClusterRasterWindows[visibleClusterIndex]);
    uint2 extent = pageWindow.zw - pageWindow.xy + 1u;
    bool cacheEnabled = all(extent <= CLUSTER_RASTER_TARGET_PAGE_CACHE_DIM.xx);

    uint lane = WaveGetLaneIndex();
    if (lane == 0u)
    {
        ClusterRasterTargetCachedOrigin = pageWindow.xy;
        ClusterRasterTargetCachedExtent = extent;
        ClusterRasterTargetPageCacheEnabled = cacheEnabled ? 1u : 0u;
    }

    if (cacheEnabled)
    {
        uint laneCount = WaveGetLaneCount();
        for (uint index = lane; index < CLUSTER_RASTER_TARGET_PAGE_CACHE_COUNT; index += laneCount)
        {
            uint2 cacheCoord = uint2(
                index & (CLUSTER_RASTER_TARGET_PAGE_CACHE_DIM - 1u),
                index / CLUSTER_RASTER_TARGET_PAGE_CACHE_DIM);
            bool inWindow = all(cacheCoord < extent);
            uint page = CLUSTER_RASTER_TARGET_INVALID_PAGE;
            uint2 receiverMask = 0u.xx;
            if (inWindow)
            {
                uint2 localPage = pageWindow.xy + cacheCoord;
                if (ClusterRasterTargetPageRequested(target, localPage))
                {
                    uint tableIndex;
                    if (ClusterRasterTargetResolvePage(target, localPage, tableIndex, page))
                    {
                        receiverMask = target.ReceiverMaskEnabled != 0u
                            ? RasterTargetReceiverMasks[tableIndex]
                            : 0xFFFFFFFFu.xx;
                    }
                }
            }
            ClusterRasterTargetCachedPages[index] = page;
            ClusterRasterTargetCachedReceiverMasks[index] = receiverMask;
        }
    }
    GroupMemoryBarrierWithGroupSync();

    uint2 firstPixel = pageWindow.xy * target.PageSize;
    uint2 lastPixelExclusive = min(
        (pageWindow.zw + 1u) * target.PageSize,
        max(target.MipResolution, 1u).xx);
    return int4(firstPixel, lastPixelExclusive);
}

bool WriteCachedRasterTargetDepth(
    int2 pixel,
    uint targetViewIndex,
    float depth)
{
    if (ClusterRasterTargetPageCacheEnabled == 0u)
        return false;

    ClusterRasterTargetView target = RasterTargetViews[targetViewIndex];
    uint2 virtualPixel = uint2(pixel);
    uint2 localPage = virtualPixel / target.PageSize;
    if (any(localPage < ClusterRasterTargetCachedOrigin))
        return true;
    uint2 cacheCoord = localPage - ClusterRasterTargetCachedOrigin;
    if (any(cacheCoord >= ClusterRasterTargetCachedExtent))
        return true;
    uint cacheIndex =
        cacheCoord.y * CLUSTER_RASTER_TARGET_PAGE_CACHE_DIM + cacheCoord.x;
    uint page = ClusterRasterTargetCachedPages[cacheIndex];
    if (page == CLUSTER_RASTER_TARGET_INVALID_PAGE)
        return true;

    uint2 pagePixel = virtualPixel & (target.PageSize - 1u);
    if (!ClusterRasterTargetCachedReceiverAccepts(
            target,
            ClusterRasterTargetCachedReceiverMasks[cacheIndex],
            pagePixel))
        return true;

    uint2 pageCoord = uint2(
        page % target.PhysicalPagesPerRow,
        page / target.PhysicalPagesPerRow);
    uint2 targetPixel = pageCoord * target.PageSize + pagePixel;
    uint encodedDepth = asuint(1.0 - saturate(depth));
    uint ignored;
    InterlockedMax(RasterTargetOutput[targetPixel], encodedDepth, ignored);
    return true;
}

void WriteRasterTargetDepth(int2 pixel, uint targetViewIndex, float depth)
{
    ClusterRasterTargetView target = RasterTargetViews[targetViewIndex];
    if (any(pixel < 0) ||
        any(uint2(pixel) >= max(target.MipResolution, 1u).xx))
    {
        return;
    }

    uint2 virtualPixel = uint2(pixel);
    uint2 localPage = virtualPixel / target.PageSize;
    if (!ClusterRasterTargetPageRequested(target, localPage))
        return;

    uint tableIndex;
    uint page;
    if (!ClusterRasterTargetResolvePage(target, localPage, tableIndex, page))
        return;

    uint2 pagePixel = virtualPixel & (target.PageSize - 1u);
    if (!ClusterRasterTargetReceiverAccepts(target, tableIndex, pagePixel))
        return;

    uint2 pageCoord = uint2(
        page % target.PhysicalPagesPerRow,
        page / target.PhysicalPagesPerRow);
    uint2 targetPixel = pageCoord * target.PageSize + pagePixel;

    uint encodedDepth = asuint(1.0 - saturate(depth));
    uint ignored;
    InterlockedMax(RasterTargetOutput[targetPixel], encodedDepth, ignored);
}

#endif
