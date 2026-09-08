#include "cluster_shade_pipeline.slang"
#include "triangle_surface.slang"
#include "standard.slang"

typedef StaticVertexEval VertexEvaluate;
typedef LinearSurface DomainEvaluate;
typedef OpaquePixelEvaluate PixelEvaluate;
typedef StandardMaterial SurfaceEvaluate;

#include "cluster_material_entries.slang"
