#ifndef CLUSTER_POLICY_RASTER_OUTPUT_SLANG
#define CLUSTER_POLICY_RASTER_OUTPUT_SLANG

__exported import cluster_raster_shared;
__exported import cluster_visibility_raster_output;
#define CLUSTER_TARGET_DEPTH_CACHE 1
#include "cluster_raster_target_output.slang"
#include "cluster_instance_properties.slang"

ByteAddressBuffer RasterOutputInstanceData;
ConstantBuffer<RenderInstancePropertyMetadata> RasterOutputInstanceProperties;
uint RasterOutputDepthCacheClass(uint visibleIndex)
{
    return LoadTargetDepthCacheClass(VisibleClusterRequest(RasterOutputVisibleClusters[visibleIndex]).InstanceID);
}

uint2 ClusterRasterTargetPixelOrigin<TOutput : IClusterRasterOutput>(uint visibleClusterIndex, ClusterViewUniforms view)
{
    if (!TOutput.UsesReverseDepth()) return 0u.xx;
    ClusterRasterTargetView target = RasterTargetViews[view.TargetViewIndex];
    if (target.RasterWindowPages == 0u) return 0u.xx;
    return ClusterUnpackRasterTargetPageWindow(VisibleClusterRasterWindow(RasterOutputVisibleClusters[visibleClusterIndex])).xy * target.PageSize;
}

float4 ClusterHardwareRasterPosition<TOutput : IClusterRasterOutput>(float4 clip, uint visibleClusterIndex,
    ClusterViewUniforms view, float2 rasterSize)
{
    float2 scale = float2(view.ScreenWidth, view.ScreenHeight) / rasterSize;
    float2 origin = float2(ClusterRasterTargetPixelOrigin<TOutput>(visibleClusterIndex, view));
    clip.xy = clip.xy * scale + clip.w * (float2(scale.x - 1.0, 1.0 - scale.y) +
        float2(-2.0, 2.0) * origin / rasterSize);
    if (!ClusterViewClipsDepth(view))
        clip.z = 0.5 * clip.w;
    return clip;
}

float2 ClusterRasterTargetViewport<TOutput : IClusterRasterOutput>(ClusterViewUniforms view)
{
    if (TOutput.UsesReverseDepth())
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
        VisibleClusterRasterWindow(RasterOutputVisibleClusters[visibleClusterIndex]));
    uint2 localPage = uint2(pixel) / target.PageSize;
    return all(localPage >= pageWindow.xy) && all(localPage <= pageWindow.zw);
}

bool ClusterRasterOutputAcceptsPage(int2 pixel, uint targetViewIndex,
    uint visibleClusterIndex, bool accepted)
{
    if (accepted && (RasterOutputVisibleClusters[visibleClusterIndex].PageAndFlags & 2u) != 0u)
    {
        ClusterRasterTargetView target = RasterTargetViews[targetViewIndex];
        uint tableIndex = ClusterRasterTargetTableIndex(target, uint2(pixel) / target.PageSize);
        accepted = (RasterTargetPageTable[tableIndex] & VSM_PAGE_PRIMARY) != 0u;
    }
    return accepted;
}

bool ResolveClusterRasterOutputPixel(int2 pixel, uint outputKind, uint targetViewIndex,
    uint visibleClusterIndex, out ClusterRasterOutputPixel outputPixel)
{
    outputPixel.Position = pixel;
    outputPixel.DepthCacheClass = 0u;
    if (outputKind == 0u)
        return true;

    outputPixel.DepthCacheClass = RasterOutputDepthCacheClass(visibleClusterIndex);
    uint2 targetPixel;
    bool accepted = ResolveRasterTargetPixel(pixel, targetViewIndex, targetPixel, outputPixel.DepthCacheClass);
    outputPixel.Position = int2(targetPixel);
    return ClusterRasterOutputAcceptsPage(pixel, targetViewIndex, visibleClusterIndex, accepted);
}

// Keep workgroup cache access out of the hardware entry point static call graph.
bool ResolveCachedClusterRasterOutputPixel(int2 pixel, uint outputKind, uint targetViewIndex,
    uint visibleClusterIndex, out ClusterRasterOutputPixel outputPixel)
{
    outputPixel.Position = pixel;
    outputPixel.DepthCacheClass = 0u;
    if (outputKind == 0u)
        return true;

    outputPixel.DepthCacheClass = RasterOutputDepthCacheClass(visibleClusterIndex);
    uint2 targetPixel;
    bool accepted = ResolveCachedRasterTargetPixel(pixel, targetViewIndex, targetPixel, outputPixel.DepthCacheClass);
    outputPixel.Position = int2(targetPixel);
    return ClusterRasterOutputAcceptsPage(pixel, targetViewIndex, visibleClusterIndex, accepted);
}

bool ResolveClusterHardwareRasterPixel<TOutput : IClusterRasterOutput>(int2 pixel, uint visibleClusterIndex,
    ClusterViewUniforms view, out ClusterRasterOutputPixel outputPixel)
{
    outputPixel.Position = pixel;
    outputPixel.DepthCacheClass = 0u;
    if (TOutput.UsesReverseDepth() && !ClusterRasterTargetWindowContainsPixel(
            visibleClusterIndex, view.TargetViewIndex, pixel))
        return false;
    return ResolveClusterRasterOutputPixel(pixel, TOutput.UsesReverseDepth() ? 1u : 0u, view.TargetViewIndex,
        visibleClusterIndex, outputPixel);
}

bool ClusterRasterQuadAcceptsPixel(bool accepted)
{
    // Keep every lane of an accepted quad active during user derivative evaluation.
    uint value = accepted ? 1u : 0u;
    return (value | QuadReadAcrossX(value) | QuadReadAcrossY(value) |
        QuadReadAcrossDiagonal(value)) != 0u;
}

