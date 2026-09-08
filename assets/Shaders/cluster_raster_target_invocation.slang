#ifndef CLUSTER_RASTER_TARGET_INVOCATION_SLANG
#define CLUSTER_RASTER_TARGET_INVOCATION_SLANG

// A raster region refers to one shared source view. Crop is an NDC scale/offset;
// the source already aligns current and previous projections to the same grid.
struct ClusterRasterTargetInvocation
{
    uint2 OutputOrigin;
    uint2 OutputExtent;
    uint CurrentHiZLayer;
    uint PreviousHiZLayer;
    uint SourceViewIndex;
    uint Flags;
    float4 Crop;
};

static const uint CLUSTER_INVALID_HIZ_LAYER = 0xFFFFFFFFu;

#endif
