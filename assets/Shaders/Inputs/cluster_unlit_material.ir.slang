import material_interfaces;
import unlit_material;

#define MATERIAL_INTERFACES_SLANG
#define DOMAIN_INTERFACES_SLANG
#define VISIBILITY_EVALUATE_SLANG
#define TRIANGLE_SURFACE_SLANG
#define VERTEX_EVALUATE_SLANG
#define VERTEX_EVALUATE_INTERFACES_SLANG
#define COMMON_SLANG
#define CLUSTER_STRUCTURES_SLANG
#define CLUSTER_HIERARCHY_SLANG
#define SURFACE_RECEIVER_SLANG
#define SURFACE_RECEIVER_DEPTH_SLANG
#define RENDER_INSTANCE_TRANSFORM_SLANG
#define SOMEENGINE_RENDER_INSTANCE_PROPERTIES_INCLUDED

typedef StaticVertexEval VertexEvaluate;
typedef LinearSurface DomainEvaluate;
typedef OpaqueVisibilityEvaluate<VertexEvaluate.Vertex> VisibilityEvaluate;
typedef UnlitMaterial SurfaceEvaluate;

#define CLUSTER_SURFACE_MODES 1
#define CLUSTER_FORWARD_SHADING 1
#define CLUSTER_FORWARD_DEPTH_POSITION 1
#include "../Pipeline/cluster_material.slang"

#define TRANSPARENCY_MATERIAL_TYPE UnlitTransparencyMaterial
#include "../cluster_transparency_material.slang"
