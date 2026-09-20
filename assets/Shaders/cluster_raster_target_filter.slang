#ifndef CLUSTER_RASTER_TARGET_FILTER_SLANG
#define CLUSTER_RASTER_TARGET_FILTER_SLANG

#include "cluster_raster_target_view.slang"
#include "cluster_view_access.slang"

StructuredBuffer<ClusterRasterTargetView> RasterTargetViews;
StructuredBuffer<uint> RasterTargetPageHierarchy;
StructuredBuffer<uint4> RasterTargetPageTable;
StructuredBuffer<uint2> RasterTargetReceiverMasks;
Texture2DArray<float> RasterTargetCurrentHiZ;
Texture2DArray<float> RasterTargetPreviousHiZ;

uint2 ClusterTargetPixelMin(float2 uv, ClusterViewUniforms view)
{
    return uint2(max(floor(uv * float2(view.ScreenWidth, view.ScreenHeight)), 0.0.xx));
}

uint2 ClusterTargetPixelMax(float2 uv, ClusterViewUniforms view)
{
    return uint2(max(ceil(uv * float2(view.ScreenWidth, view.ScreenHeight)) - 1.0, 0.0.xx));
}

bool ClusterTargetPixelBounds(
    float2 minUv,
    float2 maxUv,
    ClusterViewUniforms view,
    ClusterRasterTargetView target,
    out uint2 firstPixel,
    out uint2 lastPixel)
{
    if (any(maxUv < 0.0.xx) || any(minUv > 1.0.xx) || any(maxUv < minUv))
    {
        firstPixel = 0u.xx;
        lastPixel = 0u.xx;
        return false;
    }

    uint limit = max(target.MipResolution, 1u) - 1u;
    firstPixel = min(ClusterTargetPixelMin(saturate(minUv), view), limit.xx);
    lastPixel = min(ClusterTargetPixelMax(saturate(maxUv), view), limit.xx);
    return all(firstPixel <= lastPixel);
}

uint ClusterTargetHierarchyWordMask(
    ClusterRasterTargetView target,
    uint hierarchyLevel,
    uint row,
    uint word,
    uint firstColumn,
    uint lastColumn)
{
    uint mask = RasterTargetPageHierarchy[
        ClusterRasterTargetHierarchyIndex(
            target, hierarchyLevel, row, word)];
    if (word == firstColumn / 32u)
        mask &= 0xFFFFFFFFu << (firstColumn & 31u);
    if (word == lastColumn / 32u)
        mask &= 0xFFFFFFFFu >> (31u - (lastColumn & 31u));
    return mask;
}

bool ClusterTargetReceiverAccepts(
    ClusterRasterTargetView target,
    uint2 localPage,
    uint2 firstPixel,
    uint2 lastPixel)
{
    if (target.ReceiverMaskEnabled == 0u)
        return true;

    uint tableIndex = ClusterRasterTargetTableIndex(target, localPage);
    uint2 receiverMask = RasterTargetReceiverMasks[tableIndex];
    if (all(receiverMask == 0u.xx))
        return false;

    uint2 pageOrigin = localPage * target.PageSize;
    uint2 localFirst = firstPixel > pageOrigin
        ? firstPixel - pageOrigin
        : 0u.xx;
    uint2 localLast = min(lastPixel - min(lastPixel, pageOrigin), target.PageSize - 1u);

    uint2 firstCell = min(
        localFirst * 8u / max(target.PageSize, 1u),
        7u.xx);
    uint2 lastCell = min(
        localLast * 8u / max(target.PageSize, 1u),
        7u.xx);
    uint columnMask =
        (0xFFu << firstCell.x) &
        (0xFFu >> (7u - lastCell.x));

    for (uint row = firstCell.y; row <= lastCell.y; row++)
    {
        uint rowMask = row < 4u
            ? (receiverMask.x >> (row * 8u)) & 0xFFu
            : (receiverMask.y >> ((row - 4u) * 8u)) & 0xFFu;
        if ((rowMask & columnMask) != 0u)
            return true;
    }
    return false;
}

bool ClusterTargetPageRequested(
    ClusterRasterTargetView target,
    uint2 localPage)
{
    uint word = localPage.x / 32u;
    uint bit = 1u << (localPage.x & 31u);
    return (RasterTargetPageHierarchy[
        ClusterRasterTargetHierarchyIndex(
            target, 0u, localPage.y, word)] & bit) != 0u;
}

bool ClusterTargetPageBounds(
    ClusterRasterTargetView target,
    uint2 firstPixel,
    uint2 lastPixel,
    out uint2 firstPage,
    out uint2 lastPage)
{
    firstPage = max(
        firstPixel / target.PageSize,
        target.UncachedPageRect.xy);
    lastPage = min(
        lastPixel / target.PageSize,
        target.UncachedPageRect.zw);
    return all(firstPage <= lastPage);
}

