#ifndef CLUSTER_POLICY_RASTER_OUTPUT_SLANG
#define CLUSTER_POLICY_RASTER_OUTPUT_SLANG

#include "cluster_raster_shared.slang"
#include "cluster_visibility_raster_output.slang"
#include "cluster_raster_target_output.slang"

StructuredBuffer<uint2> VisibleClusterRasterWindows;

uint2 ClusterRasterTargetPixelOrigin(uint visibleClusterIndex, ClusterViewUniforms view)
{
    if (view.TargetPolicy == 0u) return 0u.xx;
    ClusterRasterTargetView target = RasterTargetViews[view.TargetViewIndex];
    if (target.RasterWindowPages == 0u) return 0u.xx;
    return ClusterUnpackRasterTargetPageWindow(VisibleClusterRasterWindows[visibleClusterIndex]).xy * target.PageSize;
}

float4 ClusterHardwareRasterPosition(float4 clip, uint visibleClusterIndex,
    ClusterViewUniforms view, float2 rasterSize)
{
    float2 scale = float2(view.ScreenWidth, view.ScreenHeight) / rasterSize;
    float2 origin = float2(ClusterRasterTargetPixelOrigin(visibleClusterIndex, view));
    clip.xy = clip.xy * scale + clip.w * (float2(scale.x - 1.0, 1.0 - scale.y) +
        float2(-2.0, 2.0) * origin / rasterSize);
    return clip;
}

float2 ClusterRasterTargetViewport(ClusterViewUniforms view)
{
    if (view.TargetPolicy != 0u)
    {
        ClusterRasterTargetView target = RasterTargetViews[view.TargetViewIndex];
        if (target.RasterWindowPages != 0u)
            return float(target.RasterWindowPages * target.PageSize).xx;
    }
    return float2(view.ScreenWidth, view.ScreenHeight);
}

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

// A dicing wave can contain patches from different windows/views. Its patches
// cannot share the ordinary one-window group cache; resolve each target tap
// through the same requested-page/receiver-aware writer instead.
struct ClusterDicedRasterOutput<TPrimary : IClusterRasterOutput> : IClusterRasterOutput
{
    TPrimary Primary;

    int4 scissor(uint visibleClusterIndex, uint targetPolicy, uint targetViewIndex,
        uint screenWidth, uint screenHeight)
    {
        if (targetPolicy == 0u)
            return Primary.scissor(visibleClusterIndex, targetPolicy, targetViewIndex, screenWidth, screenHeight);
        return RasterTargetWindowScissor(ClusterUnpackRasterTargetPageWindow(
            VisibleClusterRasterWindows[visibleClusterIndex]), RasterTargetViews[targetViewIndex]);
    }

    void write(int2 pixel, uint viewIndex, uint targetPolicy, uint targetViewIndex,
        float depth, uint visibilityValue)
    {
        if (targetPolicy == 0u)
            Primary.write(pixel, viewIndex, targetPolicy, targetViewIndex, depth, visibilityValue);
        else
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
