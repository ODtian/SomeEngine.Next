#ifndef CLUSTER_RASTER_REGION_ACCESS_SLANG
#define CLUSTER_RASTER_REGION_ACCESS_SLANG

#include "cluster_view_access.slang"
#include "cluster_raster_target_invocation.slang"

StructuredBuffer<ClusterRasterTargetInvocation> RasterTargetInvocations;

float4x4 ClusterCropMatrix(float4 crop)
{
    return float4x4(
        crop.x, 0.0, 0.0, 0.0,
        0.0, crop.y, 0.0, 0.0,
        0.0, 0.0, 1.0, 0.0,
        crop.z, crop.w, 0.0, 1.0);
}

ClusterViewUniforms ClusterApplyRasterRegion(
    ClusterViewUniforms view,
    ClusterRasterTargetInvocation region,
    uint regionIndex)
{
    float4x4 crop = ClusterCropMatrix(region.Crop);
    view.ViewProj = mul(view.ViewProj, crop);
    view.PrevViewProj = mul(view.PrevViewProj, crop);
    view.ClipToWorld = mul(ClusterCropMatrix(float4(
        1.0 / region.Crop.xy, -region.Crop.zw / region.Crop.xy)), view.ClipToWorld);
    view.P00 *= region.Crop.x;
    view.P11 *= region.Crop.y;
    view.PrevP00 *= region.Crop.x;
    view.PrevP11 *= region.Crop.y;
    view.HiZInvSize *= region.Crop.xy;
    view.ScreenWidth = region.OutputExtent.x;
    view.ScreenHeight = region.OutputExtent.y;
    view.HasPrevHistory = region.PreviousHiZLayer != CLUSTER_INVALID_HIZ_LAYER ? 1u : 0u;
    view.TargetViewIndex = regionIndex;
    view.ViewPadding = 1u;
    return view;
}

ClusterViewUniforms LoadClusterView(
    StructuredBuffer<ClusterViewUniforms> primaryViews,
    StructuredBuffer<ClusterViewUniforms> policyViews,
    uint viewIndex)
{
    if (!ClusterIsPolicyViewIndex(viewIndex))
        return primaryViews[viewIndex];
    uint regionIndex = ClusterPhysicalViewIndex(viewIndex);
    ClusterRasterTargetInvocation region = RasterTargetInvocations[regionIndex];
    return ClusterApplyRasterRegion(policyViews[region.SourceViewIndex], region, regionIndex);
}

#endif
