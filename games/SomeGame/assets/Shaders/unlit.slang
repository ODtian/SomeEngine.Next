#include "triangle_surface.slang"
#include "unlit_material.slang"

typedef StaticVertexEval VertexEvaluate;
typedef LinearSurface DomainEvaluate;
typedef OpaqueVisibilityEvaluate<VertexEvaluate.Vertex> VisibilityEvaluate;
typedef UnlitMaterial SurfaceEvaluate;

#include "Pipeline/cluster_material.slang"