bool ClusterTargetPolicyScreenBoundsAccepts(
    float2 minUv,
    float2 maxUv,
    ClusterViewUniforms view)
{
    ClusterRasterTargetView target = RasterTargetViews[view.TargetViewIndex];
    uint2 firstPixel;
    uint2 lastPixel;
    if (!ClusterTargetPixelBounds(
            minUv, maxUv, view, target, firstPixel, lastPixel))
        return false;

    uint2 firstPage;
    uint2 lastPage;
    if (!ClusterTargetPageBounds(
            target, firstPixel, lastPixel, firstPage, lastPage))
        return false;
    uint2 pageExtent = lastPage - firstPage + 1u;
    uint maximumExtent = max(pageExtent.x, pageExtent.y);
    uint hierarchyLevel =
        maximumExtent > 1u ? uint(firstbithigh(maximumExtent)) : 0u;
    hierarchyLevel = min(
        hierarchyLevel,
        uint(firstbithigh(max(target.PagesPerRow, 1u))));
    uint2 firstCell = firstPage >> hierarchyLevel;
    uint2 lastCell = lastPage >> hierarchyLevel;
    uint firstWord = firstCell.x / 32u;
    uint lastWord = lastCell.x / 32u;

    for (uint row = firstCell.y; row <= lastCell.y; row++)
    {
        for (uint word = firstWord; word <= lastWord; word++)
        {
            if (ClusterTargetHierarchyWordMask(
                    target,
                    hierarchyLevel,
                    row,
                    word,
                    firstCell.x,
                    lastCell.x) != 0u)
                return true;
        }
    }
    return false;
}

bool ClusterTargetPolicyRasterBoundsWindow(
    float2 minUv,
    float2 maxUv,
    ClusterViewUniforms view,
    out uint2 rasterWindow)
{
    rasterWindow = 0u.xx;
    ClusterRasterTargetView target = RasterTargetViews[view.TargetViewIndex];
    uint2 firstPixel;
    uint2 lastPixel;
    if (!ClusterTargetPixelBounds(
            minUv, maxUv, view, target, firstPixel, lastPixel))
        return false;

    uint2 firstPage;
    uint2 lastPage;
    if (!ClusterTargetPageBounds(
            target, firstPixel, lastPixel, firstPage, lastPage))
        return false;
    uint2 acceptedFirst = lastPage;
    uint2 acceptedLast = firstPage;
    bool accepted = false;
    uint firstWord = firstPage.x / 32u;
    uint lastWord = lastPage.x / 32u;
    for (uint row = firstPage.y; row <= lastPage.y; row++)
    {
        for (uint word = firstWord; word <= lastWord; word++)
        {
            uint mask = ClusterTargetHierarchyWordMask(
                target,
                0u,
                row,
                word,
                firstPage.x,
                lastPage.x);
            while (mask != 0u)
            {
                uint column = word * 32u + firstbitlow(mask);
                mask &= mask - 1u;
                uint2 localPage = uint2(column, row);
                if (ClusterTargetReceiverAccepts(
                        target, localPage, firstPixel, lastPixel))
                {
                    acceptedFirst = min(acceptedFirst, localPage);
                    acceptedLast = max(acceptedLast, localPage);
                    accepted = true;
                }
            }
        }
    }
    if (accepted)
        rasterWindow = ClusterPackRasterTargetPageWindow(
            uint4(acceptedFirst, acceptedLast));
    return accepted;
}

bool ClusterTargetEntryValid(
    ClusterRasterTargetView target,
    uint2 localPage,
    out uint physicalPage)
{
    uint tableIndex = ClusterRasterTargetTableIndex(target, localPage);
    uint4 entry = RasterTargetPageTable[tableIndex];
    int2 expectedWorldPage = ClusterRasterTargetWorldPage(target, localPage);
    if (entry.x == 0u || entry.x == 0xFFFFFFFFu ||
        entry.w != target.CacheGeneration ||
        asint(entry.y) != expectedWorldPage.x ||
        asint(entry.z) != expectedWorldPage.y)
    {
        physicalPage = 0u;
        return false;
    }
    physicalPage = entry.x - 1u;
    return true;
}

float ClusterTargetHiZMax(
    Texture2DArray<float> source,
    ClusterRasterTargetView target,
    uint physicalPage,
    uint2 localPage,
    uint2 firstPixel,
    uint2 lastPixel,
    uint mip)
{
    uint2 pageOrigin = localPage * target.PageSize;
    uint2 localFirst = firstPixel > pageOrigin
        ? firstPixel - pageOrigin
        : 0u.xx;
    uint2 localLast = min(lastPixel - min(lastPixel, pageOrigin), target.PageSize - 1u);

    uint baseSize = max(target.PageSize / 2u, 1u);
    uint mipSize = max(baseSize >> mip, 1u);
    uint2 p0 = min((localFirst / 2u) >> mip, mipSize - 1u);
    uint2 p3 = min((localLast / 2u) >> mip, mipSize - 1u);
    uint2 p1 = uint2(p3.x, p0.y);
    uint2 p2 = uint2(p0.x, p3.y);

    return max(
        max(
            source.Load(int4(p0, physicalPage, mip)),
            source.Load(int4(p1, physicalPage, mip))),
        max(
            source.Load(int4(p2, physicalPage, mip)),
            source.Load(int4(p3, physicalPage, mip))));
}

