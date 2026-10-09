#include "vsm_page_entry.slang"
#ifndef CLUSTER_RASTER_TARGET_OUTPUT_SLANG
#define CLUSTER_RASTER_TARGET_OUTPUT_SLANG

#include "cluster_raster_target_view.slang"

#include "cluster_raster_target_resources.slang"
StructuredBuffer<VisibleCluster> RasterOutputVisibleClusters;
RWTexture2DArray<uint> RasterTargetOutput;
#ifdef CLUSTER_TARGET_DEPTH_CACHE
#ifndef CLUSTER_RASTER_TARGET_METADATA_DECLARED
#define CLUSTER_RASTER_TARGET_METADATA_DECLARED 1
StructuredBuffer<VirtualShadowPageMetadata> RasterTargetPageMetadata;
#endif
#endif

static const uint CLUSTER_RASTER_TARGET_PAGE_CACHE_DIM = 8u;
static const uint CLUSTER_RASTER_TARGET_PAGE_CACHE_COUNT =
    CLUSTER_RASTER_TARGET_PAGE_CACHE_DIM * CLUSTER_RASTER_TARGET_PAGE_CACHE_DIM;
static const uint CLUSTER_RASTER_TARGET_INVALID_PAGE = 0xFFFFFFFFu;

groupshared uint ClusterRasterTargetCachedPages[CLUSTER_RASTER_TARGET_PAGE_CACHE_COUNT];
groupshared uint4 ClusterRasterTargetCachedReceiverMasks[CLUSTER_RASTER_TARGET_PAGE_CACHE_COUNT];
groupshared uint2 ClusterRasterTargetCachedOrigin;
groupshared uint2 ClusterRasterTargetCachedExtent;
groupshared uint ClusterRasterTargetPageCacheEnabled;

bool ClusterRasterTargetPageRequested(
    ClusterRasterTargetView target,
    uint2 localPage, uint depthCacheClass = 2u)
{
    uint word = localPage.x / 32u;
    uint bit = 1u << (localPage.x & 31u);
    return (ClusterTargetDirtyPageWord(ClusterRasterTargetHierarchyIndex(
            target, 0u, localPage.y, word), depthCacheClass) & bit) != 0u;
}

bool ClusterRasterTargetReceiverAccepts(
    ClusterRasterTargetView target,
    uint2 localPage,
    uint2 pagePixel)
{
    if (target.ReceiverMaskEnabled == 0u)
        return true;

    uint4 mask = LoadClusterTargetReceiverMask(target, localPage);
    uint2 cell = min(
        pagePixel * 8u / max(target.PageSize, 1u),
        7u.xx);
    return VirtualShadowReceiverMaskCell(mask, cell);
}

bool ClusterRasterTargetCachedReceiverAccepts(
    ClusterRasterTargetView target,
    uint4 receiverMask,
    uint2 pagePixel)
{
    if (target.ReceiverMaskEnabled == 0u)
        return true;

    uint2 cell = min(
        pagePixel * 8u / max(target.PageSize, 1u),
        7u.xx);
    return VirtualShadowReceiverMaskCell(receiverMask, cell);
}

