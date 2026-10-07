#ifndef CLUSTER_POLICY_RASTER_OUTPUT_SLANG
#define CLUSTER_POLICY_RASTER_OUTPUT_SLANG

#include "cluster_raster_shared.slang"
#include "cluster_visibility_raster_output.slang"
#define CLUSTER_TARGET_DEPTH_CACHE 1
#include "cluster_raster_target_output.slang"
#include "cluster_instance_properties.slang"

StructuredBuffer<uint2> VisibleClusterRasterWindows;
StructuredBuffer<DrawRequest> RasterOutputVisibleClusters;
ByteAddressBuffer RasterOutputInstanceData;
ConstantBuffer<RenderInstancePropertyMetadata> RasterOutputInstanceProperties;
uint RasterOutputDepthCacheClass(uint visibleIndex)
{
    return LoadInstanceDepthCacheClass(RasterOutputInstanceData, RasterOutputInstanceProperties,
        RasterOutputVisibleClusters[visibleIndex].InstanceID);
}

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
    if (view.TargetPolicy != 0u && (RasterTargetViews[view.TargetViewIndex].Padding.y & 4u) == 0u)
        clip.z = 0.5 * clip.w;
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

bool ResolveClusterRasterOutputPixel(int2 pixel, uint targetPolicy, uint targetViewIndex,
    uint visibleClusterIndex, bool usePageCache, out ClusterRasterOutputPixel outputPixel)
{
    outputPixel.Position = pixel;
    outputPixel.DepthCacheClass = 0u;
    if (targetPolicy == 0u)
        return true;

    outputPixel.DepthCacheClass = RasterOutputDepthCacheClass(visibleClusterIndex);
    uint2 targetPixel;
    bool accepted = usePageCache
        ? ResolveCachedRasterTargetPixel(pixel, targetViewIndex, targetPixel, outputPixel.DepthCacheClass)
        : ResolveRasterTargetPixel(pixel, targetViewIndex, targetPixel, outputPixel.DepthCacheClass);
    outputPixel.Position = int2(targetPixel);
    return accepted;
}

bool ResolveClusterHardwareRasterPixel(int2 pixel, uint visibleClusterIndex,
    ClusterViewUniforms view, out ClusterRasterOutputPixel outputPixel)
{
    outputPixel.Position = pixel;
    outputPixel.DepthCacheClass = 0u;
    if (view.TargetPolicy != 0u && !ClusterRasterTargetWindowContainsPixel(
            visibleClusterIndex, view.TargetViewIndex, pixel))
        return false;
    return ResolveClusterRasterOutputPixel(pixel, view.TargetPolicy, view.TargetViewIndex,
        visibleClusterIndex, false, outputPixel);
}

bool ClusterRasterQuadAcceptsPixel(bool accepted)
{
    // Keep every lane of an accepted quad active during user derivative evaluation.
    uint value = accepted ? 1u : 0u;
    return (value | QuadReadAcrossX(value) | QuadReadAcrossY(value) |
        QuadReadAcrossDiagonal(value)) != 0u;
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

    bool resolvePixel(int2 pixel, uint targetPolicy, uint targetViewIndex,
        uint visibilityValue, out ClusterRasterOutputPixel outputPixel)
    {
        return ResolveClusterRasterOutputPixel(pixel, targetPolicy, targetViewIndex,
            DecodeVisBufferVisibleClusterIndex(visibilityValue), true, outputPixel);
    }

    void write(
        ClusterRasterOutputPixel pixel,
        uint viewIndex,
        uint targetPolicy,
        uint targetViewIndex,
        float depth,
        uint visibilityValue)
    {
        if (targetPolicy == 0u)
        {
            WriteVisibilityWinnerImage(pixel.Position, targetViewIndex, depth, visibilityValue);
            return;
        }
        WriteRasterTargetPixelDepth(uint2(pixel.Position), depth, pixel.DepthCacheClass);
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

    bool resolvePixel(int2 pixel, uint targetPolicy, uint targetViewIndex,
        uint visibilityValue, out ClusterRasterOutputPixel outputPixel)
    {
        return ResolveClusterRasterOutputPixel(pixel, targetPolicy, targetViewIndex,
            DecodeVisBufferVisibleClusterIndex(visibilityValue), true, outputPixel);
    }

    void write(
        ClusterRasterOutputPixel pixel,
        uint viewIndex,
        uint targetPolicy,
        uint targetViewIndex,
        float depth,
        uint visibilityValue)
    {
        if (targetPolicy == 0u)
        {
            WriteVisibilityWinnerBuffer(
                pixel.Position,
                targetViewIndex,
                Width,
                Height,
                depth,
                visibilityValue);
            return;
        }
        WriteRasterTargetPixelDepth(uint2(pixel.Position), depth, pixel.DepthCacheClass);
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

    bool resolvePixel(int2 pixel, uint targetPolicy, uint targetViewIndex,
        uint visibilityValue, out ClusterRasterOutputPixel outputPixel)
    {
        if (targetPolicy == 0u)
            return Primary.resolvePixel(pixel, targetPolicy, targetViewIndex, visibilityValue, outputPixel);
        return ResolveClusterRasterOutputPixel(pixel, targetPolicy, targetViewIndex,
            DecodeVisBufferVisibleClusterIndex(visibilityValue), false, outputPixel);
    }

    void write(ClusterRasterOutputPixel pixel, uint viewIndex, uint targetPolicy, uint targetViewIndex,
        float depth, uint visibilityValue)
    {
        if (targetPolicy == 0u)
            Primary.write(pixel, viewIndex, targetPolicy, targetViewIndex, depth, visibilityValue);
        else
            WriteRasterTargetPixelDepth(uint2(pixel.Position), depth, pixel.DepthCacheClass);
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

    bool resolvePixel(int2 pixel, uint targetPolicy, uint targetViewIndex,
        uint visibilityValue, out ClusterRasterOutputPixel outputPixel)
    {
        return ResolveClusterRasterOutputPixel(pixel, targetPolicy, targetViewIndex,
            DecodeVisBufferVisibleClusterIndex(visibilityValue), true, outputPixel);
    }

    void write(ClusterRasterOutputPixel pixel, uint viewIndex, uint targetPolicy, uint targetViewIndex,
        float depth, uint visibilityValue)
    {
        WriteRasterTargetPixelDepth(uint2(pixel.Position), depth, pixel.DepthCacheClass);
    }
};

#endif