bool ClusterTargetPolicyAccepts(
    ClusterProjectedBounds projectedBounds,
    ClusterViewUniforms view)
{
    if (view.TargetPolicy == 0u)
        return true;
    if (view.CullingEnabled == 0u || projectedBounds.CrossesNearPlane)
        return ClusterTargetPolicyScreenBoundsAccepts(0.0.xx, 1.0.xx, view);
    return ClusterTargetPolicyScreenBoundsAccepts(
        float2(projectedBounds.NdcMin.x, -projectedBounds.NdcMax.y) * 0.5 + 0.5,
        float2(projectedBounds.NdcMax.x, -projectedBounds.NdcMin.y) * 0.5 + 0.5,
        view);
}

bool ClusterTargetPolicyScreenBoundsOccluded(
    float2 minUv,
    float2 maxUv,
    float nearDepth,
    bool usePrevious,
    ClusterViewUniforms view)
{
    if (view.TargetPolicy == 0u || view.HiZMipCount == 0u)
        return false;
    if (usePrevious && view.HasPrevHistory == 0u)
        return false;

    ClusterRasterTargetView target = RasterTargetViews[view.TargetViewIndex];
    uint2 firstPixel;
    uint2 lastPixel;
    if (!ClusterTargetPixelBounds(
            minUv, maxUv, view, target, firstPixel, lastPixel))
        return false;

    uint2 firstPage;
    uint2 lastPage;
    if (!ClusterTargetPageBounds(
            target, firstPixel, lastPixel, firstPage, lastPage))
        return false;
    uint firstWord = firstPage.x / 32u;
    uint lastWord = lastPage.x / 32u;
    bool tested = false;

    for (uint row = firstPage.y; row <= lastPage.y; row++)
    {
        for (uint word = firstWord; word <= lastWord; word++)
        {
            uint mask = ClusterTargetHierarchyWordMask(
                target, 0u, row, word, firstPage.x, lastPage.x);
            while (mask != 0u)
            {
                uint column = word * 32u + firstbitlow(mask);
                mask &= mask - 1u;
                uint2 localPage = uint2(column, row);
                if (!ClusterTargetReceiverAccepts(
                        target, localPage, firstPixel, lastPixel))
                    continue;

                uint physicalPage;
                if (!ClusterTargetEntryValid(target, localPage, physicalPage))
                    return false;

                uint2 pageOrigin = localPage * target.PageSize;
                uint2 localFirst = firstPixel > pageOrigin
                    ? firstPixel - pageOrigin
                    : 0u.xx;
                uint2 localLast = min(
                    lastPixel - min(lastPixel, pageOrigin),
                    target.PageSize - 1u);
                uint2 diameter = localLast - localFirst + 1u;
                uint hizMip = min(
                    uint(max(0.0, ceil(log2(float(max(diameter.x, diameter.y)))) - 1.0)),
                    view.HiZMipCount - 1u);
                float hizDepth;
                if (usePrevious)
                {
                    hizDepth = ClusterTargetHiZMax(
                        RasterTargetPreviousHiZ,
                        target,
                        physicalPage,
                        localPage,
                        firstPixel,
                        lastPixel,
                        hizMip);
                }
                else
                {
                    hizDepth = ClusterTargetHiZMax(
                        RasterTargetCurrentHiZ,
                        target,
                        physicalPage,
                        localPage,
                        firstPixel,
                        lastPixel,
                        hizMip);
                }
                tested = true;
                float epsilon = 5e-5 +
                    1.0 / float(max(target.PageSize, 1u));
                if (nearDepth <= hizDepth + epsilon)
                    return false;
            }
        }
    }
    return tested;
}

bool ClusterTargetPolicyWorldBoundsOccluded(
    float3 worldMin,
    float3 worldMax,
    bool usePrevious,
    ClusterViewUniforms view)
{
    if (usePrevious && view.HasPrevHistory == 0u)
        return false;

    float2 minUv;
    float2 maxUv;
    float nearDepth;
    uint unusedMip;
    BuildWorldAabbScreenBoundsAndNearDepth(
        worldMin,
        worldMax,
        usePrevious,
        view,
        minUv,
        maxUv,
        nearDepth,
        unusedMip);
    return ClusterTargetPolicyScreenBoundsOccluded(
        minUv,
        maxUv,
        nearDepth,
        usePrevious,
        view);
}

#endif
