#ifndef CLUSTER_VIEW_SLANG
#define CLUSTER_VIEW_SLANG

// Production Cluster view/capacity ABI. Diagnostics use separate resources and shaders; no debug
// mode is allowed to alter traversal, LOD, or visibility through this uniform.
struct ClusterViewUniforms
{
    float4x4 ViewProj;
    float3 CameraPos;
    float LodThreshold;

    float GeometryLodScale;
    uint MaxCandidates;
    // Optional sphere centered at CameraPos; zero leaves only the frustum limits.
    float CullingRadius;
    uint InstanceCount;

    uint MaxPageFaults;
    uint RasterMode;
    float SoftwareRasterEdgeThreshold;
    uint CullingEnabled;

    float4x4 PrevViewProj;
    uint HasPrevHistory;
    uint HiZMipCount;
    float2 HiZInvSize;

    float4x4 View;
    float P00;
    float P11;
    uint ScreenWidth;
    uint ScreenHeight;

    float4x4 PrevView;
    float PrevP00;
    float PrevP11;
    // Zero uses the surface group's configured edge size.
    float TessellationEdgePixels;
    uint Pad3;

    float4x4 ClipToWorld;

    uint TargetPolicy;
    uint TargetViewIndex;
    // Zero disables reuse. Within one target policy, equal nonzero indices identify
    // identical ViewProj, CameraPos, CullingRadius and CullingEnabled values.
    uint CullingProjectionIndex;
    float NearPlane;
    float4x4 GeometryViewProj;
    float3 WorldOriginHigh;
    float OriginHighPadding;
    float3 WorldOriginLow;
    float OriginLowPadding;
    float3 PreviousWorldOriginHigh;
    float PreviousOriginHighPadding;
    float3 PreviousWorldOriginLow;
    float PreviousOriginLowPadding;
};

float3 TranslateClusterWorldPosition(float3 position, float3 originHigh,
    float3 originLow, ClusterViewUniforms view)
{
    return position + ((originHigh - view.WorldOriginHigh) + (originLow - view.WorldOriginLow));
}

float3 ClusterProjectionScales(ClusterViewUniforms view)
{
    return float3(
        length(float3(view.GeometryViewProj[0][0], view.GeometryViewProj[1][0], view.GeometryViewProj[2][0])),
        length(float3(view.GeometryViewProj[0][1], view.GeometryViewProj[1][1], view.GeometryViewProj[2][1])),
        length(float3(view.GeometryViewProj[0][3], view.GeometryViewProj[1][3], view.GeometryViewProj[2][3])));
}

float ClusterProjectedError(
    float minimumW, float2 maximumAbsoluteXY, float radius,
    float3 projectionScales, uint2 screenSize)
{
    radius = max(radius, 0.0);
    if (radius <= 1e-8) return 0.0;
    float3 change = projectionScales * radius;
    if (minimumW <= change.z + 1e-5) return 1e20;
    // Bound (c + dc)/(w + dw) - c/w using projection-column norms.
    // Orthographic w has zero derivative; off-axis/cropped perspective includes
    // its full xy columns. This also supports non-uniform world/view transforms.
    float2 ndc = (change.xy * minimumW + maximumAbsoluteXY * change.z) /
        max(minimumW * (minimumW - change.z), 1e-12);
    float2 pixels = ndc * 0.5 * float2(screenSize);
    return max(pixels.x, pixels.y);
}

float CalculateClusterViewPixelScale(ClusterViewUniforms view)
{
    return 0.5 * ClusterProjectionScales(view).y * float(view.ScreenHeight);
}

float CalculateClusterSphereEdgeScale(float3 center, float radius, ClusterViewUniforms view)
{
    if (ClusterProjectionScales(view).z <= 1e-8) return 1.0;
    float3 viewCenter = mul(float4(center, 1.0), view.View).xyz;
    float depth = -viewCenter.z;
    float nearDepth = view.NearPlane;
    if (depth + radius <= nearDepth) return 0.0;

    // The nearest sphere tangent bounds edge projection by forward depth and
    // viewing angle. View-space forward depth is positive after the RH conversion.
    float distanceSquared = dot(viewCenter, viewCenter);
    float sideDistance = sqrt(max(distanceSquared - depth * depth, 0.0));
    float tangentDistance = sqrt(max(distanceSquared - radius * radius, 0.0));
    float minimumCosine = (tangentDistance * depth - radius * sideDistance) /
        max(distanceSquared, 1e-12);
    if (distanceSquared < radius * radius || minimumCosine * tangentDistance < nearDepth)
    {
        float nearOffset = nearDepth - depth;
        float sideExtent = sqrt(max(radius * radius - nearOffset * nearOffset, 0.0));
        float farSide = sideDistance + sideExtent;
        minimumCosine = nearDepth / sqrt(farSide * farSide + nearDepth * nearDepth);
    }
    return max(depth - radius, nearDepth) * minimumCosine;
}

float CalculateClusterSpherePixelScale(float3 center, float radius, ClusterViewUniforms view)
{
    return CalculateClusterViewPixelScale(view) / max(CalculateClusterSphereEdgeScale(center, radius, view), 1e-12);
}

float ClusterSphereError(float3 center, float sphereRadius, float error, ClusterViewUniforms view)
{
    if (error <= 1e-8) return 0.0;
    if (view.TargetPolicy != 0u)
        return error * view.GeometryLodScale * CalculateClusterSpherePixelScale(center, sphereRadius, view);
    float4 clip = mul(float4(center, 1.0), view.GeometryViewProj);
    float3 scales = ClusterProjectionScales(view);
    return ClusterProjectedError(clip.w - sphereRadius * scales.z,
        abs(clip.xy) + sphereRadius * scales.xy, error, scales,
        uint2(view.ScreenWidth, view.ScreenHeight)) * view.GeometryLodScale;
}

#endif
