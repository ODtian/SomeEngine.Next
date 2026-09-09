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
