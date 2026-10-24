#ifndef CLUSTER_BVH_CULL_COMMON_SLANG
#define CLUSTER_BVH_CULL_COMMON_SLANG

#include "cluster_structures.slang"
#include "cluster_instance_properties.slang"
#include "cluster_view.slang"

float ClusterLocalExpansionForWorldRadius(
    RenderInstanceTransform transform,
    float worldExpansion)
{
    if (worldExpansion <= 0.0)
        return 0.0;

    float3 axisScale = abs(transform.Stretch) * abs(transform.Scale);
    float minAxisScale = max(min(min(axisScale.x, axisScale.y), axisScale.z), 1e-6);
    return worldExpansion / minAxisScale;
}

struct ClusterProjectedBounds
{
    float2 NdcMin;
    float2 NdcMax;
    float NearDepth;
    bool CrossesNearPlane;
};

void ClusterNodeWorldBounds(
    ClusterBVHNode node,
    RenderInstanceTransform transform,
    float boundsExpansion,
    out float3 worldMin,
    out float3 worldMax)
{
    float localExpansion = ClusterLocalExpansionForWorldRadius(
        transform,
        boundsExpansion);
    float3 localMin = node.BoundMin.xyz - localExpansion.xxx;
    float3 localMax = node.BoundMax.xyz + localExpansion.xxx;
    worldMin = 1e30.xxx;
    worldMax = -1e30.xxx;
    [unroll]
    for (uint corner = 0u; corner < 8u; corner++)
    {
        float3 local = float3(
            (corner & 1u) != 0u ? localMax.x : localMin.x,
            (corner & 2u) != 0u ? localMax.y : localMin.y,
            (corner & 4u) != 0u ? localMax.z : localMin.z);
        float3 world = TransformInstanceLocalPosition(local, transform);
        worldMin = min(worldMin, world);
        worldMax = max(worldMax, world);
    }
}

bool IsWorldAabbOutsideView(float3 worldMin, float3 worldMax, ClusterViewUniforms view)
{
    if (view.CullingRadius > 0.0)
    {
        float3 distance = max(max(worldMin - view.CameraPos, view.CameraPos - worldMax), 0.0.xxx);
        float radius = view.CullingRadius * 1.00001 + 1e-5;
        if (dot(distance, distance) > radius * radius) return true;
    }
    float3 center = (worldMin + worldMax) * 0.5;
    float3 extent = (worldMax - worldMin) * 0.5;
    float4 clip = mul(float4(center, 1.0), view.ViewProj);
    float4 magnitude = mul(float4(abs(center), 1.0), abs(view.ViewProj));
    float3 x = float3(view.ViewProj[0][0], view.ViewProj[1][0], view.ViewProj[2][0]);
    float3 y = float3(view.ViewProj[0][1], view.ViewProj[1][1], view.ViewProj[2][1]);
    float3 z = float3(view.ViewProj[0][2], view.ViewProj[1][2], view.ViewProj[2][2]);
    float3 w = float3(view.ViewProj[0][3], view.ViewProj[1][3], view.ViewProj[2][3]);
    float4 radius = float4(dot(extent, abs(w + x)), dot(extent, abs(w - x)),
        dot(extent, abs(w + y)), dot(extent, abs(w - y)));
    float4 distance = clip.wwww + float4(clip.x, -clip.x, clip.y, -clip.y);
    float4 rounding = (magnitude.wwww + magnitude.xxyy + radius + 1.0) * 9.5367431640625e-7;
    if (any(distance + radius < -rounding)) return true;
    float2 depthRadius = float2(dot(extent, abs(z)), dot(extent, abs(w - z)));
    float2 depthDistance = float2(clip.z, clip.w - clip.z);
    float2 depthRounding = (float2(magnitude.z, magnitude.w + magnitude.z) + depthRadius + 1.0) * 9.5367431640625e-7;
    return any(depthDistance + depthRadius < -depthRounding);
}

