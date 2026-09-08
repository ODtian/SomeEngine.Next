#ifndef CLUSTER_RASTER_TARGET_OUTPUT_SLANG
#define CLUSTER_RASTER_TARGET_OUTPUT_SLANG

#include "cluster_raster_region_access.slang"
RWTexture2D<uint> RasterTargetOutput;

void WriteRasterTargetDepth(int2 pixel, uint targetViewIndex, float depth)
{
    ClusterRasterTargetInvocation invocation = RasterTargetInvocations[targetViewIndex];
    if (any(pixel < 0) || any(uint2(pixel) >= invocation.OutputExtent))
        return;
    uint2 targetPixel = invocation.OutputOrigin + uint2(pixel);
    uint encodedDepth = asuint(1.0 - saturate(depth));
    uint ignored;
    InterlockedMax(RasterTargetOutput[targetPixel], encodedDepth, ignored);
}

#endif
