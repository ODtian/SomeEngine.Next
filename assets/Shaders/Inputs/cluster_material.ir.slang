import material_interfaces;
import standard_material;

#define MATERIAL_INTERFACES_SLANG
#define DOMAIN_INTERFACES_SLANG
#define VISIBILITY_EVALUATE_SLANG
#define TRIANGLE_SURFACE_SLANG
#define VERTEX_EVALUATE_SLANG
#define VERTEX_EVALUATE_INTERFACES_SLANG
#define COMMON_SLANG
#define RENDER_INSTANCE_TRANSFORM_SLANG
#define SOMEENGINE_RENDER_INSTANCE_PROPERTIES_INCLUDED

typedef StaticVertexEval VertexEvaluate;
typedef LinearSurface DomainEvaluate;
typedef OpaqueVisibilityEvaluate<VertexEvaluate.Vertex> VisibilityEvaluate;
typedef StandardMaterial SurfaceEvaluate;

#include "../Pipeline/cluster_material.slang"
