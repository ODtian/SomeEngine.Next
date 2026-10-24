#include "generated/render_instance_layout.slang"
#include "cluster_shade_pipeline.slang"
#include "triangle_surface.slang"
#include "unlit_material.slang"

typedef WaveVertexEval VertexEvaluate;
#include "phong_tessellation.slang"
typedef PhongTessellation DomainEvaluate;
typedef OpaqueVisibilityEvaluate<VertexEvaluate.Vertex> VisibilityEvaluate;
typedef UnlitMaterial SurfaceEvaluate;

#define CLUSTER_MESHLET_RASTER 1
#define CLUSTER_SOFTWARE_MESHLET_RASTER 1
#define CLUSTER_SURFACE_MODES 1
#define CLUSTER_FORWARD_SHADING 1
#define CLUSTER_FORWARD_DEPTH_POSITION 1
#include "cluster_material_entries.slang"

#define TRANSPARENCY_MATERIAL_TYPE DeformedPhongTransparencyMaterial
#include "cluster_transparency_material.slang"

// Wave moves each vertex by at most .3 world units. Phong's edge control point
// moves at most half the undeformed world edge beyond its source triangle.
// Callers supply that sum as MeshInstance.BoundsExpansion (.66 for gallery parts).
#define CLUSTER_TESSELLATION_BOUNDS "finite"
#include "cluster_tessellation_entries.slang"
