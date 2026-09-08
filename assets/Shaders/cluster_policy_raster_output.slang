#ifndef CLUSTER_POLICY_RASTER_OUTPUT_SLANG
#define CLUSTER_POLICY_RASTER_OUTPUT_SLANG

#include "cluster_raster_shared.slang"
#include "cluster_visibility_raster_output.slang"
#include "cluster_raster_target_output.slang"

struct ClusterRasterOutputImageAtomic64 : IClusterRasterOutput
{
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
        WriteRasterTargetDepth(pixel, targetViewIndex, depth);
    }
};

struct ClusterRasterOutputBufferAtomic64 : IClusterRasterOutput
{
    uint Width;
    uint Height;

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
        WriteRasterTargetDepth(pixel, targetViewIndex, depth);
    }
};

#endif
