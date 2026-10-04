#include "generated/render_instance_layout.slang"
#include "cluster_shade_pipeline.slang"
#include "triangle_surface.slang"

float3 MaterialCameraPosition(uint viewIndex)
{
    return Views[viewIndex].CameraPos;
}

#include "standard_material.slang"

typedef StaticVertexEval VertexEvaluate;
typedef LinearSurface DomainEvaluate;
typedef OpaqueVisibilityEvaluate<VertexEvaluate.Vertex> VisibilityEvaluate;
typedef StandardMaterial SurfaceEvaluate;

typedef StaticPositionEval RasterVertexEvaluate;
typedef OpaqueVisibilityEvaluate<RasterVertexEvaluate.Vertex> RasterVisibilityEvaluate;
#define CLUSTER_POSITION_RASTER 1

#define CLUSTER_MESHLET_RASTER 1
#define CLUSTER_SOFTWARE_MESHLET_RASTER 1
#include "cluster_material_entries.slang"
