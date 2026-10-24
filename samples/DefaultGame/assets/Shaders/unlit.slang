#include "generated/render_instance_layout.slang"
#include "cluster_shade_pipeline.slang"
#include "triangle_surface.slang"
#include "unlit_material.slang"

typedef StaticVertexEval VertexEvaluate;
typedef LinearSurface DomainEvaluate;
typedef OpaqueVisibilityEvaluate<VertexEvaluate.Vertex> VisibilityEvaluate;
typedef UnlitMaterial SurfaceEvaluate;

#define CLUSTER_MESHLET_RASTER 1
#define CLUSTER_SURFACE_RECEIVERS 1
#define CLUSTER_SOFTWARE_MESHLET_RASTER 1
#define CLUSTER_SURFACE_VISIBILITY_DEPTH 1
#define CLUSTER_SURFACE_MODES 1
#define CLUSTER_FORWARD_SHADING 1
#include "cluster_material_entries.slang"

#define TRANSPARENCY_MATERIAL_TYPE UnlitTransparencyMaterial
#include "cluster_transparency_material.slang"