bool ProjectWorldAabb(
    float3 worldMin,
    float3 worldMax,
    ClusterViewUniforms view,
    out ClusterProjectedBounds projectedBounds)
{
    uint outsideLeft = 0u;
    uint outsideRight = 0u;
    uint outsideBottom = 0u;
    uint outsideTop = 0u;
    uint outsideNear = 0u;
    uint outsideFar = 0u;
    projectedBounds.NdcMin = float2(1e30, 1e30);
    projectedBounds.NdcMax = float2(-1e30, -1e30);
    projectedBounds.NearDepth = 1.0;
    projectedBounds.CrossesNearPlane = false;
    // Projection and perspective division are needed only after the cheap plane test.
    // Keep the original corner path for survivors and for explicitly disabled culling.
    if (view.CullingEnabled != 0u && IsWorldAabbOutsideView(worldMin, worldMax, view))
        return true;

    [unroll]
    for (uint corner = 0u; corner < 8u; corner++)
    {
        float3 world = float3(
            (corner & 1u) != 0u ? worldMax.x : worldMin.x,
            (corner & 2u) != 0u ? worldMax.y : worldMin.y,
            (corner & 4u) != 0u ? worldMax.z : worldMin.z);
        float4 clip = mul(float4(world, 1.0), view.ViewProj);
        if (clip.w <= 1e-5)
        {
            projectedBounds.CrossesNearPlane = true;
        }
        else
        {
            float3 ndc = clip.xyz / clip.w;
            projectedBounds.NdcMin = min(projectedBounds.NdcMin, ndc.xy);
            projectedBounds.NdcMax = max(projectedBounds.NdcMax, ndc.xy);
            projectedBounds.NearDepth = min(
                projectedBounds.NearDepth,
                saturate(ndc.z));
            if (clip.z < 0.0)
                projectedBounds.CrossesNearPlane = true;
        }

        outsideLeft += clip.x < -clip.w ? 1u : 0u;
        outsideRight += clip.x > clip.w ? 1u : 0u;
        outsideBottom += clip.y < -clip.w ? 1u : 0u;
        outsideTop += clip.y > clip.w ? 1u : 0u;
        outsideNear += clip.z < 0.0 ? 1u : 0u;
        outsideFar += clip.z > clip.w ? 1u : 0u;
    }

    return outsideLeft == 8u || outsideRight == 8u ||
        outsideBottom == 8u || outsideTop == 8u ||
        outsideNear == 8u || outsideFar == 8u;
}

// Reuse is local to one fixed world AABB; callers reset this data for each node.
struct ClusterBoundsProjection
{
    uint2 View;
    bool Outside;
    ClusterProjectedBounds Bounds;
};

bool ProjectWorldAabbForView(
    float3 worldMin, float3 worldMax, ClusterViewUniforms view,
    inout ClusterBoundsProjection previous,
    out ClusterProjectedBounds projectedBounds)
{
    if (view.CullingProjectionIndex == 0u)
        return ProjectWorldAabb(worldMin, worldMax, view, projectedBounds);
    uint2 projection = uint2(view.TargetPolicy, view.CullingProjectionIndex);
    if (any(previous.View != projection))
    {
        previous.Outside = ProjectWorldAabb(worldMin, worldMax, view, previous.Bounds);
        previous.View = projection;
    }
    projectedBounds = previous.Bounds;
    return previous.Outside;
}

bool ProjectClusterNode(
    ClusterBVHNode node,
    RenderInstanceTransform transform,
    float boundsExpansion,
    ClusterViewUniforms view,
    out ClusterProjectedBounds projectedBounds)
{
    float3 worldMin;
    float3 worldMax;
    ClusterNodeWorldBounds(
        node,
        transform,
        boundsExpansion,
        worldMin,
        worldMax);
    return ProjectWorldAabb(
        worldMin,
        worldMax,
        view,
        projectedBounds);
}

bool ClusterNodePassesLod(
    ClusterBVHNode node,
    RenderInstanceTransform transform,
    ClusterViewUniforms view)
{
    if (view.CullingEnabled == 0u)
        return true;
    float3 worldCenter = TransformInstanceLocalPosition(node.LODSphere.xyz, transform);
    float maxScale = abs(transform.Scale) *
        max(max(abs(transform.Stretch.x), abs(transform.Stretch.y)), abs(transform.Stretch.z));
    float worldRadius = node.LODSphere.w * maxScale;
    return ClusterSphereError(worldCenter, worldRadius, node.LODError * maxScale, view) > view.LodThreshold;
}

#endif
