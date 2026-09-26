#ifndef CLUSTER_RASTER_TARGET_VIEW_SLANG
#define CLUSTER_RASTER_TARGET_VIEW_SLANG

struct ClusterRasterTargetView
{
    uint PageTableBase;
    uint PageHierarchyBase;
    uint PagesPerRow;
    uint PageSize;

    uint MipResolution;
    uint PhysicalPagesPerRow;
    uint CacheGeneration;
    uint MipLevel;

    int WorldPageOriginX;
    int WorldPageOriginY;
    uint TableOriginX;
    uint TableOriginY;

    uint ReceiverMaskEnabled;
    uint RasterWindowPages;
    uint2 Padding;
    uint4 UncachedPageRect;
};

static const uint CLUSTER_RASTER_TARGET_VIEW_BYTES = 80u;

uint2 ClusterPackRasterTargetPageWindow(uint4 pageWindow)
{
    return uint2(
        (pageWindow.x & 0xFFFFu) | ((pageWindow.y & 0xFFFFu) << 16u),
        (pageWindow.z & 0xFFFFu) | ((pageWindow.w & 0xFFFFu) << 16u));
}

uint4 ClusterUnpackRasterTargetPageWindow(uint2 packedWindow)
{
    return uint4(
        packedWindow.x & 0xFFFFu,
        packedWindow.x >> 16u,
        packedWindow.y & 0xFFFFu,
        packedWindow.y >> 16u);
}

uint ClusterRasterTargetTableIndex(
    ClusterRasterTargetView target,
    uint2 localPage)
{
    uint mask = target.PagesPerRow - 1u;
    uint2 slot = (uint2(target.TableOriginX, target.TableOriginY) + localPage) & mask;
    return target.PageTableBase + slot.y * target.PagesPerRow + slot.x;
}

int2 ClusterRasterTargetWorldPage(
    ClusterRasterTargetView target,
    uint2 localPage)
{
    return int2(target.WorldPageOriginX, target.WorldPageOriginY) + int2(localPage);
}

uint ClusterRasterTargetHierarchyLevelOffset(
    ClusterRasterTargetView target,
    uint hierarchyLevel)
{
    uint offset = target.PageHierarchyBase;
    uint dimension = target.PagesPerRow;
    [loop]
    for (uint level = 0u; level < hierarchyLevel; level++)
    {
        offset += dimension * ((dimension + 31u) / 32u);
        dimension = max(dimension >> 1u, 1u);
    }
    return offset;
}

uint ClusterRasterTargetHierarchyDimension(
    ClusterRasterTargetView target,
    uint hierarchyLevel)
{
    return max(target.PagesPerRow >> hierarchyLevel, 1u);
}

uint ClusterRasterTargetHierarchyIndex(
    ClusterRasterTargetView target,
    uint hierarchyLevel,
    uint row,
    uint word)
{
    uint dimension =
        ClusterRasterTargetHierarchyDimension(target, hierarchyLevel);
    return ClusterRasterTargetHierarchyLevelOffset(target, hierarchyLevel) +
        row * ((dimension + 31u) / 32u) + word;
}

#endif
