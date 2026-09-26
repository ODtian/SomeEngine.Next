#ifndef CLUSTER_POLICY_RASTER_OUTPUT_SLANG
#define CLUSTER_POLICY_RASTER_OUTPUT_SLANG

#include "cluster_raster_shared.slang"
#include "cluster_visibility_raster_output.slang"
#include "cluster_raster_target_output.slang"

StructuredBuffer<uint2> VisibleClusterRasterWindows;

bool ClusterRasterTargetWindowContainsPixel(
    uint visibleClusterIndex,
    uint targetViewIndex,
    int2 pixel)
{
    if (any(pixel < 0))
        return false;
    ClusterRasterTargetView target = RasterTargetViews[targetViewIndex];
    uint4 pageWindow = ClusterUnpackRasterTargetPageWindow(
        VisibleClusterRasterWindows[visibleClusterIndex]);
    uint2 localPage = uint2(pixel) / target.PageSize;
    return all(localPage >= pageWindow.xy) && all(localPage <= pageWindow.zw);
}

struct ClusterRasterOutputImageAtomic64 : IClusterRasterOutput
{
    int4 scissor(
        uint visibleClusterIndex,
        uint targetPolicy,
        uint targetViewIndex,
        uint screenWidth,
        uint screenHeight)
    {
        if (targetPolicy == 0u)
            return int4(0, 0, int(screenWidth), int(screenHeight));
        return PrepareRasterTargetScissor(
            visibleClusterIndex,
            targetViewIndex,
            screenWidth,
            screenHeight);
    }

    void write(
        int2 pixel,
        uint viewIndex,
        uint targetPolicy,
        uint targetViewIndex,
        float depth,
        uint visibilityValue)
    {
        if (targetPolicy == 0u)
        {
            WriteVisibilityWinnerImage(pixel, targetViewIndex, depth, visibilityValue);
            return;
        }
        if (WriteCachedRasterTargetDepth(pixel, targetViewIndex, depth))
            return;
        WriteRasterTargetDepth(pixel, targetViewIndex, depth);
    }
};

struct ClusterRasterOutputBufferAtomic64 : IClusterRasterOutput
{
    uint Width;
    uint Height;

    int4 scissor(
        uint visibleClusterIndex,
        uint targetPolicy,
        uint targetViewIndex,
        uint screenWidth,
        uint screenHeight)
    {
        if (targetPolicy == 0u)
            return int4(0, 0, int(screenWidth), int(screenHeight));
        return PrepareRasterTargetScissor(
            visibleClusterIndex,
            targetViewIndex,
            screenWidth,
            screenHeight);
    }

    void write(
        int2 pixel,
        uint viewIndex,
        uint targetPolicy,
        uint targetViewIndex,
        float depth,
        uint visibilityValue)
    {
        if (targetPolicy == 0u)
        {
            WriteVisibilityWinnerBuffer(
                pixel,
                targetViewIndex,
                Width,
                Height,
                depth,
                visibilityValue);
            return;
        }
        if (WriteCachedRasterTargetDepth(pixel, targetViewIndex, depth))
            return;
        WriteRasterTargetDepth(pixel, targetViewIndex, depth);
    }
};

// This output is selected only for a raster stream whose views target depth pages.
struct ClusterRasterOutputTarget : IClusterRasterOutput
{
    int4 scissor(uint visibleClusterIndex, uint targetPolicy, uint targetViewIndex,
        uint screenWidth, uint screenHeight)
    {
        return PrepareRasterTargetScissor(visibleClusterIndex, targetViewIndex, screenWidth, screenHeight);
    }

    void write(int2 pixel, uint viewIndex, uint targetPolicy, uint targetViewIndex,
        float depth, uint visibilityValue)
    {
        if (!WriteCachedRasterTargetDepth(pixel, targetViewIndex, depth))
            WriteRasterTargetDepth(pixel, targetViewIndex, depth);
    }
};

#endif
