#include "generated/render_instance_layout.slang"
#include "cluster_shade_pipeline.slang"
#include "triangle_surface.slang"
#include "vsm_sampling.slang"

float3 MaterialCameraPosition(uint viewIndex)
{
    return Views[viewIndex].CameraPos;
}

// This material explicitly consumes the always requested coarse directional page.
float SampleDirectionalFallback(
    StructuredBuffer<VirtualShadowMapParameters> views,
    StructuredBuffer<uint4> pageTable,
    Texture2D<uint> atlas,
    uint firstView,
    uint clipmapLevelCount,
    float dynamicResolutionLodBias,
    float3 worldPosition,
    float3 worldPositionDx,
    float3 worldPositionDy,
    uint blockerSampleCount,
    uint filterSampleCount,
    float blockerSearchRadiusTexels,
    float maxFilterRadiusTexels)
{
    if (clipmapLevelCount == 0u) return 1.0;
    VirtualShadowMapParameters view = views[firstView + clipmapLevelCount - 1u];
    return SampleVirtualShadowMapFiltered(view, pageTable, atlas, worldPosition,
        max(view.MipLevelCount, 1u) - 1u, blockerSampleCount, filterSampleCount,
        blockerSearchRadiusTexels, maxFilterRadiusTexels,
        CalculateVirtualShadowReceiverDepthGradient(view, worldPosition, worldPositionDx, worldPositionDy));
}

#define SampleDirectionalVirtualShadowMap SampleDirectionalFallback
#include "standard_material.slang"

typedef StaticVertexEval VertexEvaluate;
typedef LinearSurface DomainEvaluate;
typedef OpaqueVisibilityEvaluate<VertexEvaluate.Vertex> VisibilityEvaluate;
typedef StandardMaterial SurfaceEvaluate;

#define CLUSTER_MESHLET_RASTER 1
#define CLUSTER_SOFTWARE_MESHLET_RASTER 1
#include "cluster_material_entries.slang"