int4 PrepareRasterTargetScissor(
    uint2 rasterWindow,
    uint targetViewIndex,
    uint screenWidth,
    uint screenHeight,
    uint threadIndex,
    uint threadCount, uint requiredPageFlags = 0u)
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
            uint4 receiverMask = 0u.xxxx;
            uint2 localPage = pageWindow.xy + cacheCoord;
            if (ClusterRasterTargetPageRequested(target, localPage) &&
                (requiredPageFlags == 0u || (RasterTargetPageTable[ClusterRasterTargetTableIndex(target, localPage)] & requiredPageFlags) != 0u))
            {
                uint tableIndex;
                if (ClusterRasterTargetResolvePage(target, localPage, tableIndex, page))
                {
                    receiverMask = target.ReceiverMaskEnabled != 0u
                        ? LoadClusterTargetReceiverMask(target, localPage)
                        : 0xffffu.xxxx;
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
    return PrepareRasterTargetScissor(VisibleClusterRasterWindow(RasterOutputVisibleClusters[visibleClusterIndex]), targetViewIndex,
        screenWidth, screenHeight, threadIndex, threadCount,
        (RasterOutputVisibleClusters[visibleClusterIndex].PageAndFlags & 2u) != 0u ? VSM_PAGE_PRIMARY : 0u);
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
    float2 windowSize, uint depthCacheClass = 0u)
{
    if (ClusterRasterTargetPageCacheEnabled == 0u) return true;
    ClusterRasterTargetView target = RasterTargetViews[targetViewIndex];
    // Bounds include neighboring pixels at quantized raster edges and apply
    // receiver coverage before admitted triangles write their requested pages.
    uint2 firstPixel = uint2(clamp(floor(minimumPixel) - 1.0, 0.0.xx, windowSize - 1.0));
    uint2 lastPixel = uint2(clamp(ceil(maximumPixel) + 1.0, 0.0.xx, windowSize - 1.0));
    uint2 firstPage = firstPixel / target.PageSize;
    uint2 lastPage = lastPixel / target.PageSize;
    for (uint row = firstPage.y; row <= lastPage.y; row++)
    for (uint column = firstPage.x; column <= lastPage.x; column++)
    {
        uint index = row * CLUSTER_RASTER_TARGET_PAGE_CACHE_DIM + column;
        if (ClusterRasterTargetCachedPages[index] == CLUSTER_RASTER_TARGET_INVALID_PAGE) continue;
        if (target.ReceiverMaskEnabled == 0u || depthCacheClass == 1u) return true;
        uint2 pageOrigin = uint2(column, row) * target.PageSize;
        uint2 pageFirst = firstPixel - min(firstPixel, pageOrigin);
        uint2 pageLast = min(lastPixel - pageOrigin, target.PageSize - 1u);
        uint2 firstCell = min(pageFirst * 8u / target.PageSize, 7u.xx);
        uint2 lastCell = min(pageLast * 8u / target.PageSize, 7u.xx);
        if (VirtualShadowReceiverMaskRectangle(
                ClusterRasterTargetCachedReceiverMasks[index], firstCell, lastCell)) return true;
    }
    return false;
}

bool ResolveCachedRasterTargetPixel(
    int2 pixel,
    uint targetViewIndex,
    out uint2 targetPixel,
    uint depthCacheClass = 0u)
{
    if (ClusterRasterTargetPageCacheEnabled == 0u)
        return ResolveRasterTargetPixel(pixel, targetViewIndex, targetPixel, depthCacheClass);

    targetPixel = 0u.xx;
    ClusterRasterTargetView target = RasterTargetViews[targetViewIndex];
    uint2 virtualPixel = uint2(pixel);
    uint2 localPage = virtualPixel / target.PageSize;
    if (any(localPage < ClusterRasterTargetCachedOrigin))
        return false;
    uint2 cacheCoord = localPage - ClusterRasterTargetCachedOrigin;
    if (any(cacheCoord >= ClusterRasterTargetCachedExtent))
        return false;
    uint cacheIndex =
        cacheCoord.y * CLUSTER_RASTER_TARGET_PAGE_CACHE_DIM + cacheCoord.x;
    uint page = ClusterRasterTargetCachedPages[cacheIndex];
    if (page == CLUSTER_RASTER_TARGET_INVALID_PAGE)
        return false;

    uint2 pagePixel = virtualPixel & (target.PageSize - 1u);
    uint2 pageCoord = uint2(
        page % target.PhysicalPagesPerRow,
        page / target.PhysicalPagesPerRow);
#ifdef CLUSTER_TARGET_DEPTH_CACHE
    if (depthCacheClass == 1u && (RasterTargetPageMetadata[page].Flags & 8u) == 0u)
        return false;
#endif
    targetPixel = pageCoord * target.PageSize + pagePixel;
    return true;
}

bool ResolveRasterTargetPixel(int2 pixel, uint targetViewIndex, out uint2 targetPixel, uint depthCacheClass = 0u)
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
    if (!ClusterRasterTargetPageRequested(target, localPage, depthCacheClass))
        return false;

    uint tableIndex;
    uint page;
    if (!ClusterRasterTargetResolvePage(target, localPage, tableIndex, page))
        return false;

    uint2 pagePixel = virtualPixel & (target.PageSize - 1u);
    uint2 pageCoord = uint2(
        page % target.PhysicalPagesPerRow,
        page / target.PhysicalPagesPerRow);
#ifdef CLUSTER_TARGET_DEPTH_CACHE
    if (depthCacheClass == 1u && (RasterTargetPageMetadata[page].Flags & 8u) == 0u) return false;
#endif
    targetPixel = pageCoord * target.PageSize + pagePixel;
    return true;
}

uint ReadRasterTargetPixelDepth(uint2 targetPixel, uint depthCacheClass = 0u)
{
#ifdef CLUSTER_TARGET_DEPTH_CACHE
    if (depthCacheClass == 1u)
        return RasterTargetOutput[int3(targetPixel, 1)];
#endif
    return RasterTargetOutput[int3(targetPixel, 0)];
}

void WriteRasterTargetPixelDepth(uint2 targetPixel, float depth, uint depthCacheClass = 0u)
{
    uint encodedDepth = asuint(1.0 - saturate(depth));
    uint ignored;
#ifdef CLUSTER_TARGET_DEPTH_CACHE
    if (depthCacheClass == 1u)
        InterlockedMax(RasterTargetOutput[int3(targetPixel, 1)], encodedDepth, ignored);
    else
#endif
        InterlockedMax(RasterTargetOutput[int3(targetPixel, 0)], encodedDepth, ignored);
}

void WriteRasterTargetDepth(int2 pixel, uint targetViewIndex, float depth, uint depthCacheClass = 0u)
{
    uint2 targetPixel;
    if (ResolveRasterTargetPixel(pixel, targetViewIndex, targetPixel, depthCacheClass))
        WriteRasterTargetPixelDepth(targetPixel, depth, depthCacheClass);
}

#endif