uint ReadClusterRasterTargetDepth(ClusterRasterOutputPixel pixel, uint outputKind, bool accepted = true)
{
    if (outputKind == 0u || !accepted)
        return 0u;
    return ReadRasterTargetPixelDepth(uint2(pixel.Position), pixel.DepthCacheClass);
}

struct ClusterRasterOutputImageAtomic64 : IClusterRasterOutput
{
    static bool UsesReverseDepth() { return false; }

    uint readDepth(ClusterRasterOutputPixel pixel, uint outputView)
    { return uint(VisibilityWinnerImage[uint3(pixel.Position, outputView)] >> 32u); }
    int4 scissor(uint visible, uint outputView, uint width, uint height)
    { return int4(0, 0, int(width), int(height)); }
    bool resolvePixel(int2 pixel, uint outputView, uint identity,
        out ClusterRasterOutputPixel result)
    {
        result = (ClusterRasterOutputPixel)0;
        result.Position = pixel;
        return true;
    }
    void write(ClusterRasterOutputPixel pixel, uint view, uint outputView,
        float depth, uint identity)
    { WriteVisibilityWinnerImage(pixel.Position, outputView, depth, identity); }
};

struct ClusterRasterOutputBufferAtomic64 : IClusterRasterOutput
{
    static bool UsesReverseDepth() { return false; }
    uint Width;
    uint Height;

    uint readDepth(ClusterRasterOutputPixel pixel, uint outputView)
    { return VisibilityWinnerBuffer.Load(((outputView * Height + uint(pixel.Position.y)) * Width + uint(pixel.Position.x)) * 8u + 4u); }
    int4 scissor(uint visible, uint outputView, uint width, uint height)
    { return int4(0, 0, int(width), int(height)); }
    bool resolvePixel(int2 pixel, uint outputView, uint identity,
        out ClusterRasterOutputPixel result)
    {
        result = (ClusterRasterOutputPixel)0;
        result.Position = pixel;
        return true;
    }
    void write(ClusterRasterOutputPixel pixel, uint view, uint outputView,
        float depth, uint identity)
    { WriteVisibilityWinnerBuffer(pixel.Position, outputView, Width, Height, depth, identity); }
};

struct ClusterRasterOutputNonAtomic : IClusterRasterOutput
{
    static bool UsesReverseDepth() { return false; }
    uint Width;
    uint Height;

    uint readDepth(ClusterRasterOutputPixel pixel, uint outputView)
    { return ReadVisibilityNonAtomicDepth(pixel.Position, outputView, Width, Height); }
    int4 scissor(uint visible, uint outputView, uint width, uint height)
    { return int4(0, 0, int(width), int(height)); }
    bool resolvePixel(int2 pixel, uint outputView, uint identity,
        out ClusterRasterOutputPixel result)
    {
        result = (ClusterRasterOutputPixel)0;
        result.Position = pixel;
        return true;
    }
    void write(ClusterRasterOutputPixel pixel, uint view, uint outputView,
        float depth, uint identity)
    { WriteVisibilityNonAtomic(pixel.Position, outputView, Width, Height, depth, identity); }
};

struct ShadowPageOutput : IClusterRasterOutput
{
    static bool UsesReverseDepth() { return true; }
    uint readDepth(ClusterRasterOutputPixel pixel, uint outputView)
    { return ReadClusterRasterTargetDepth(pixel, 1u); }
    int4 scissor(uint visible, uint outputView, uint width, uint height)
    { return PrepareRasterTargetScissor(visible, outputView, width, height); }
    bool resolvePixel(int2 pixel, uint outputView, uint identity,
        out ClusterRasterOutputPixel result)
    { return ResolveCachedClusterRasterOutputPixel(pixel, 1u, outputView,
        DecodeVisBufferVisibleClusterIndex(identity), result); }
    void write(ClusterRasterOutputPixel pixel, uint view, uint outputView,
        float depth, uint identity)
    { WriteRasterTargetPixelDepth(uint2(pixel.Position), depth, pixel.DepthCacheClass); }
};
typedef ShadowPageOutput ClusterRasterOutputTarget;

// Dicing resolves each tap separately because a wave contains several patch windows.
struct ClusterDicedRasterOutput<TPrimary : IClusterRasterOutput> : IClusterRasterOutput
{
    TPrimary Primary;
    static bool UsesReverseDepth() { return TPrimary.UsesReverseDepth(); }
    uint readDepth(ClusterRasterOutputPixel pixel, uint outputView)
    { return Primary.readDepth(pixel, outputView); }
    int4 scissor(uint visible, uint outputView, uint width, uint height)
    {
        if (TPrimary.UsesReverseDepth())
            return RasterTargetWindowScissor(ClusterUnpackRasterTargetPageWindow(
                VisibleClusterRasterWindow(RasterOutputVisibleClusters[visible])), RasterTargetViews[outputView]);
        return Primary.scissor(visible, outputView, width, height);
    }
    bool resolvePixel(int2 pixel, uint outputView, uint identity,
        out ClusterRasterOutputPixel result)
    {
        if (TPrimary.UsesReverseDepth())
            return ResolveClusterRasterOutputPixel(pixel, 1u, outputView,
                DecodeVisBufferVisibleClusterIndex(identity), result);
        return Primary.resolvePixel(pixel, outputView, identity, result);
    }
    void write(ClusterRasterOutputPixel pixel, uint view, uint outputView,
        float depth, uint identity)
    { Primary.write(pixel, view, outputView, depth, identity); }
};

#endif
