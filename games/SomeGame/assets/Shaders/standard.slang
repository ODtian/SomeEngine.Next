#include "triangle_surface.slang"
#include "standard_material.slang"

typedef StaticVertexEval VertexEvaluate;
typedef LinearSurface DomainEvaluate;
typedef OpaqueVisibilityEvaluate<VertexEvaluate.Vertex> VisibilityEvaluate;
typedef StandardMaterial SurfaceEvaluate;

#include "Pipeline/cluster_material.slang"
