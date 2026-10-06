#ifndef CLUSTER_RASTER_TARGET_OUTPUT_SLANG
#define CLUSTER_RASTER_TARGET_OUTPUT_SLANG

#include "cluster_raster_target_view.slang"

#include "cluster_raster_target_resources.slang"
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

int4 PrepareRasterTargetScissor(
    uint2 rasterWindow,
    uint targetViewIndex,
    uint screenWidth,
    uint screenHeight,
    uint threadIndex,
    uint threadCount)
{
    ClusterRasterTargetView target = RasterTargetViews[targetViewIndex];
    uint4 pageWindow = ClusterUnpackRasterTargetPageWindow(
        rasterWindow);
    uint2 extent = pageWindow.zw - pageWindow.xy + 1u;
    bool cacheEnabled = all(extent <= CLUSTER_RASTER_TARGET_PAGE_CACHE_DIM.xx);

    if (threadIndex == 0u)
    {
        ClusterRasterTargetCachedOrigin = pageWindow.xy;
        ClusterRasterTargetCachedExtent = extent;
        ClusterRasterTargetPageCacheEnabled = cacheEnabled ? 1u : 0u;
    }

    if (cacheEnabled)
    {
        uint pageCount = extent.x * extent.y;
        for (uint pageIndex = threadIndex; pageIndex < pageCount; pageIndex += threadCount)
        {
            uint2 cacheCoord = uint2(
                pageIndex % extent.x,
                pageIndex / extent.x);
            uint cacheIndex = cacheCoord.y * CLUSTER_RASTER_TARGET_PAGE_CACHE_DIM + cacheCoord.x;
            uint page = CLUSTER_RASTER_TARGET_INVALID_PAGE;
            uint2 receiverMask = 0u.xx;
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
            ClusterRasterTargetCachedPages[cacheIndex] = page;
            ClusterRasterTargetCachedReceiverMasks[cacheIndex] = receiverMask;
        }
    }
    GroupMemoryBarrierWithGroupSync();

    return RasterTargetWindowScissor(pageWindow, target);
}

int4 RasterTargetWindowScissor(uint4 pageWindow, ClusterRasterTargetView target)
{
    uint2 firstPixel = pageWindow.xy * target.PageSize;
    uint2 lastPixelExclusive = min((pageWindow.zw + 1u) * target.PageSize, max(target.MipResolution, 1u).xx);
    return int4(firstPixel, lastPixelExclusive);
}

int4 PrepareRasterTargetScissor(uint visibleClusterIndex, uint targetViewIndex,
    uint screenWidth, uint screenHeight, uint threadIndex, uint threadCount)
{
    return PrepareRasterTargetScissor(VisibleClusterRasterWindows[visibleClusterIndex], targetViewIndex,
        screenWidth, screenHeight, threadIndex, threadCount);
}

int4 PrepareRasterTargetScissor(
    uint visibleClusterIndex,
    uint targetViewIndex,
    uint screenWidth,
    uint screenHeight)
{
    return PrepareRasterTargetScissor(visibleClusterIndex, targetViewIndex,
        screenWidth, screenHeight, WaveGetLaneIndex(), WaveGetLaneCount());
}

bool ClusterRasterTargetCachedBoundsAccepts(
    float2 minimumPixel,
    float2 maximumPixel,
    uint targetViewIndex,
    float2 windowSize)
{
    if (ClusterRasterTargetPageCacheEnabled == 0u) return true;
    ClusterRasterTargetView target = RasterTargetViews[targetViewIndex];
    // Include neighboring pixels at quantized raster edges. Pixel evaluation
    // still performs the final request and receiver tests before writing depth.
    uint2 firstPixel = uint2(clamp(floor(minimumPixel) - 1.0, 0.0.xx, windowSize - 1.0));
    uint2 lastPixel = uint2(clamp(ceil(maximumPixel) + 1.0, 0.0.xx, windowSize - 1.0));
    uint2 firstPage = firstPixel / target.PageSize;
    uint2 lastPage = lastPixel / target.PageSize;
    for (uint row = firstPage.y; row <= lastPage.y; row++)
    for (uint column = firstPage.x; column <= lastPage.x; column++)
    {
        uint index = row * CLUSTER_RASTER_TARGET_PAGE_CACHE_DIM + column;
        if (ClusterRasterTargetCachedPages[index] == CLUSTER_RASTER_TARGET_INVALID_PAGE) continue;
        if (target.ReceiverMaskEnabled == 0u) return true;
        uint2 pageOrigin = uint2(column, row) * target.PageSize;
        uint2 pageFirst = firstPixel - min(firstPixel, pageOrigin);
        uint2 pageLast = min(lastPixel - pageOrigin, target.PageSize - 1u);
        uint2 firstCell = min(pageFirst * 8u / target.PageSize, 7u.xx);
        uint2 lastCell = min(pageLast * 8u / target.PageSize, 7u.xx);
        if (ClusterRasterReceiverRectangleAccepts(
                ClusterRasterTargetCachedReceiverMasks[index], firstCell, lastCell)) return true;
    }
    return false;
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

bool ResolveRasterTargetPixel(int2 pixel, uint targetViewIndex, out uint2 targetPixel)
{
    targetPixel = 0u.xx;
    ClusterRasterTargetView target = RasterTargetViews[targetViewIndex];
    if (any(pixel < 0) ||
        any(uint2(pixel) >= max(target.MipResolution, 1u).xx))
    {
        return false;
    }

    uint2 virtualPixel = uint2(pixel);
    uint2 localPage = virtualPixel / target.PageSize;
    if (!ClusterRasterTargetPageRequested(target, localPage))
        return false;

    uint tableIndex;
    uint page;
    if (!ClusterRasterTargetResolvePage(target, localPage, tableIndex, page))
        return false;

    uint2 pagePixel = virtualPixel & (target.PageSize - 1u);
    if (!ClusterRasterTargetReceiverAccepts(target, tableIndex, pagePixel))
        return false;

    uint2 pageCoord = uint2(
        page % target.PhysicalPagesPerRow,
        page / target.PhysicalPagesPerRow);
    targetPixel = pageCoord * target.PageSize + pagePixel;
    return true;
}

void WriteRasterTargetPixelDepth(uint2 targetPixel, float depth)
{
    uint encodedDepth = asuint(1.0 - saturate(depth));
    uint ignored;
    InterlockedMax(RasterTargetOutput[targetPixel], encodedDepth, ignored);
}

void WriteRasterTargetDepth(int2 pixel, uint targetViewIndex, float depth)
{
    uint2 targetPixel;
    if (ResolveRasterTargetPixel(pixel, targetViewIndex, targetPixel))
        WriteRasterTargetPixelDepth(targetPixel, depth);
}

#endif
